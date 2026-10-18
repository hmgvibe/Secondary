using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Monitoring;

public sealed record GameTarget(int Id, long StartedUtcTicks, string Name);
public sealed record GameTelemetrySnapshot(SystemSample? System, double? Gpu, string GpuStatus,
    SensorSnapshot? Sensors, string SensorStatus, FrameStatistics Frames, string FrameStatus, bool TargetExited,
    string SystemStatus = "等待系統採樣", TelemetrySourceState SystemState = TelemetrySourceState.Waiting,
    TelemetrySourceState GpuState = TelemetrySourceState.Waiting, TelemetrySourceState SensorState = TelemetrySourceState.Waiting,
    TelemetrySourceState FrameState = TelemetrySourceState.Waiting, DateTimeOffset? LastSystemSuccess = null,
    DateTimeOffset? LastGpuSuccess = null, DateTimeOffset? LastSensorSuccess = null, DateTimeOffset? LastFrameSuccess = null,
    TelemetrySourceState CpuState = TelemetrySourceState.Waiting, TelemetrySourceState MemoryState = TelemetrySourceState.Waiting,
    TelemetrySourceState NetworkState = TelemetrySourceState.Waiting);

// The game page reads one of these once a second: local sampling, or the main computer's in connection mode.
public interface IGameTelemetry
{
    GameTelemetrySnapshot Read();
    Task RunAsync(CancellationToken token);
}

