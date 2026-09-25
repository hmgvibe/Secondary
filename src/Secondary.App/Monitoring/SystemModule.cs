using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Secondary.Core;

namespace Secondary.App.Monitoring;

public sealed class SystemModule(SystemDashboardModel model, Dispatcher dispatcher, Func<ISystemSampler>? factory = null) : ISectionModule
{
    private readonly Func<ISystemSampler> factory = factory ?? (() => new WindowsSampler());
    private bool active, visible, game, high, disposed;
    private CancellationTokenSource? cancellation;
    private readonly object cancellationGate = new();
    private Task worker = Task.CompletedTask;
    public Task Completion => worker;
    public void Configure(bool visible, bool game, bool high = false)
    {
        if (this.visible == visible && this.game == game && this.high == high) return;
        this.visible = visible; this.game = game; this.high = high; Restart();
    }
    public void Activate() { if (disposed || active) return; active = true; Restart(); }
    public void Suspend() { active = false; Restart(); }
    private void Restart()
    {
        lock (cancellationGate) cancellation?.Cancel();
        if (disposed || !active || !visible)
        { model.Status = "採樣已暫停；返回系統分區並顯示視窗後恢復。"; return; }
        var previous = worker;
        var owner = new CancellationTokenSource();
        lock (cancellationGate) cancellation = owner;
        var token = owner.Token;
        var gameMode = game;
        var highSampling = high;
        model.Status = "正在取得採樣基準…";
        // One serial worker, no overlapping queries, no UI-thread native calls,
        // and at most one awaited dispatcher update. Old generations cannot publish.
        worker = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using var sampler = factory();
                while (!token.IsCancellationRequested)
                {
                    var sample = sampler.Read(gameMode, highSampling);
                    token.ThrowIfCancellationRequested();
                    await dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) model.Update(sample, gameMode, highSampling); }, DispatcherPriority.Background, token);
                    await Task.Delay(SamplingPolicy.IntervalMs(gameMode, highSampling), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!token.IsCancellationRequested && !dispatcher.HasShutdownStarted)
                    await dispatcher.InvokeAsync(() => { if (!token.IsCancellationRequested) model.Status = "採樣失敗；離開再返回系統分區可重試。"; });
            }
        });
        // Cleanup is independent of the dispatcher, including application shutdown.
        _ = worker.ContinueWith(_ =>
        {
            lock (cancellationGate)
            {
                if (ReferenceEquals(cancellation, owner)) cancellation = null;
                owner.Dispose();
            }
        }, TaskScheduler.Default);
    }
    public void Dispose() { if (disposed) return; disposed = true; active = false; lock (cancellationGate) cancellation?.Cancel(); }
}
