using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// The connection helper's network side: answers discovery, pairs devices while the user has pairing open,
// and keeps at most one paired device connected. It never samples anything by itself.
public sealed class HelperServer(ConnectionIdentity identity, PairedStore devices, string name,
    IPAddress? bind = null, int port = TcpPort, int discoveryPort = DiscoveryPort, Func<DateTimeOffset>? clock = null) : IAsyncDisposable
{
    private sealed class Session(FrameChannel channel, string fingerprint, string name, string address)
    {
        public FrameChannel Channel => channel;
        public string Fingerprint => fingerprint;
        public string Name => name;
        public string Address => address;
        public CancellationTokenSource Stop { get; } = new();
    }
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim handshakes = new(4, 4);
    private TcpListener? listener;
    private UdpClient? discovery;
    private Session? active;
    private string? pairingCode;
    private DateTimeOffset pairingExpires;
    private int pairingFailures;
    private bool pairingBusy;
    private Task accepting = Task.CompletedTask, answering = Task.CompletedTask;
    public const int MaxPairingFailures = 3;

    public event Action? Changed;
    public int Port { get; private set; }
    public string Name => name;
    public string Fingerprint => identity.Fingerprint;
    public string? DiscoveryError { get; private set; }
    public string? ActiveDevice { get { lock (gate) return active?.Name; } }
    public string? ActiveAddress { get { lock (gate) return active?.Address; } }
    public string? PairingCode { get { lock (gate) return PairingOpen ? pairingCode : null; } }
    public DateTimeOffset PairingExpires { get { lock (gate) return pairingExpires; } }
    public string PairingStatus { get; private set; } = "未開始配對";
    private bool PairingOpen => pairingCode is not null && clock() < pairingExpires;
    // Messages after the handshake, for later stages; the session's fingerprint identifies the device.
    public Func<string, JsonObject, FrameChannel, CancellationToken, Task>? MessageHandler { get; set; }

    public void Start()
    {
        listener = new TcpListener(bind ?? IPAddress.Any, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            discovery = new UdpClient(new IPEndPoint(bind ?? IPAddress.Any, discoveryPort)) { EnableBroadcast = true };
            answering = Answer(discovery, stopping.Token);
        }
        catch (SocketException error) { DiscoveryError = "無法接收搜尋：" + error.Message + "；設備仍可手動輸入 IP。"; }
        accepting = Accept(listener, stopping.Token);
    }

    public string StartPairing(TimeSpan? duration = null)
    {
        lock (gate)
        {
            pairingCode = Pairing.NewCode();
            pairingExpires = clock() + (duration ?? TimeSpan.FromMinutes(2));
            pairingFailures = 0;
        }
        PairingStatus = "等待設備輸入配對碼";
        Changed?.Invoke();
        return pairingCode;
    }
    public void StopPairing(string status = "已關閉配對")
    {
        lock (gate) pairingCode = null;
        PairingStatus = status;
        Changed?.Invoke();
    }
    // Removing a device also ends its connection.
    public void Forget(string fingerprint)
    {
        devices.Remove(fingerprint);
        Session? session;
        lock (gate) session = active?.Fingerprint == fingerprint ? active : null;
        session?.Stop.Cancel();
    }

    private async Task Answer(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var request = await udp.ReceiveAsync(token).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(request.Buffer) != DiscoverRequest) continue;
                var reply = Encoding.UTF8.GetBytes(Message("secondary-helper", ("protocol", ProtocolVersion), ("name", name), ("port", Port), ("fingerprint", identity.Fingerprint)).ToJsonString());
                await udp.SendAsync(reply, request.RemoteEndPoint, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            // Windows reports an ICMP "port unreachable" from an earlier reply as a receive error; keep listening.
            catch (SocketException) { }
        }
    }

    private async Task Accept(TcpListener tcp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            // Unauthenticated connections are limited so a flood cannot exhaust the helper.
            if (!handshakes.Wait(0)) { client.Dispose(); continue; }
            _ = Handle(client, token);
        }
    }

    private async Task Handle(TcpClient client, CancellationToken token)
    {
        var released = false;
        void Release() { if (!released) { released = true; handshakes.Release(); } }
        FrameChannel? channel = null;
        try
        {
            client.NoDelay = true;
            var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
            var ssl = new SslStream(client.GetStream(), false);
            channel = new FrameChannel(ssl);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(HandshakeTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = identity.Context, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }, limit.Token).ConfigureAwait(false);
            }
            if (ssl.RemoteCertificate is not { } certificate) return;
            var device = ConnectionIdentity.FingerprintOf(certificate);
            var hello = await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false);
            if (TypeOf(hello) != "hello") return;
            if (Number(hello, "protocol") != ProtocolVersion)
            {
                await Refuse(channel, "version", $"版本不相容：連接助手使用協定 {ProtocolVersion}，設備使用 {Number(hello, "protocol")}。請把兩邊更新到同一版本。", token).ConfigureAwait(false);
                return;
            }
            var deviceName = Clean(Text(hello, "name"));
            if (Text(hello, "intent") == "pair") { await Pair(channel, device, deviceName, token).ConfigureAwait(false); return; }
            if (devices.Find(device) is not { } paired)
            {
                await Refuse(channel, "not-paired", "這臺設備尚未與主電腦配對，或配對已被移除。", token).ConfigureAwait(false);
                return;
            }
            Session? session = null, replaced = null;
            lock (gate)
            {
                if (active is not { } current || current.Fingerprint == device) { replaced = active; session = active = new Session(channel, device, deviceName, address); }
            }
            if (session is null)
            {
                await Refuse(channel, "busy", "主電腦正與「" + ActiveDevice + "」連線；同一時間只允許一臺設備。", token).ConfigureAwait(false);
                return;
            }
            // The same device reconnecting replaces its own stale session.
            replaced?.Stop.Cancel();
            Release();
            devices.Save(paired with { Name = deviceName, Address = address, LastSeen = clock() });
            await channel.SendAsync(Message("welcome", ("protocol", ProtocolVersion), ("name", name)), token).ConfigureAwait(false);
            Changed?.Invoke();
            await Serve(session, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or AuthenticationException or InvalidDataException or TimeoutException
            or OperationCanceledException or SocketException or ObjectDisposedException or ConnectionRefusedException or FormatException) { }
        finally
        {
            Release();
            var ended = false;
            lock (gate) if (active?.Channel == channel) { active = null; ended = true; }
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            if (ended) Changed?.Invoke();
        }
    }

    private async Task Serve(Session session, CancellationToken token)
    {
        // Handlers tie their work to this token, so it is cancelled however the session ends.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, session.Stop.Token);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var message = await session.Channel.ReceiveAsync(HeartbeatTimeout, stop.Token).ConfigureAwait(false);
                switch (TypeOf(message))
                {
                    case "ping": await session.Channel.SendAsync(Message("pong", ("sent", message["sent"]?.DeepClone())), stop.Token).ConfigureAwait(false); break;
                    case "unpair": devices.Remove(session.Fingerprint); return;
                    case "bye": return;
                    default:
                        if (MessageHandler is { } handler) await handler(session.Fingerprint, message, session.Channel, stop.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally { stop.Cancel(); }
    }

    // Sends to the connected device, if any; false when none is connected or the send failed.
    public async Task<bool> SendToActiveAsync(JsonObject message)
    {
        Session? session;
        lock (gate) session = active;
        if (session is null) return false;
        try { await session.Channel.SendAsync(message, session.Stop.Token).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { return false; }
    }

    private async Task Pair(FrameChannel channel, string device, string deviceName, CancellationToken token)
    {
        string? code;
        lock (gate)
        {
            code = PairingOpen && !pairingBusy ? pairingCode : null;
            if (code is not null) pairingBusy = true;
        }
        if (code is null)
        {
            var (reason, text) = pairingBusy ? ("busy", "另一臺設備正在配對，請稍候再試。")
                : pairingCode is null ? ("pairing-closed", "主電腦尚未開始配對：請在連接助手的系統匣選單按「開始配對」。")
                : ("pairing-expired", "配對碼已過期：請在主電腦重新開始配對。");
            await Refuse(channel, reason, text, token).ConfigureAwait(false);
            return;
        }
        try
        {
            PairingStatus = "「" + deviceName + "」正在配對";
            Changed?.Invoke();
            await channel.SendAsync(Message("pair-start", ("name", name)), token).ConfigureAwait(false);
            var matched = await Pairing.RunAsync(channel, false, int.Parse(code, System.Globalization.CultureInfo.InvariantCulture), identity.Fingerprint, device, token).ConfigureAwait(false);
            // The device confirms after checking this side's bits; only then is it trusted.
            if (matched && TypeOf(await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false)) == "pair-confirm")
            {
                if (!devices.Save(new PairedPeer(device, deviceName, clock())))
                {
                    await Refuse(channel, "full", $"已配對 {PairedStore.MaxPeers} 臺設備；請先在連接助手移除不用的設備。", token).ConfigureAwait(false);
                    StopPairing("已達配對上限");
                    return;
                }
                await channel.SendAsync(Message("pair-ok", ("name", name)), token).ConfigureAwait(false);
                StopPairing("已配對「" + deviceName + "」");
                return;
            }
            Failed();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ConnectionRefusedException or FormatException) { Failed(); throw; }
        finally { lock (gate) pairingBusy = false; }
    }
    private void Failed()
    {
        int failures;
        lock (gate) failures = ++pairingFailures;
        if (failures >= MaxPairingFailures) StopPairing("配對碼錯誤 3 次，已作廢；需要時請重新開始配對");
        else { PairingStatus = $"配對碼不符（剩 {MaxPairingFailures - failures} 次）"; Changed?.Invoke(); }
    }

    private static async Task Refuse(FrameChannel channel, string code, string message, CancellationToken token)
    {
        try { await channel.SendAsync(Message("error", ("code", code), ("message", message)), token).ConfigureAwait(false); }
        catch (IOException) { }
    }
    internal static string Clean(string? text)
    {
        var name = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length == 0 ? "未命名設備" : name.Length > 60 ? name[..60] : name;
    }

    private int disposed;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stopping.Cancel();
        listener?.Stop();
        discovery?.Dispose();
        Session? session;
        lock (gate) session = active;
        session?.Stop.Cancel();
        try { await Task.WhenAll(accepting, answering).ConfigureAwait(false); } catch (Exception) { }
        stopping.Dispose();
    }
}
