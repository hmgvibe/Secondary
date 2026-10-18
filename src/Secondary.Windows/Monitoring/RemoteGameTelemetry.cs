using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using Secondary.Shared.Connection;

namespace Secondary.Monitoring;

// Converts between the game page's snapshot and the connection's wire snapshot, on the helper and on a Windows device.
public static class RemoteGameMapping
{
    private static readonly FrameStatistics NoFrames = new(null, 0, null, null, null, false);
    private static string Clip(string text) => text.Length > 500 ? text[..500] : text;

    public static RemoteGameSnapshot ToRemote(GameTelemetrySnapshot s, long session, bool high) => new(session, s.System?.Time ?? DateTimeOffset.UtcNow, high,
        s.System?.Cpu, s.System?.TotalMemory ?? 0, s.System?.AvailableMemory ?? 0, s.System?.Download, s.System?.Upload,
        s.SystemState, s.CpuState, s.MemoryState, s.NetworkState, Clip(s.SystemStatus),
        s.Gpu, s.GpuState, Clip(s.GpuStatus),
        RemoteGame.CompactSensors(s.Sensors?.Readings ?? []), s.SensorState, Clip(s.SensorStatus),
        s.Frames, s.FrameState, Clip(s.FrameStatus), s.TargetExited);

    // A missing or stale snapshot shows no values: every source reports why instead.
    public static GameTelemetrySnapshot FromRemote(RemoteGameView view)
    {
        if (view.Snapshot is not { } s || view.Stale)
        {
            var state = view.Stale ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            return new(null, null, view.Status, null, view.Status, NoFrames, view.Status, false, view.Status, state, state, state, state,
                CpuState: state, MemoryState: state, NetworkState: state);
        }
        var system = s.TotalMemory > 0 || s.Cpu.HasValue || s.Download.HasValue
            ? new SystemSample(s.Cpu, s.TotalMemory, s.AvailableMemory, s.Download, s.Upload, null, "", "", "", s.Time,
                CpuState: s.CpuState, MemoryState: s.MemoryState, NetworkState: s.NetworkState, SourceStatus: s.SystemStatus) : null;
        return new(system, s.Gpu, s.GpuStatus, s.Sensors.Length > 0 ? new SensorSnapshot(1, s.Sensors) : null, s.SensorStatus,
            s.Frames, s.FrameStatus, s.TargetExited, s.SystemStatus, s.SystemState, s.GpuState, s.SensorState, s.FrameState,
            CpuState: s.CpuState, MemoryState: s.MemoryState, NetworkState: s.NetworkState);
    }
}

// The main computer's game telemetry on a Windows device; the game page reads it like local sampling.
public sealed class RemoteGameTelemetry(ConnectionController controller, bool high, bool sensors, GameTarget? target) : IGameTelemetry
{
    private readonly RemoteGameClient client = new(controller, high, sensors, target is null ? null : new RemoteProcess(target.Id, target.StartedUtcTicks, target.Name));
    public RemoteGameView LastView { get; private set; } = new(null, false, "等待主電腦", "主電腦");
    public GameTelemetrySnapshot Read() => RemoteGameMapping.FromRemote(LastView = client.Read());
    public Task RunAsync(CancellationToken token) => client.RunAsync(token);
}

public static class ProcessCatalog
{
    // Running processes this user can read, by name; the caller's own process is left out.
    public static GameTarget[] List() => Process.GetProcesses().Select(process =>
    {
        using (process)
        {
            try { return !process.HasExited && process.Id != Environment.ProcessId
                ? new GameTarget(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName) : null; }
            catch (Exception) { return null; }
        }
    }).Where(p => p is not null).Cast<GameTarget>().OrderBy(p => p.Name).ThenBy(p => p.Id).Take(RemoteGame.MaxProcesses).ToArray();
}
