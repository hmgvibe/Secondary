using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Secondary.Linux.Monitoring;

// GPU engine busy time from DRM client fdinfo (/proc/<pid>/fdinfo), the unprivileged source gputop and nvtop use.
// Only the user's own processes are readable, which covers the desktop and games they start.
// The result per PCI device is the busiest engine, matching the Windows "busiest engine" reading.
public sealed class DrmUsage(string root = "/")
{
    private sealed record Client(string Device, Dictionary<string, double> Busy, Dictionary<string, double> Cycles, Dictionary<string, double> TotalCycles, Dictionary<string, double> Capacity);
    private Dictionary<(string Device, string Id), Client> previous = [];
    private double previousSeconds;

    // Devices with at least one client reporting engines map to a percentage, or null until a second sample.
    public Dictionary<string, double?> Sample(double seconds)
    {
        var current = Read();
        var result = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var elapsed = seconds - previousSeconds;
        var engines = new Dictionary<(string Device, string Engine), double>();
        foreach (var (key, client) in current)
        {
            result.TryAdd(client.Device, null);
            if (!previous.TryGetValue(key, out var before) || elapsed <= 0) continue;
            foreach (var (engine, busy) in client.Busy)
            {
                if (!before.Busy.TryGetValue(engine, out var old) || busy < old) continue;
                var capacity = client.Capacity.GetValueOrDefault(engine, 1);
                engines[(client.Device, engine)] = engines.GetValueOrDefault((client.Device, engine)) + (busy - old) / (elapsed * 1e9 * Math.Max(1, capacity));
            }
            // The xe driver counts GPU cycles against a shared GPU timestamp instead of nanoseconds.
            foreach (var (engine, cycles) in client.Cycles)
            {
                if (!before.Cycles.TryGetValue(engine, out var oldCycles) || !client.TotalCycles.TryGetValue(engine, out var total) ||
                    !before.TotalCycles.TryGetValue(engine, out var oldTotal) || total <= oldTotal || cycles < oldCycles) continue;
                var capacity = client.Capacity.GetValueOrDefault(engine, 1);
                engines[(client.Device, engine)] = engines.GetValueOrDefault((client.Device, engine)) + (cycles - oldCycles) / ((total - oldTotal) * Math.Max(1, capacity));
            }
        }
        foreach (var device in result.Keys.ToArray())
            if (previousSeconds > 0 && elapsed > 0)
                result[device] = Math.Clamp(100 * engines.Where(e => e.Key.Device.Equals(device, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value).DefaultIfEmpty(0).Max(), 0, 100);
        previous = current;
        previousSeconds = seconds;
        return result;
    }

    private Dictionary<(string Device, string Id), Client> Read()
    {
        var clients = new Dictionary<(string, string), Client>();
        var proc = Path.Combine(root, "proc");
        if (!Directory.Exists(proc)) return clients;
        foreach (var process in Directory.EnumerateDirectories(proc))
        {
            if (!Path.GetFileName(process).All(char.IsAsciiDigit)) continue;
            IEnumerable<string> descriptors;
            try { descriptors = Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var descriptor in descriptors)
            {
                try
                {
                    if (new FileInfo(descriptor).LinkTarget is not { } target || !target.StartsWith("/dev/dri/", StringComparison.Ordinal)) continue;
                    if (Parse(File.ReadAllText(Path.Combine(process, "fdinfo", Path.GetFileName(descriptor)))) is { } parsed)
                        clients.TryAdd((parsed.Client.Device, parsed.Id), parsed.Client);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        return clients;
    }

    private static (string Id, Client Client)? Parse(string fdinfo)
    {
        string? device = null, id = null;
        var busy = new Dictionary<string, double>(StringComparer.Ordinal);
        var cycles = new Dictionary<string, double>(StringComparer.Ordinal);
        var total = new Dictionary<string, double>(StringComparer.Ordinal);
        var capacity = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in fdinfo.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key == "drm-pdev") device = value;
            else if (key == "drm-client-id") id = value;
            else if (!key.StartsWith("drm-", StringComparison.Ordinal)) continue;
            var number = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) continue;
            if (key.StartsWith("drm-engine-capacity-", StringComparison.Ordinal)) capacity[key[20..]] = amount;
            else if (key.StartsWith("drm-engine-", StringComparison.Ordinal) && value.EndsWith("ns", StringComparison.Ordinal)) busy[key[11..]] = amount;
            else if (key.StartsWith("drm-total-cycles-", StringComparison.Ordinal)) total[key[17..]] = amount;
            else if (key.StartsWith("drm-cycles-", StringComparison.Ordinal)) cycles[key[11..]] = amount;
        }
        return device is null || id is null || busy.Count + cycles.Count == 0 ? null : (id, new Client(device, busy, cycles, total, capacity));
    }
}
