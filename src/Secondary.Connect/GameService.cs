using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Monitoring;
using Secondary.Shared;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Connect;

// Samples only while the connected device shows its game page, with the device's sampling rate, and sends one
// snapshot a second. A new request replaces the previous one after it has fully stopped (PresentMon, sensor helper).
public sealed class GameService(Func<RemoteGameRequest, IGameTelemetry>? factory = null, Func<IReadOnlyList<RemoteProcess>>? processes = null)
{
    private readonly object gate = new();
    private readonly Func<RemoteGameRequest, IGameTelemetry> factory = factory ?? (request => new GameTelemetrySession(request.High, request.Sensors,
        request.Target is { } target ? new GameTarget(target.Id, target.Started, target.Name) : null));
    private readonly Func<IReadOnlyList<RemoteProcess>> processes = processes ?? (() => ProcessCatalog.List().Select(p => new RemoteProcess(p.Id, p.StartedUtcTicks, p.Name)).ToArray());
    private CancellationTokenSource? running;
    private Task completion = Task.CompletedTask;
    public event Action? Changed;
    public RemoteGameRequest? Current { get; private set; }
    public long SnapshotsSent { get; private set; }
    public string Status => Current is { } request
        ? $"遊戲監測：傳送中 · {(request.High ? "高" : "低")}採樣率{(request.Sensors ? " · 硬體感測" : "")}{(request.Target is { } target ? " · " + target.Name : "")}"
        : "遊戲監測：未使用（設備不在遊戲頁時不採樣）";

    // The session token ends with the device's connection, which also stops sampling.
    public Task Handle(JsonObject message, FrameChannel channel, CancellationToken session)
    {
        switch (TypeOf(message))
        {
            case "game-start" when RemoteGame.ReadStart(message) is { } request: Start(request, channel, session); break;
            case "game-stop": Stop(Number(message, "session")); break;
            case "processes": _ = ReplyProcesses(message, channel, session); break;
        }
        return Task.CompletedTask;
    }

    private void Start(RemoteGameRequest request, FrameChannel channel, CancellationToken session)
    {
        var owner = CancellationTokenSource.CreateLinkedTokenSource(session);
        CancellationTokenSource? previous;
        lock (gate)
        {
            previous = running;
            running = owner;
            Current = request;
            var before = completion;
            completion = Task.Run(() => Stream(request, channel, owner, before));
        }
        Cancel(previous);
        Changed?.Invoke();
    }
    // A stream that ended on its own may already have disposed its source.
    private static void Cancel(CancellationTokenSource? owner)
    {
        try { owner?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Stop(long? session)
    {
        CancellationTokenSource? owner = null;
        lock (gate) if (Current is { } current && (session is null || current.Session == session)) { owner = running; running = null; Current = null; }
        Cancel(owner);
        if (owner is not null) Changed?.Invoke();
    }

    private async Task Stream(RemoteGameRequest request, FrameChannel channel, CancellationTokenSource owner, Task before)
    {
        try { await before.ConfigureAwait(false); } catch (Exception) { }
        var token = owner.Token;
        if (token.IsCancellationRequested) { Finish(owner); return; }
        var telemetry = factory(request);
        var sampling = Task.Run(() => telemetry.RunAsync(token));
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                await channel.SendAsync(RemoteGame.Snapshot(RemoteGameMapping.ToRemote(telemetry.Read(), request.Session, request.High)), token).ConfigureAwait(false);
                SnapshotsSent++;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            owner.Cancel();
            try { await sampling.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { AppDiagnostics.Write("connect-game", error); }
            Finish(owner);
        }
    }
    private void Finish(CancellationTokenSource owner)
    {
        var ended = false;
        lock (gate) if (running == owner) { running = null; Current = null; ended = true; }
        owner.Dispose();
        if (ended) Changed?.Invoke();
    }

    private async Task ReplyProcesses(JsonObject request, FrameChannel channel, CancellationToken session)
    {
        try
        {
            var list = await Task.Run(processes, session).ConfigureAwait(false);
            await channel.SendAsync(Reply(request, RemoteGame.ProcessList(list)), session).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception error) { AppDiagnostics.Write("connect-processes", error); }
    }

    // Stops sampling when the helper exits, so PresentMon and the sensor helper are not left running.
    public async Task StopAsync()
    {
        Stop(null);
        Task pending;
        lock (gate) pending = completion;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch (Exception) { }
    }
}
