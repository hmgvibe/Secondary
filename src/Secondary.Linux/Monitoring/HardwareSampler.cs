using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Secondary.Core;

namespace Secondary.Linux.Monitoring;

// CPU and GPU sensors from sysfs, which any user can read without a helper.
// Identities follow the Windows sensor naming ("/gpu-<vendor>/<n>/...", "<GPU name> / GPU Core")
// so the dashboard groups and labels them the same way.
public sealed class HardwareSampler(string root = "/", Func<double>? clock = null, NvidiaSource? nvidia = null) : IDisposable
{
    private readonly Func<double> clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
    private readonly Dictionary<string, (double Value, double Seconds)> counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly DrmUsage drm = new(root);
    private NvidiaSource? nvidia = nvidia;
    private bool nvidiaTried = nvidia is not null;
    private string Sys(params string[] parts) => Path.Combine([root, "sys", .. parts]);

    public MonitoringReading[] Sample()
    {
        var time = DateTimeOffset.UtcNow;
        var readings = new List<MonitoringReading>();
        // A reason explains a missing value; a note qualifies a present one.
        void Add(string identity, string metric, double? value, string unit, string? reason = null, string? note = null) =>
            readings.Add(new(metric, "sysfs", identity, time, value, unit, value.HasValue ? ReadingState.Available : ReadingState.Unavailable, value.HasValue ? note : reason ?? "此裝置未提供"));
        Add("/cpu/0/temperature", "CPU / Temperature", CpuTemperature(), "°C", "找不到 coretemp／k10temp 感測器");
        Add("/cpu/0/clock", "CPU / Clock", CpuClock(), "MHz", "找不到 cpufreq");
        var (power, powerReason) = CpuPower();
        Add("/cpu/0/power", "CPU / Package", power, "W", powerReason);
        var cards = Cards().ToArray();
        var usage = cards.Length > 0 ? drm.Sample(clock()) : [];
        foreach (var card in cards) SampleGpu(card, usage, Add);
        return readings.ToArray();
    }

    private IEnumerable<string> Cards()
    {
        var drm = Sys("class", "drm");
        if (!Directory.Exists(drm)) return [];
        return Directory.EnumerateFileSystemEntries(drm)
            .Where(p => Path.GetFileName(p) is var n && n.StartsWith("card", StringComparison.Ordinal) && n.Length > 4 && n[4..].All(char.IsAsciiDigit))
            .Where(p => File.Exists(Path.Combine(p, "device", "vendor")))
            .OrderBy(p => int.Parse(Path.GetFileName(p)[4..], CultureInfo.InvariantCulture));
    }

