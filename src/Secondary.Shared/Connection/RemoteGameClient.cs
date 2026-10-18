using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// What the game page shows from the main computer this second. Snapshot is null until the first one arrives;
// Stale means the newest one is too old to show values from.
public sealed record RemoteGameView(RemoteGameSnapshot? Snapshot, bool Stale, string Status, string HelperName);

// The device side of remote game monitoring: asks the helper to sample while the game page is on screen,
// keeps the newest snapshot, and asks again after a reconnection. Stopping tells the helper to stop sampling.
public sealed class RemoteGameClient(ConnectionController controller, bool high, bool sensors, RemoteProcess? target)
{
    private static long sessions;
    private readonly object gate = new();
    private readonly long session = Interlocked.Increment(ref sessions);
    private RemoteGameSnapshot? latest;
    private long received;
    private bool connected;
    public long Session => session;
    public bool High => high;

    public async Task RunAsync(CancellationToken token)
    {
        void OnMessage(JsonObject message)
        {
            if (TypeOf(message) != "game-snapshot" || RemoteGame.ReadSnapshot(message) is not { } snapshot || snapshot.Session != session) return;
            lock (gate) { latest = snapshot; received = Stopwatch.GetTimestamp(); }
        }
        void OnChanged()
        {
            var now = controller.State == LinkState.Connected;
            bool reconnected;
            lock (gate) { reconnected = now && !connected; connected = now; }
            if (reconnected && !token.IsCancellationRequested) _ = Send(RemoteGame.Start(new(session, high, sensors, target)));
        }
        controller.MessageReceived += OnMessage;
        controller.Changed += OnChanged;
        try
        {
            OnChanged();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            controller.MessageReceived -= OnMessage;
            controller.Changed -= OnChanged;
            if (controller.State == LinkState.Connected) await Send(Message("game-stop", ("session", session))).ConfigureAwait(false);
        }
    }

    private async Task Send(JsonObject message)
    {
        try { await controller.SendAsync(message).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    public RemoteGameView Read()
    {
        var name = controller.ConnectedName ?? "主電腦";
        if (controller.State != LinkState.Connected) return new(null, false, "與主電腦的連線中斷：" + controller.Status, name);
        RemoteGameSnapshot? snapshot;
        long at;
        lock (gate) { snapshot = latest; at = received; }
        if (snapshot is null) return new(null, false, $"等待主電腦「{name}」開始採樣", name);
        var age = Stopwatch.GetElapsedTime(at);
        return age > RemoteGame.StaleAfter
            ? new(snapshot, true, $"主電腦資料已過期：{age.TotalSeconds:0} 秒沒有更新", name)
            : new(snapshot, false, $"主電腦「{name}」· 連線模式", name);
    }
}
