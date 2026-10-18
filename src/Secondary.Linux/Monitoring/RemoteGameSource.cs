using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using Secondary.Shared.Connection;

namespace Secondary.Linux.Monitoring;

// The game page reads one of these once a second: this computer's samplers, or the main computer's in connection mode.
public interface IGameSource
{
    GameSnapshot Read();
    Task RunAsync(CancellationToken token);
}

// The main computer's game telemetry on a Linux device. Windows sensor readings are renamed to the
// identities the Linux game page reads (/gpu-…/load, /cpu/0/temperature and so on).
public sealed class RemoteGameSource(ConnectionController controller, bool high, bool sensors, RemoteProcess? target) : IGameSource
{
    public const string FrameSourceName = "主電腦 PresentMon";
    private static readonly FrameStatistics NoFrames = new(null, 0, null, null, null, false);
    private readonly RemoteGameClient client = new(controller, high, sensors, target);
    private string? swapChain;
    private int generation;
    public RemoteGameView LastView { get; private set; } = new(null, false, "等待主電腦", "主電腦");
    public bool TargetExited => LastView is { Stale: false, Snapshot.TargetExited: true };
    public Task RunAsync(CancellationToken token) => client.RunAsync(token);

    public GameSnapshot Read()
    {
        var view = LastView = client.Read();
        if (view.Snapshot is not { } s || view.Stale)
        {
            var state = view.Stale ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            return new(null, state, state, state, view.Status, [], state, view.Status, NoFrames, state, view.Status, FrameSourceName, true, generation);
        }
        // A new swap chain starts a new frame-time trace, as on Windows.
        if (s.Frames.SwapChain is { } chain && chain != swapChain) { swapChain = chain; generation++; }
        var system = s.TotalMemory > 0 || s.Cpu.HasValue || s.Download.HasValue
            ? new SystemSample(s.Time, s.Cpu, s.TotalMemory, s.AvailableMemory, s.Download, s.Upload, s.SystemStatus) : null;
        var hardware = Translate(s);
        var hardwareState = s.SensorState == TelemetrySourceState.Disabled && hardware.Length > 0 ? s.GpuState : s.SensorState;
        return new(system, s.CpuState, s.MemoryState, s.NetworkState, s.SystemStatus, hardware, hardwareState, s.SensorStatus,
            s.Frames, s.FrameState, s.FrameStatus, FrameSourceName, true, generation);
    }

    public static MonitoringReading[] Translate(RemoteGameSnapshot s)
    {
        var result = new List<MonitoringReading>();
        MonitoringReading Renamed(MonitoringReading r, string root, string name, string key) => r with { Identity = root + "/" + key, Metric = name + " / " + key };
        var gpus = s.Sensors.Where(r => r.Identity.StartsWith("/gpu-", StringComparison.Ordinal))
            .GroupBy(r => string.Join('/', r.Identity.Split('/').Take(3))).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var gpu in gpus)
        {
            var name = gpu.First().Metric.Split(" / ")[0];
            MonitoringReading? Find(string metric, string unit) => gpu.FirstOrDefault(r => r.Unit == unit && r.Metric.EndsWith(" / " + metric, StringComparison.Ordinal));
            foreach (var (metric, unit, key) in new[] { ("GPU Core", "%", "load"), ("GPU Core", "°C", "temperature"), ("GPU Core", "MHz", "clock"),
                ("GPU Package", "W", "power"), ("GPU Memory Used", "MB", "memory-used"), ("GPU Memory Total", "MB", "memory-total") })
                if (Find(metric, unit) is { } reading) result.Add(Renamed(reading, gpu.Key, name, key));
        }
        // Without sensor readings the helper still measures the busiest engine across all GPUs.
        if (result.Count == 0)
        {
            const string name = "主電腦 GPU（所有 GPU 最忙引擎）";
            result.Add(new MonitoringReading(name + " / load", "主電腦", "/gpu-main/0/load", s.Time, s.Gpu, "%",
                s.Gpu.HasValue ? ReadingState.Available : ReadingState.Unavailable, s.Gpu.HasValue ? null : s.GpuStatus));
            result.Add(new MonitoringReading(name + " / memory-used", "主電腦", "/gpu-main/0/memory-used", s.Time, null, "MB", ReadingState.Unavailable,
                "溫度、時脈、功耗與顯存需要開啟「主電腦硬體感測」"));
        }
        foreach (var reading in s.Sensors.Where(r => r.Identity.StartsWith("/intelcpu/", StringComparison.Ordinal) || r.Identity.StartsWith("/amdcpu/", StringComparison.Ordinal)))
        {
            var key = reading.Unit switch { "°C" => "temperature", "MHz" => "clock", "W" => "power", _ => null };
            if (key is not null && result.All(r => r.Identity != "/cpu/0/" + key)) result.Add(Renamed(reading, "/cpu/0", "CPU", key));
        }
        if (s.Sensors.FirstOrDefault(r => r.Identity == "cpu/pawnio") is { Reason: { } note } && result.All(r => r.Identity != "/cpu/0/power"))
            result.Add(new MonitoringReading("CPU / power", "主電腦", "/cpu/0/power", s.Time, null, "W", ReadingState.Unavailable, note));
        return result.ToArray();
    }
}
