using System.Text.Json;
using System.Text.Json.Serialization;
using Secondary.App.Monitoring;
using Secondary.Core;
using Secondary.Diagnostics;

// Explicit one-shot diagnostics only. Never writes user settings or starts a service.
if (args.Length != 0 && (args.Length != 3 || args[0] != "--presentmon" || !int.TryParse(args[2], out var id) || id <= 0))
{
    Console.Error.WriteLine("Usage: Secondary.Diagnostics [--presentmon <official 2.6.0 exe> <PID>]");
    return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    using var sampler = new WindowsSampler();
    sampler.Read(false);
    await Task.Delay(2200, cancel.Token);
    var sample = sampler.Read(false);
    var readings = new List<MonitoringReading>();
    void Add(string metric, string source, double? value, string unit, string? reason = null) =>
        readings.Add(new(metric, source, "system", sample.Time, value, unit,
            value.HasValue ? ReadingState.Available : ReadingState.Unavailable, value.HasValue ? null : reason));
    Add("CPU usage", "GetSystemTimes", sample.Cpu, "%", "CPU counter baseline unavailable");
    Add("Memory used", "GlobalMemoryStatusEx", sample.TotalMemory > 0 && sample.AvailableMemory <= sample.TotalMemory
        ? sample.TotalMemory - sample.AvailableMemory : null, "bytes", "Memory query unavailable");
    Add("Network receive", "NetworkInterface", sample.Download, "bytes/s", "No valid active adapter baseline");
    Add("Network send", "NetworkInterface", sample.Upload, "bytes/s", "No valid active adapter baseline");
    Add("GPU busiest engine (all adapters)", "PDH GPU Engine", sample.Gpu, "%", sample.GpuStatus);
    readings.Add(new("Temperature / clock / power / VRAM", "LibreHardwareMonitor 0.9.6", "not selected",
        sample.Time, null, "", ReadingState.Disabled, "Optional hardware helper not enabled; no driver installed or elevation requested"));
    var frames = args.Length == 3 ? await PresentMonProbe.Run(args[1], int.Parse(args[2]), cancel.Token) : null;
    Console.WriteLine(JsonSerializer.Serialize(new { Schema = 1, Readings = readings, PresentMon = frames,
        Note = "One-shot source diagnostics, not game HUD or an FPS benchmark" },
        new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Diagnostics cancelled."); return 130; }