public sealed class GameTelemetrySession(bool high, bool sensors, GameTarget? target, Func<ISystemSampler>? systemSamplerFactory = null,
    Func<bool, Action<SensorSnapshot>, CancellationToken, Task>? sensorStreamRunner = null) : IGameTelemetry
{
    private readonly object gate = new();
    private SystemSample? system;
    private double? gpu;
    private string systemStatus = "等待系統採樣";
    private string gpuStatus = "等待 GPU 基準", sensorStatus = sensors ? "正在啟動感測助手" : "硬體助手未啟用";
    private string frameStatus = target is null ? "請手動選擇遊戲程序" : "等待 PresentMon";
    private SensorSnapshot? sensorSnapshot;
    private readonly FrameWindow frames = new(target?.Id ?? 0);
    private long lastSystem, lastGpu, lastSensor;
    private DateTimeOffset? systemSuccess, gpuSuccess, sensorSuccess;
    private bool targetExited, frameEnded, stopped, systemFailed, gpuFailed, sensorFailed;
    private FrameStatistics frameSnapshot = new(null, 0, null, null, null, false);
    private DateTimeOffset? frameSuccess;
    private static bool Fresh(DateTimeOffset time, DateTimeOffset now, double maximumSeconds) =>
        time > DateTimeOffset.MinValue && (now - time).TotalSeconds is >= -2 && (now - time).TotalSeconds < maximumSeconds;
    private static bool BlocksValue(TelemetrySourceState state) => state is TelemetrySourceState.Failed or
        TelemetrySourceState.Stale or TelemetrySourceState.Stopped or TelemetrySourceState.Disabled;
    private static SystemSample NormalizeSystem(SystemSample value)
    {
        var cpuValid = value.Cpu.HasValue && double.IsFinite(value.Cpu.Value) && value.Cpu is >= 0 and <= 100 && !BlocksValue(value.CpuState);
        var memoryValid = value.TotalMemory > 0 && value.AvailableMemory <= value.TotalMemory && !BlocksValue(value.MemoryState);
        var networkValid = value.Download.HasValue && value.Upload.HasValue && double.IsFinite(value.Download.Value) && double.IsFinite(value.Upload.Value) &&
            value.Download >= 0 && value.Upload >= 0 && !BlocksValue(value.NetworkState);
        return value with
        {
            Cpu = cpuValid ? value.Cpu : null, TotalMemory = memoryValid ? value.TotalMemory : 0, AvailableMemory = memoryValid ? value.AvailableMemory : 0,
            Download = networkValid ? value.Download : null, Upload = networkValid ? value.Upload : null,
            CpuState = cpuValid ? TelemetrySourceState.Available : (value.Cpu.HasValue || value.CpuState == TelemetrySourceState.Available) && !BlocksValue(value.CpuState) ? TelemetrySourceState.Failed : value.CpuState,
            MemoryState = memoryValid ? TelemetrySourceState.Available : value.MemoryState == TelemetrySourceState.Available ? TelemetrySourceState.Failed : value.MemoryState,
            NetworkState = networkValid ? TelemetrySourceState.Available : value.NetworkState == TelemetrySourceState.Available ? TelemetrySourceState.Failed : value.NetworkState
        };
    }

    public GameTelemetrySnapshot Read()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            var lastFrame = frameSnapshot.LastArrivalTimestamp;
            var frameFresh = lastFrame != 0 && Stopwatch.GetElapsedTime(lastFrame).TotalSeconds < 3 && !targetExited && !frameEnded && !stopped;
            var sensorFresh = lastSensor != 0 && Stopwatch.GetElapsedTime(lastSensor).TotalSeconds < (high ? 8 : 25);
            var systemFresh = lastSystem != 0 && system is not null && Stopwatch.GetElapsedTime(lastSystem).TotalSeconds < (high ? 4 : 8) && Fresh(system.Time, now, high ? 4 : 8);
            var gpuFresh = lastGpu != 0 && Stopwatch.GetElapsedTime(lastGpu).TotalSeconds < (high ? 8 : 25);
            TelemetrySourceState State(bool fresh, long last, bool failed) => stopped ? TelemetrySourceState.Stopped :
                failed ? TelemetrySourceState.Failed : fresh ? TelemetrySourceState.Available :
                last != 0 ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            var basicState = State(systemFresh, lastSystem, systemFailed);
            var currentSystem = systemFresh && !stopped ? system : null;
            if (currentSystem is not null && currentSystem.CpuState != TelemetrySourceState.Available &&
                currentSystem.MemoryState != TelemetrySourceState.Available && currentSystem.NetworkState != TelemetrySourceState.Available)
                basicState = currentSystem.CpuState == TelemetrySourceState.Failed || currentSystem.MemoryState == TelemetrySourceState.Failed || currentSystem.NetworkState == TelemetrySourceState.Failed
                    ? TelemetrySourceState.Failed : currentSystem.CpuState == TelemetrySourceState.Stale || currentSystem.MemoryState == TelemetrySourceState.Stale || currentSystem.NetworkState == TelemetrySourceState.Stale
                    ? TelemetrySourceState.Stale : TelemetrySourceState.Waiting;
            var gpuState = State(gpuFresh && gpu.HasValue, lastGpu, gpuFailed || (!gpu.HasValue && !gpuStatus.StartsWith("等待", StringComparison.Ordinal)));
            var currentSensors = sensorFresh && !stopped && !sensorFailed ? sensorSnapshot : null;
            var hardwareState = sensors ? State(sensorFresh, lastSensor, sensorFailed) : TelemetrySourceState.Disabled;
            var hardwareStatus = sensorStatus;
            if (currentSensors is not null)
            {
                var expired = currentSensors.Readings.Count(r => r.State == ReadingState.Available && !Fresh(r.Time, now, high ? 8 : 25));
                if (expired > 0)
                    currentSensors = currentSensors with { Readings = currentSensors.Readings.Select(r => r.State == ReadingState.Available && !Fresh(r.Time, now, high ? 8 : 25)
                        ? r with { Value = null, State = ReadingState.Unavailable, Reason = "感測資料已過期或時間無效；等待新的硬體讀數。" } : r).ToArray() };
                var available = currentSensors.Readings.Any(r => r.State == ReadingState.Available);
                if (!available) { hardwareState = expired > 0 ? TelemetrySourceState.Stale : TelemetrySourceState.Failed; hardwareStatus = expired > 0 ? "硬體讀數已過期；助手傳輸仍持續，等待新資料" : "硬體助手已連接，來源目前無可用讀數"; }
                else if (expired > 0) hardwareStatus = "部分硬體讀數已過期；其他來源照常更新";
            }
            return new(currentSystem, gpuFresh && !stopped ? gpu : null,
                stopped ? "GPU 採樣已停止" : gpuState == TelemetrySourceState.Stale ? "GPU 資料已過期，等待重新採樣" : gpuStatus,
                currentSensors,
                stopped ? "硬體採樣已停止" : !sensorFresh && lastSensor != 0 && !sensorFailed ? "硬體資料已過期，等待助手回應" : hardwareStatus,
                frameFresh ? frameSnapshot : new(null, 0, null, null, null, false),
                stopped ? "幀監測已停止" : frameFresh ? "PresentMon / 目前活動交換鏈" : lastFrame != 0 && !frameEnded && !targetExited ? "選定交換鏈暫無新幀，等待程序恢復呈現" : frameStatus, targetExited,
                stopped ? "系統採樣已停止" : !systemFresh && lastSystem != 0 && !systemFailed ? "系統資料已過期，等待重新採樣" : systemStatus,
                basicState, gpuState, hardwareState,
                stopped || targetExited ? TelemetrySourceState.Stopped : frameEnded ? TelemetrySourceState.Failed :
                frameFresh ? TelemetrySourceState.Available : lastFrame != 0 ? TelemetrySourceState.Stale : target is null ? TelemetrySourceState.Disabled : TelemetrySourceState.Waiting,
                systemSuccess, gpuSuccess, sensorSuccess, frameSuccess,
                currentSystem?.Cpu is not null ? TelemetrySourceState.Available : currentSystem?.CpuState ?? basicState,
                currentSystem is { TotalMemory: > 0 } ? TelemetrySourceState.Available : currentSystem?.MemoryState ?? basicState,
                currentSystem?.Download is not null && currentSystem.Upload is not null ? TelemetrySourceState.Available : currentSystem?.NetworkState ?? basicState);
        }
    }
    public async Task RunAsync(CancellationToken token)
    {
        async Task Protect(Func<Task> action, Action<string> failed)
        {
            try { await action(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) { lock (gate) failed(e.Message); }
        }
        async Task Basic()
        {
            using var sampler = systemSamplerFactory?.Invoke() ?? new WindowsSampler(enableGpu: false);
            while (!token.IsCancellationRequested)
            {
                var value = NormalizeSystem(sampler.Read(true, high));
                lock (gate)
                {
                    system = value; lastSystem = Stopwatch.GetTimestamp();
                    if (Fresh(value.Time, DateTimeOffset.UtcNow, high ? 4 : 8) &&
                        (value.CpuState == TelemetrySourceState.Available || value.MemoryState == TelemetrySourceState.Available || value.NetworkState == TelemetrySourceState.Available)) systemSuccess = value.Time;
                    systemStatus = value.SourceStatus ?? (value.TotalMemory > 0 ? "整機採樣 / 持續更新" : "整機採樣 / 記憶體來源無資料");
                }
                await Task.Delay(SamplingPolicy.IntervalMs(true, high), token);
            }
        }
        async Task Gpu()
        {
            using var counter = new GpuCounter();
            while (!token.IsCancellationRequested)
            {
                var value = counter.Read();
                lock (gate)
                {
                    gpu = value; gpuStatus = counter.Status;
                    if (value.HasValue) { lastGpu = Stopwatch.GetTimestamp(); gpuSuccess = DateTimeOffset.UtcNow; }
                }
                await Task.Delay(SamplingPolicy.GpuIntervalSeconds(true, high) * 1000, token);
            }
        }
        async Task Hardware()
        {
            if (!sensors) return;
            var runner = sensorStreamRunner ?? SensorStreamClient.RunAsync;
            await runner(high, value =>
            {
                lock (gate)
                {
                    sensorSnapshot = value; lastSensor = Stopwatch.GetTimestamp(); sensorStatus = "硬體助手 / 持續採樣";
                    var now = DateTimeOffset.UtcNow;
                    var successful = value.Readings.Where(r => r.State == ReadingState.Available && Fresh(r.Time, now, high ? 8 : 25)).Select(r => (DateTimeOffset?)r.Time).Max();
                    if (successful.HasValue) sensorSuccess = successful.Value;
                }
            }, token);
            if (!token.IsCancellationRequested) lock (gate) { sensorFailed = true; sensorSnapshot = null; sensorStatus = "硬體助手已停止；重新切換監測可重試。"; }
        }
        async Task FrameStatisticsWorker()
        {
            while (!token.IsCancellationRequested)
            {
                // Snapshot copying is bounded; tail selection and chain scoring never hold the source lock.
                var value = frames.Read();
                lock (gate)
                {
                    if (value.LastArrivalTimestamp != 0 && value.LastArrivalTimestamp != frameSnapshot.LastArrivalTimestamp)
                        frameSuccess = DateTimeOffset.UtcNow - Stopwatch.GetElapsedTime(value.LastArrivalTimestamp);
                    frameSnapshot = value;
                }
                await Task.Delay(250, token);
            }
        }
        async Task Frames()
        {
            if (target is null) return;
            using var selected = Process.GetProcessById(target.Id);
            if (selected.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks) throw new InvalidOperationException("原程序已退出或 PID 已重新使用；請重新選擇。");
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stream = PresentMonStream.RunAsync(target.Id,
                value => frames.Add(value),
                value => { lock (gate) frameStatus = value; }, capture.Token);
            var exit = selected.WaitForExitAsync(capture.Token);
            try
            {
                var finished = await Task.WhenAny(stream, exit);
                if (finished == exit && !token.IsCancellationRequested)
                    lock (gate) { targetExited = true; frameStatus = "選定程序已退出；請手動選擇新程序。"; }
                else { await stream; lock (gate) frameEnded = true; }
                // Even if ETW is denied, retain exit detection without restarting capture.
                if (!selected.HasExited && !token.IsCancellationRequested) await exit;
            }
            finally
            {
                if (selected.HasExited && !token.IsCancellationRequested)
                    lock (gate) { targetExited = true; frameStatus = "選定程序已退出；請手動選擇新程序。"; }
                capture.Cancel();
                try { await stream; } catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
                try { await exit; } catch (OperationCanceledException) when (capture.IsCancellationRequested) { }
                lock (gate) frameEnded = true;
            }
        }
        try
        {
            await Task.WhenAll(
                Task.Run(() => Protect(Basic, message => { system = null; systemFailed = true; systemStatus = "系統採樣失敗：" + message; }), token),
                Task.Run(() => Protect(Gpu, message => { gpu = null; gpuFailed = true; gpuStatus = message; }), token),
                Task.Run(() => Protect(Hardware, message => { sensorSnapshot = null; sensorFailed = true; sensorStatus = message; }), token),
                Task.Run(() => Protect(Frames, message => { frameStatus = message; frameEnded = true; }), token),
                Task.Run(() => Protect(FrameStatisticsWorker, message => { frameStatus = "幀統計失敗：" + message; frameEnded = true; }), token));
        }
        finally { lock (gate) stopped = true; }
    }
}
