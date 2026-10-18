using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.Linux.Monitoring;

public sealed record GameSnapshot(
    SystemSample? System, TelemetrySourceState CpuState, TelemetrySourceState MemoryState, TelemetrySourceState NetworkState, string SystemStatus,
    MonitoringReading[] Hardware, TelemetrySourceState HardwareState, string HardwareStatus,
    FrameStatistics Frames, TelemetrySourceState FrameState, string FrameStatus, string? FrameSource, bool? PerFrame, int FrameGeneration);

// Runs the samplers on worker tasks at the shared sampling policy; the UI reads one snapshot per second.
// Each source fails, goes stale and recovers on its own, as in the Windows session.
public sealed class GameTelemetrySession(bool high, SystemSampler system, HardwareSampler hardware, MangoHudLog frames) : IGameSource
{
    private static readonly FrameStatistics NoFrames = new(null, 0, null, null, null, false);
    private readonly object gate = new();
    private SystemSample? systemSample;
    private MonitoringReading[] readings = [];
    private FrameStatistics frameStatistics = NoFrames;
    private long lastSystem, lastHardware, lastFrame;
    private string? systemFailure, hardwareFailure, frameFailure, frameSource;
    private bool? perFrame;
    private bool hasHeader, stopped;
    private int frameGeneration;
    public int SystemIntervalMs => SamplingPolicy.IntervalMs(true, high);
    public int HardwareIntervalMs => SamplingPolicy.GpuIntervalSeconds(true, high) * 1000;
    public int FrameIntervalMs => high ? 500 : 1000;
    public string MangoHudFolder => frames.Folder;

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.WhenAll(
                Loop(SystemIntervalMs, () => { var value = system.Sample(); lock (gate) { systemSample = value; lastSystem = Stopwatch.GetTimestamp(); systemFailure = null; } },
                    message => { systemSample = null; systemFailure = "系統採樣失敗：" + message; }, token),
                Loop(HardwareIntervalMs, () => { var value = hardware.Sample(); lock (gate) { readings = value; lastHardware = Stopwatch.GetTimestamp(); hardwareFailure = null; } },
                    message => { readings = []; hardwareFailure = "硬體採樣失敗：" + message; }, token),
                Loop(FrameIntervalMs, PollFrames, message => { frameStatistics = NoFrames; frameFailure = "MangoHud 記錄讀取失敗：" + message; }, token)).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) stopped = true;
            hardware.Dispose();
        }
    }

    private void PollFrames()
    {
        var added = frames.Poll(TimeSpan.FromSeconds(30));
        var statistics = frames.Window.Read();
        lock (gate)
        {
            if (frameGeneration != frames.Generation) { frameGeneration = frames.Generation; lastFrame = 0; }
            if (added > 0) lastFrame = Stopwatch.GetTimestamp();
            frameStatistics = statistics; frameSource = frames.Source; perFrame = frames.PerFrame; hasHeader = frames.HasHeader; frameFailure = null;
        }
    }

    // One failure does not end the loop; the next interval retries, so a transient read error recovers.
    private async Task Loop(int intervalMs, Action sample, Action<string> fail, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { sample(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lock (gate) fail(error.Message);
                AppDiagnostics.Write("game-sampler", error);
            }
            try { await Task.Delay(intervalMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public GameSnapshot Read()
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            bool Fresh(long last, int intervalMs) => last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency <= intervalMs * 2.5 + 500;
            TelemetrySourceState State(bool fresh, long last, string? failure) => stopped ? TelemetrySourceState.Stopped :
                failure is not null ? TelemetrySourceState.Failed : fresh ? TelemetrySourceState.Available :
                last != 0 ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            var systemFresh = Fresh(lastSystem, SystemIntervalMs);
            var basic = State(systemFresh, lastSystem, systemFailure);
            var current = systemFresh ? systemSample : null;
            // The first sample has no CPU or network baseline yet: that is warming up, not a failure.
            var cpu = basic != TelemetrySourceState.Available ? basic : current?.Cpu is not null ? TelemetrySourceState.Available : current?.SourceStatus?.Contains("CPU", StringComparison.Ordinal) == true ? TelemetrySourceState.Failed : TelemetrySourceState.Waiting;
            var memory = basic != TelemetrySourceState.Available ? basic : current is { TotalMemory: > 0 } ? TelemetrySourceState.Available : TelemetrySourceState.Failed;
            var network = basic != TelemetrySourceState.Available ? basic : current?.Download is not null ? TelemetrySourceState.Available : current?.SourceStatus?.Contains("網路", StringComparison.Ordinal) == true ? TelemetrySourceState.Failed : TelemetrySourceState.Waiting;
            var systemStatus = stopped ? "系統採樣已停止" : systemFailure ?? (basic == TelemetrySourceState.Stale ? "系統資料已過期，等待重新採樣" : current?.SourceStatus ?? (current is null ? "等待系統採樣" : "整機採樣 / /proc 持續更新"));
            var hardwareFresh = Fresh(lastHardware, HardwareIntervalMs);
            var hardwareState = State(hardwareFresh, lastHardware, hardwareFailure);
            var hardwareStatus = stopped ? "硬體採樣已停止" : hardwareFailure ?? (hardwareState == TelemetrySourceState.Stale ? "硬體資料已過期，等待重新採樣" : lastHardware == 0 ? "等待硬體採樣" : "sysfs 感測器 / 持續採樣");
            var frameFresh = lastFrame != 0 && (now - lastFrame) * 1000.0 / Stopwatch.Frequency <= Math.Max(2000, FrameIntervalMs * 2.5);
            var frameState = stopped ? TelemetrySourceState.Stopped : frameFailure is not null ? TelemetrySourceState.Failed :
                frameFresh ? TelemetrySourceState.Available : lastFrame != 0 ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            var frameStatus = stopped ? "幀率讀取已停止" : frameFailure ?? frameState switch
            {
                TelemetrySourceState.Available => "MangoHud · " + frameSource + (perFrame == false ? " · 記錄不是逐幀，1% Low 不可用；請設定 log_interval=0" : ""),
                TelemetrySourceState.Stale => "MangoHud 記錄已停止更新：" + frameSource + "（遊戲可能已結束或暫停）",
                _ when frameSource is not null && !hasHeader => "已找到 " + frameSource + " 的記錄，等待 MangoHud 寫入欄位",
                _ when frameSource is not null => "等待 " + frameSource + " 的新幀",
                _ => "等待 MangoHud 記錄；請用下方啟動選項開啟遊戲"
            };
            // Stale or stopped frames must not keep showing an old frame rate.
            var shown = frameState == TelemetrySourceState.Available ? frameStatistics with { LowOnePercent = perFrame == false ? null : frameStatistics.LowOnePercent } : NoFrames;
            return new(current, cpu, memory, network, systemStatus,
                hardwareState == TelemetrySourceState.Available ? readings : [], hardwareState, hardwareStatus,
                shown, frameState, frameStatus, frameSource, perFrame, frameGeneration);
        }
    }
}
