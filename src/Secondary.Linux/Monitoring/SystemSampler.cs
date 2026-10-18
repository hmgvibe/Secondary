using System;
using System.Globalization;
using System.IO;
using Secondary.Core;

namespace Secondary.Linux.Monitoring;

// Whole-machine readings; rates are null until a second sample gives a baseline.
public sealed record SystemSample(DateTimeOffset Time, double? Cpu, ulong TotalMemory, ulong AvailableMemory,
    double? Download, double? Upload, string? SourceStatus = null);

// Reads /proc under an injectable root so tests can use fixture files.
public sealed class SystemSampler(string root = "/")
{
    private CpuTimes? previousCpu;
    private (long Received, long Sent, long Ticks)? previousNetwork;
    private string Proc(string name) => Path.Combine(root, "proc", name);

    public SystemSample Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var problems = new System.Collections.Generic.List<string>();
        double? cpu = null;
        try
        {
            var times = ReadCpu(File.ReadAllText(Proc("stat")));
            cpu = TelemetryMath.CpuPercent(previousCpu, times);
            previousCpu = times;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { previousCpu = null; problems.Add("CPU：" + error.Message); }
        ulong total = 0, available = 0;
        try { (total, available) = ReadMemory(File.ReadAllText(Proc("meminfo"))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { problems.Add("記憶體：" + error.Message); }
        double? down = null, up = null;
        try
        {
            var (received, sent) = ReadNetwork(File.ReadAllText(Proc("net/dev")));
            var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (previousNetwork is { } p)
            {
                var seconds = (ticks - p.Ticks) / (double)System.Diagnostics.Stopwatch.Frequency;
                down = TelemetryMath.Rate(p.Received, received, seconds);
                up = TelemetryMath.Rate(p.Sent, sent, seconds);
            }
            previousNetwork = (received, sent, ticks);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { previousNetwork = null; problems.Add("網路：" + error.Message); }
        return new(now, cpu, total, available, down, up, problems.Count > 0 ? string.Join("；", problems) : null);
    }

    // Matches the Windows meaning: Kernel time includes idle, and idle includes I/O wait.
    public static CpuTimes ReadCpu(string stat)
    {
        foreach (var line in stat.AsSpan().EnumerateLines())
        {
            if (!line.StartsWith("cpu ")) continue;
            var fields = line[4..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4) break;
            ulong Field(int index) => index < fields.Length ? ulong.Parse(fields[index], CultureInfo.InvariantCulture) : 0;
            var idle = Field(3) + Field(4);
            return new(idle, Field(2) + Field(5) + Field(6) + Field(7) + idle, Field(0) + Field(1));
        }
        throw new FormatException("/proc/stat 沒有整機 cpu 行");
    }
    public static (ulong Total, ulong Available) ReadMemory(string meminfo)
    {
        ulong? total = null, available = null;
        foreach (var line in meminfo.AsSpan().EnumerateLines())
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon];
            if (!name.SequenceEqual("MemTotal") && !name.SequenceEqual("MemAvailable")) continue;
            var value = line[(colon + 1)..].Trim();
            var kib = ulong.Parse(value.EndsWith("kB") ? value[..^2].Trim() : value, CultureInfo.InvariantCulture) * 1024;
            if (name.SequenceEqual("MemTotal")) total = kib; else available = kib;
        }
        if (total is not > 0 || available is null || available > total) throw new FormatException("/proc/meminfo 缺少 MemTotal 或 MemAvailable");
        return (total.Value, available.Value);
    }
    // Sums every interface except loopback, like the Windows sampler's physical-and-virtual total.
    public static (long Received, long Sent) ReadNetwork(string dev)
    {
        long received = 0, sent = 0;
        var found = false;
        foreach (var line in dev.AsSpan().EnumerateLines())
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name.SequenceEqual("lo")) { found = true; continue; }
            var fields = line[(colon + 1)..].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9) continue;
            received += long.Parse(fields[0], CultureInfo.InvariantCulture);
            sent += long.Parse(fields[8], CultureInfo.InvariantCulture);
            found = true;
        }
        if (!found) throw new FormatException("/proc/net/dev 沒有網路介面");
        return (received, sent);
    }
}
