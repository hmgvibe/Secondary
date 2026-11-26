using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

public enum LinkState { Idle, Connecting, Connected, Waiting, Failed }
public sealed record HelperEntry(string Fingerprint, string Name, string Address, int Port, bool Paired, bool Found, bool Connected);

// What the device's "连接模式" card drives: search, pair, connect with automatic reconnection, disconnect and forget.
// Changed is raised from worker threads; the UI marshals it to its own thread.
public sealed class ConnectionController(ConnectionIdentity identity, PairedStore helpers, string deviceName,
    Func<IEnumerable<IPEndPoint>>? discoveryTargets = null, TimeSpan? searchWait = null) : IAsyncDisposable
{
    private static readonly int[] RetrySeconds = [1, 2, 5, 10, 30];
    private readonly object gate = new();
    private readonly Func<IEnumerable<IPEndPoint>> targets = discoveryTargets ?? (() => DeviceLink.BroadcastTargets());
    private readonly TimeSpan wait = searchWait ?? TimeSpan.FromSeconds(2);
    private Dictionary<string, DiscoveredHelper> found = new(StringComparer.Ordinal);
    private CancellationTokenSource? running;
    private Task loop = Task.CompletedTask;
    private FrameChannel? channel;
    private string? target;
    private string? hotkey;
    private long nextRequest;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    public event Action? Changed;
    public event Action<JsonObject>? MessageReceived;
    // The main computer's hotkey was pressed; the device enters or leaves game mode as with its own hotkey.
    public event Action? ToggleGameRequested;
    public LinkState State { get; private set; } = LinkState.Idle;
    public string Status { get; private set; } = "未连接";
    public string? ConnectedName { get; private set; }
    public double? LatencyMs { get; private set; }
    public bool Searching { get; private set; }
    public string SearchStatus { get; private set; } = "";
    public string DeviceName => deviceName;
    public string Fingerprint => identity.Fingerprint;
    public string StatusLine => State == LinkState.Connected
        ? (LatencyMs is { } ms ? $"{Status} · 延迟 {ms:0} ms" : Status) + (HotkeyStatus is { } keys ? "\n" + keys : "") : Status;
    public static string Describe(HelperEntry entry) => $"{entry.Name} · {entry.Address}" +
        (entry.Connected ? " · 已连接" : entry.Paired ? entry.Found ? " · 已配对 · 在线上" : " · 已配对" : " · 尚未配对");
    // Accepts "host" or "host:port" as typed by the user; IPv6 is not used on the local network here.
    public static bool TryParseAddress(string? text, out string host, out int port)
    {
        host = (text ?? "").Trim(); port = TcpPort;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            if (!int.TryParse(host[(colon + 1)..], out port) || port is <= 0 or > 65535) return false;
            host = host[..colon];
        }
        return host.Length is > 0 and <= 253 && !host.Any(char.IsWhiteSpace);
    }
    // The device's hotkey, registered on the main computer while connected so the same keys work there.
    public string? Hotkey { get => hotkey; set { hotkey = value; _ = SendHotkeyAsync(); } }
    public string? HotkeyStatus { get; private set; }
    public string? StoreProblem => helpers.LoadError ?? helpers.SaveError ?? (identity.Recreated ? "本机连接身分已重新建立，先前的配对需要重新进行。" : null);

    public IReadOnlyList<HelperEntry> Helpers
    {
        get
        {
            Dictionary<string, DiscoveredHelper> seen;
            string? connected;
            lock (gate) { seen = new(found); connected = State == LinkState.Connected ? target : null; }
            var paired = helpers.Peers.Select(p => new HelperEntry(p.Fingerprint, p.Name,
                seen.TryGetValue(p.Fingerprint, out var d) ? d.Address : p.Address ?? "", seen.TryGetValue(p.Fingerprint, out d) ? d.Port : p.Port,
                true, seen.ContainsKey(p.Fingerprint), p.Fingerprint == connected));
            var others = seen.Values.Where(d => helpers.Find(d.Fingerprint) is null).Select(d => new HelperEntry(d.Fingerprint, d.Name, d.Address, d.Port, false, true, false));
            return paired.Concat(others).ToArray();
        }
    }

    // Reconnects to the helper that was connected when the app last closed.
    public void Start()
    {
        if (helpers.Peers.FirstOrDefault(p => p.AutoConnect) is { } peer) Connect(peer.Fingerprint);
        else Raise();
    }

    public async Task SearchAsync(CancellationToken token = default)
    {
        Searching = true; SearchStatus = "正在搜索区域网络中的主机…"; Raise();
        try
        {
            var results = await DeviceLink.DiscoverAsync(targets(), wait, token).ConfigureAwait(false);
            lock (gate) found = results.ToDictionary(r => r.Fingerprint, StringComparer.Ordinal);
            // A paired helper whose address changed (for example a new DHCP lease) is updated in place.
            foreach (var result in results)
                if (helpers.Find(result.Fingerprint) is { } peer && (peer.Address != result.Address || peer.Port != result.Port))
                    helpers.Save(peer with { Address = result.Address, Port = result.Port });
            SearchStatus = results.Count == 0
                ? "没有找到主机。请确认主机已执行连接助手、两台在同一个网络，且 Windows 防火墙允许连接助手使用「私人网络」；也可以手动输入 IP。"
                : results.Any(r => r.Protocol != ProtocolVersion) ? "找到的主机中有版本不相容的连接助手，请更新到同一版本。" : $"找到 {results.Count} 台主机。";
        }
        catch (Exception error) when (error is SocketException or IOException) { SearchStatus = "无法搜索：" + error.Message + "；可以手动输入 IP。"; }
        finally { Searching = false; Raise(); }
    }

    // Returns null on success (and starts connecting), or the reason pairing failed.
    public async Task<string?> PairAsync(string host, int port, string codeText, CancellationToken token = default)
    {
        if (!Pairing.TryParse(codeText, out var code)) return "请输入主机上显示的 6 位数配对码。";
        if (string.IsNullOrWhiteSpace(host)) return "请选择主机或输入 IP。";
        try
        {
            var peer = await DeviceLink.PairAsync(identity, helpers, deviceName, host.Trim(), port, code, token).ConfigureAwait(false);
            Connect(peer.Fingerprint);
            return null;
        }
        catch (Exception error) when (Describe(error) is { } reason) { return reason; }
    }

    public void Connect(string fingerprint)
    {
        if (helpers.Find(fingerprint) is not { } peer) return;
        StopLoop();
        helpers.SetAutoConnect(fingerprint);
        var owner = new CancellationTokenSource();
        lock (gate) { running = owner; target = fingerprint; }
        loop = Task.Run(() => Run(peer, owner.Token));
    }

    public void Disconnect()
    {
        helpers.SetAutoConnect(null);
        StopLoop(Message("bye"));
        Set(LinkState.Idle, "未连接", null);
    }

    // Forgetting the connected helper also asks it to drop this device.
    public void Forget(string fingerprint)
    {
        string? current;
        lock (gate) current = target;
        if (current == fingerprint) { StopLoop(Message("unpair")); Set(LinkState.Idle, "已移除配对", null); }
        helpers.Remove(fingerprint);
        Raise();
    }

    public async Task SendAsync(JsonObject message, CancellationToken token = default)
    {
        FrameChannel? current;
        lock (gate) current = channel;
        if (current is null) throw new InvalidOperationException("尚未连接");
        await current.SendAsync(message, token).ConfigureAwait(false);
    }

    // Sends a message with an id and waits for the reply that carries it back as "reply-to".
    public async Task<JsonObject> RequestAsync(JsonObject message, TimeSpan timeout, CancellationToken token = default)
    {
        // The id field belongs to the request itself; a message field of that name would be overwritten.
        if (message.ContainsKey("id")) throw new ArgumentException("请求消息不可自行使用 id 栏位。", nameof(message));
        var id = Interlocked.Increment(ref nextRequest);
        message["id"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try
        {
            await SendAsync(message, token).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            try { return await reply.Task.WaitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("主机没有响应"); }
        }
        finally { pending.TryRemove(id, out _); }
    }

    private async Task SendHotkeyAsync()
    {
        FrameChannel? current;
        lock (gate) current = channel;
        if (current is null || hotkey is null) return;
        try { await current.SendAsync(Message("hotkey", ("gesture", hotkey)), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private void StopLoop(JsonObject? farewell = null)
    {
        CancellationTokenSource? owner;
        FrameChannel? current;
        lock (gate) { owner = running; running = null; target = null; current = channel; }
        if (farewell is not null && current is not null)
        {
            // Best effort: the helper also notices a closed connection on its own.
            try { current.SendAsync(farewell, CancellationToken.None).Wait(TimeSpan.FromSeconds(1)); }
            catch (Exception error) when (error is AggregateException or IOException or ObjectDisposedException) { }
        }
        owner?.Cancel();
    }

    private async Task Run(PairedPeer peer, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            if (helpers.Find(peer.Fingerprint) is not { } current) { Set(LinkState.Idle, "已移除配对", null); return; }
            string reason;
            try
            {
                Set(LinkState.Connecting, $"正在连接到「{current.Name}」（{current.Address}）", null);
                var opened = await DeviceLink.ConnectAsync(identity, current, current.Address ?? "", current.Port > 0 ? current.Port : TcpPort, deviceName, token).ConfigureAwait(false);
                await using (opened.ConfigureAwait(false))
                {
                    attempt = 0;
                    helpers.Save(current with { LastSeen = DateTimeOffset.UtcNow });
                    lock (gate) channel = opened;
                    Set(LinkState.Connected, $"已连接到「{current.Name}」", current.Name);
                    await SendHotkeyAsync().ConfigureAwait(false);
                    try { await Converse(opened, token).ConfigureAwait(false); }
                    finally
                    {
                        lock (gate) channel = null;
                        LatencyMs = null; HotkeyStatus = null;
                        foreach (var waiting in pending.Values) waiting.TrySetException(new IOException("与主机的连接已中断"));
                    }
                }
                reason = "主机已断开连接";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (ConnectionRefusedException error) when (error.Code == "not-paired")
            {
                helpers.Remove(peer.Fingerprint);
                Set(LinkState.Failed, "主机已移除这台设备的配对；需要时请重新配对。", null);
                return;
            }
            catch (ConnectionRefusedException error) when (error.Code is "identity-changed" or "version") { Set(LinkState.Failed, error.Message, null); return; }
            catch (Exception error) when (Describe(error) is { } description) { reason = description; }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                // Unexpected errors are logged and retried rather than silently ending the loop.
                AppDiagnostics.Write("connection", error);
                reason = "连接发生错误：" + error.Message;
            }
            if (token.IsCancellationRequested) return;
            var delay = RetrySeconds[Math.Min(attempt, RetrySeconds.Length - 1)];
            attempt++;
            Set(LinkState.Waiting, $"{reason}；{delay} 秒后重试", null);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                // After repeated failures the helper may have a new address; look for its fingerprint.
                if (attempt >= 2) await SearchAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Converse(FrameChannel opened, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = Stopwatch.StartNew();
        async Task Beat()
        {
            while (!stop.IsCancellationRequested)
            {
                await opened.SendAsync(Message("ping", ("sent", clock.Elapsed.TotalMilliseconds)), stop.Token).ConfigureAwait(false);
                await Task.Delay(HeartbeatInterval, stop.Token).ConfigureAwait(false);
            }
        }
        var heartbeat = Beat();
        try
        {
            while (true)
            {
                var message = await opened.ReceiveAsync(HeartbeatTimeout, stop.Token).ConfigureAwait(false);
                switch (TypeOf(message))
                {
                    case "pong" when message["sent"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                        LatencyMs = Math.Max(0, clock.Elapsed.TotalMilliseconds - message["sent"]!.GetValue<double>());
                        Raise();
                        break;
                    case "error": DeviceLink.ThrowIfError(message); break;
                    case "hotkey-status": HotkeyStatus = Text(message, "text"); Raise(); break;
                    case "toggle-game": ToggleGameRequested?.Invoke(); break;
                    case var _ when Number(message, "reply-to") is { } id:
                        if (pending.TryRemove(id, out var waiting)) waiting.TrySetResult(message);
                        break;
                    default: MessageReceived?.Invoke(message); break;
                }
            }
        }
        catch (EndOfStreamException) { }
        finally
        {
            stop.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (Exception) { }
        }
    }

    // A user-facing reason for errors that end a connection attempt; null for unexpected errors.
    private static string? Describe(Exception error) => error switch
    {
        ConnectionRefusedException refused => refused.Message,
        TimeoutException timeout => timeout.Message.Length > 0 ? timeout.Message : "主机没有响应",
        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "主机没有执行连接助手，或防火墙挡住了连接",
            SocketError.HostNotFound or SocketError.NoData => "找不到这个主机位址",
            _ => "连不到主机：" + socket.Message
        },
        AuthenticationException auth => "加密连接失败：" + auth.Message,
        EndOfStreamException or ObjectDisposedException => "主机中断了连接",
        IOException io => "连接中断：" + io.Message,
        InvalidDataException data => "主机响应不正确：" + data.Message,
        FormatException => "主机响应不正确",
        _ => null
    };

    private void Set(LinkState state, string status, string? connectedName)
    {
        State = state; Status = status; ConnectedName = connectedName;
        Raise();
    }
    private void Raise() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        StopLoop(Message("bye"));
        try { await loop.ConfigureAwait(false); } catch (Exception) { }
    }
}