    private delegate void Adder(string identity, string metric, double? value, string unit, string? reason = null, string? note = null);
    private void SampleGpu(string card, Dictionary<string, double?> usage, Adder add)
    {
        var device = Path.Combine(card, "device");
        var vendorId = Text(Path.Combine(device, "vendor"))?.ToLowerInvariant();
        var deviceId = Text(Path.Combine(device, "device"))?.ToLowerInvariant();
        var vendor = vendorId switch { "0x1002" => "amd", "0x8086" => "intel", "0x10de" => "nvidia", _ => "gpu" };
        var index = Path.GetFileName(card)[4..];
        var id = $"/gpu-{vendor}/{index}";
        var name = Name(vendorId, deviceId, index);
        var hwmon = HwmonFolders(Path.Combine(device, "hwmon")).FirstOrDefault();
        double? load = null, temperature = null, clock = null, power = null, used = null, total = null;
        string? loadReason = null, loadNote = null;
        var nvml = vendor == "nvidia" && Driver(device) == "nvidia" ? Nvidia()?.Read(Slot(device)) : null;
        if (nvml is { } n) (load, temperature, clock, power, used, total) = (n.Load, n.Temperature, n.Clock, n.Power, n.MemoryUsed, n.MemoryTotal);
        else if (vendor == "nvidia") loadReason = Driver(device) == "nvidia" ? "無法載入 NVML（libnvidia-ml.so.1）" : "nouveau 驅動未提供使用率";
        if (vendor == "amd")
        {
            load = Number(Path.Combine(device, "gpu_busy_percent"));
            used = Number(Path.Combine(device, "mem_info_vram_used")) / 1048576;
            total = Number(Path.Combine(device, "mem_info_vram_total")) / 1048576;
        }
        // Without a device-wide counter, the busiest engine across DRM clients is the usage.
        if (load is null && Slot(device) is { } slot && usage.TryGetValue(slot, out var busiest))
        {
            load = busiest;
            loadNote = "DRM 用戶端最忙引擎";
            loadReason = "等待第二次採樣";
        }
        else if (vendor == "intel")
        {
            // Older kernels without fdinfo engine times: time outside the RC6 sleep state is the closest measure.
            var residency = Number(Path.Combine(card, "gt", "gt0", "rc6_residency_ms")) ?? Number(Path.Combine(card, "power", "rc6_residency_ms"));
            if (residency is { } ms && Rate(card + "/rc6", ms / 1000) is { } idle) { load = Math.Clamp(100 * (1 - idle), 0, 100); loadNote = "以非 RC6 省電時間估算，高於實際使用率"; }
            loadReason = residency is null ? "Intel 驅動未提供使用率" : "等待第二次採樣";
        }
        if (vendor == "intel") clock = Number(Path.Combine(card, "gt_act_freq_mhz")) ?? Number(Path.Combine(device, "tile0", "gt0", "freq0", "act_freq"));
        if (hwmon is not null)
        {
            // temp1 is the edge sensor on amdgpu and the only one on most other drivers.
            temperature ??= Number(Path.Combine(hwmon, "temp1_input")) is { } t and > 0 and < 150000 ? t / 1000 : null;
            clock ??= Number(Path.Combine(hwmon, "freq1_input")) / 1e6;
            power ??= (Number(Path.Combine(hwmon, "power1_average")) ?? Number(Path.Combine(hwmon, "power1_input"))) / 1e6
                ?? (Number(Path.Combine(hwmon, "energy1_input")) is { } energy ? Rate(card + "/energy", energy / 1e6) : null);
        }
        add(id + "/load", name + " / GPU Core", load, "%", loadReason, loadNote);
        var integrated = vendor == "intel" && hwmon is null;
        add(id + "/temperature", name + " / GPU Core", temperature, "°C", integrated ? "內建顯示卡沒有獨立溫度感測器" : null);
        add(id + "/clock", name + " / GPU Core", clock, "MHz");
        add(id + "/power", name + " / GPU Package", power, "W", integrated ? "內建顯示卡功耗包含在 CPU 封裝內" : null);
        add(id + "/memory-used", name + " / GPU Memory Used", used, "MB", integrated ? "內建顯示卡使用共用系統記憶體" : null);
        add(id + "/memory-total", name + " / GPU Memory Total", total, "MB", integrated ? "內建顯示卡使用共用系統記憶體" : null);
    }

