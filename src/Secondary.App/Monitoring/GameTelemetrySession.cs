using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.App.Monitoring;

public sealed record GameTarget(int Id, long StartedUtcTicks, string Name);
public sealed record GameTelemetrySnapshot(SystemSample? System, double? Gpu, string GpuStatus,
    SensorSnapshot? Sensors, string SensorStatus, FrameStatistics Frames, string FrameStatus, bool TargetExited);

public sealed class GameTelemetrySession(bool high, bool sensors, GameTarget? target)
{
    private readonly object gate = new();
    private SystemSample? system;
    private double? gpu;
    private string gpuStatus = "等待 GPU 基準", sensorStatus = sensors ? "正在啟動感測助手" : "硬體助手未啟用";
    private string frameStatus = target is null ? "請手動選擇遊戲程序" : "等待 PresentMon";
    private SensorSnapshot? sensorSnapshot;
    private readonly FrameWindow frames = new(target?.Id ?? 0);
    private long lastFrame, lastSensor;
    private bool targetExited, frameEnded;

    public GameTelemetrySnapshot Read()
    {
        lock (gate)
        {
            var frameFresh = lastFrame != 0 && Stopwatch.GetElapsedTime(lastFrame).TotalSeconds < 3 && !targetExited && !frameEnded;
            var sensorFresh = lastSensor != 0 && Stopwatch.GetElapsedTime(lastSensor).TotalSeconds < (high ? 8 : 25);
            return new(system, gpu, gpuStatus, sensorFresh ? sensorSnapshot : null, sensorStatus,
                frameFresh ? frames.Read() : new(null, 0, null, null, null, false),
                frameFresh ? "PresentMon / 最近 60 秒最活躍交換鏈" : lastFrame != 0 && !frameEnded && !targetExited ? "暫無新幀，等待程序恢復呈現" : frameStatus, targetExited);
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
            using var sampler = new WindowsSampler(enableGpu: false);
            while (!token.IsCancellationRequested)
            {
                var value = sampler.Read(true, high);
                lock (gate) system = value;
                await Task.Delay(SamplingPolicy.IntervalMs(true, high), token);
            }
        }
        async Task Gpu()
        {
            using var counter = new GpuCounter();
            while (!token.IsCancellationRequested)
            {
                var value = counter.Read();
                lock (gate) { gpu = value; gpuStatus = counter.Status; }
                await Task.Delay(SamplingPolicy.GpuIntervalSeconds(true, high) * 1000, token);
            }
        }
        async Task Hardware()
        {
            if (!sensors) return;
            await SensorStreamClient.RunAsync(high, value => { lock (gate) { sensorSnapshot = value; lastSensor = Stopwatch.GetTimestamp(); sensorStatus = "硬體助手 / 持續採樣"; } }, token);
        }
        async Task Frames()
        {
            if (target is null) return;
            using var selected = Process.GetProcessById(target.Id);
            if (selected.StartTime.ToUniversalTime().Ticks != target.StartedUtcTicks) throw new InvalidOperationException("原程序已退出或 PID 已重新使用；請重新選擇。");
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(token);
            var stream = PresentMonStream.RunAsync(target.Id,
                value => { lock (gate) { frames.Add(value); lastFrame = Stopwatch.GetTimestamp(); } },
                value => { lock (gate) frameStatus = value; }, capture.Token);
            var exit = selected.WaitForExitAsync(capture.Token);
            try
            {
                var finished = await Task.WhenAny(stream, exit);
                if (finished == exit && !token.IsCancellationRequested)
                    lock (gate) { targetExited = true; frameStatus = "選定程序已退出；請手動選擇新程序。"; }
                else await stream;
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
        await Task.WhenAll(
            Task.Run(() => Protect(Basic, message => { system = null; gpuStatus = "系統採樣失敗：" + message; }), token),
            Task.Run(() => Protect(Gpu, message => { gpu = null; gpuStatus = message; }), token),
            Task.Run(() => Protect(Hardware, message => { sensorSnapshot = null; lastSensor = 0; sensorStatus = message; }), token),
            Task.Run(() => Protect(Frames, message => { frameStatus = message; frameEnded = true; }), token));
    }
}