    private double? CpuTemperature()
    {
        var hwmon = HwmonFolders(Sys("class", "hwmon")).Where(h => Text(Path.Combine(h, "name")) is "coretemp" or "k10temp" or "zenpower" or "cpu_thermal").ToArray();
        var values = hwmon.SelectMany(Temperatures).ToArray();
        if (values.Length > 0) return values.Max();
        var zones = Sys("class", "thermal");
        if (!Directory.Exists(zones)) return null;
        var package = Directory.EnumerateFileSystemEntries(zones, "thermal_zone*")
            .FirstOrDefault(z => Text(Path.Combine(z, "type")) is "x86_pkg_temp" or "cpu-thermal");
        return package is null ? null : Number(Path.Combine(package, "temp")) / 1000;
    }
    private double? CpuClock()
    {
        var cpus = Sys("devices", "system", "cpu");
        if (!Directory.Exists(cpus)) return null;
        var values = Directory.EnumerateFileSystemEntries(cpus, "cpu*")
            .Where(c => Path.GetFileName(c)[3..].All(char.IsAsciiDigit) && Path.GetFileName(c).Length > 3)
            .Select(c => Number(Path.Combine(c, "cpufreq", "scaling_cur_freq"))).Where(v => v.HasValue).Select(v => v!.Value / 1000).ToArray();
        return values.Length > 0 ? values.Max() : null;
    }
    private (double? Watts, string Reason) CpuPower()
    {
        var zone = Sys("class", "powercap", "intel-rapl:0");
        var file = Path.Combine(zone, "energy_uj");
        if (!File.Exists(file)) return (null, "找不到 RAPL 功耗介面");
        try
        {
            var energy = double.Parse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture) / 1e6;
            var range = Number(Path.Combine(zone, "max_energy_range_uj")) / 1e6;
            return (Rate("rapl", energy, range), "等待第二次採樣");
        }
        catch (UnauthorizedAccessException) { return (null, "RAPL 功耗需要 root 權限（Linux 預設只讓 root 讀取）"); }
        catch (Exception error) when (error is IOException or FormatException) { return (null, "RAPL 讀取失敗：" + error.Message); }
    }

    // Per-second change of a cumulative counter, allowing for one wrap at the given range.
    private double? Rate(string key, double value, double? range = null)
    {
        var now = clock();
        counters.TryGetValue(key, out var previous);
        counters[key] = (value, now);
        if (previous.Seconds <= 0 || now <= previous.Seconds) return null;
        var delta = value - previous.Value;
        if (delta < 0 && range > 0) delta += range.Value;
        return delta < 0 ? null : delta / (now - previous.Seconds);
    }

    private static IEnumerable<string> HwmonFolders(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder, "hwmon*").Order(StringComparer.Ordinal) : [];
    private static IEnumerable<double> Temperatures(string hwmon) =>
        Directory.EnumerateFiles(hwmon, "temp*_input").Select(Number).Where(v => v is > 0 and < 150000).Select(v => v!.Value / 1000);
    private static string? Text(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    private static double? Number(string path) =>
        double.TryParse(Text(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    private static string? Driver(string device) =>
        Path.GetFileName(new FileInfo(Path.Combine(device, "driver")).LinkTarget) is { Length: > 0 } linked ? linked
        : Text(Path.Combine(device, "uevent"))?.Split('\n').FirstOrDefault(l => l.StartsWith("DRIVER=", StringComparison.Ordinal))?[7..];
    private static string? Slot(string device) =>
        Text(Path.Combine(device, "uevent"))?.Split('\n').FirstOrDefault(l => l.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))?[14..];
    private NvidiaSource? Nvidia()
    {
        if (!nvidiaTried) { nvidiaTried = true; nvidia = NvidiaSource.TryLoad(); }
        return nvidia;
    }

    private string Name(string? vendor, string? device, string index)
    {
        var key = vendor + ":" + device;
        if (names.TryGetValue(key, out var cached)) return cached;
        var brand = vendor switch { "0x1002" => "AMD", "0x8086" => "Intel", "0x10de" => "NVIDIA", _ => "GPU" };
        var found = PciName(vendor, device);
        // AMD and NVIDIA list the marketing name in brackets: "Navi 23 [Radeon RX 6600/6600 XT/6600M]".
        if (found is not null && found.LastIndexOf('[') is var open and >= 0 && found.EndsWith(']')) found = found[(open + 1)..^1];
        return names[key] = found is null ? brand + " GPU " + index : brand + " " + found;
    }
    private string? PciName(string? vendor, string? device)
    {
        if (vendor is not { Length: 6 } || device is not { Length: 6 }) return null;
        var (v, d) = (vendor[2..], device[2..]);
        foreach (var file in new[] { Path.Combine(root, "usr", "share", "hwdata", "pci.ids"), Path.Combine(root, "usr", "share", "misc", "pci.ids") })
        {
            if (!File.Exists(file)) continue;
            try
            {
                var inVendor = false;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line[0] != '\t') { if (inVendor) return null; inVendor = line.StartsWith(v, StringComparison.OrdinalIgnoreCase) && line.Length > 4 && line[4] == ' '; continue; }
                    if (inVendor && line.Length > 6 && line[1] != '\t' && line.AsSpan(1, 4).Equals(d, StringComparison.OrdinalIgnoreCase)) return line[5..].Trim();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
    public void Dispose() => nvidia?.Dispose();
}
