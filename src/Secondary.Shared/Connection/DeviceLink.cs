using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

public sealed record DiscoveredHelper(string Name, string Address, int Port, string Fingerprint, int Protocol);

// The device side of one conversation with a helper: search, pair and open an authenticated session.
public static class DeviceLink
{
    // Broadcast on every IPv4 network the device is on, plus the limited broadcast address.
    public static IReadOnlyList<IPEndPoint> BroadcastTargets(int port = DiscoveryPort)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    var address = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                    targets.Add(new IPEndPoint(new IPAddress(broadcast), port));
                }
            }
        }
        catch (NetworkInformationException) { }
        return targets.DistinctBy(t => t.ToString()).ToArray();
    }

    public static async Task<IReadOnlyList<DiscoveredHelper>> DiscoverAsync(IEnumerable<IPEndPoint> targets, TimeSpan wait, CancellationToken token)
    {
        var found = new Dictionary<string, DiscoveredHelper>(StringComparer.Ordinal);
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var request = Encoding.ASCII.GetBytes(DiscoverRequest);
        var endpoints = targets.ToArray();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(wait);
        // Ask three times in case one broadcast is dropped.
        async Task Ask()
        {
            for (var i = 0; i < 3 && !limit.IsCancellationRequested; i++)
            {
                foreach (var endpoint in endpoints)
                {
                    try { await udp.SendAsync(request, endpoint, limit.Token).ConfigureAwait(false); }
                    catch (SocketException) { }
                }
                try { await Task.Delay(wait / 4, limit.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
        var asking = Ask();
        while (!limit.IsCancellationRequested)
        {
            UdpReceiveResult reply;
            try { reply = await udp.ReceiveAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            try
            {
                if (JsonNode.Parse(reply.Buffer) is not JsonObject message || TypeOf(message) != "secondary-helper") continue;
                if (Text(message, "fingerprint") is not { Length: 64 } fingerprint || Number(message, "port") is not long port || port is <= 0 or >= 65536) continue;
                found[fingerprint] = new(HelperServer.Clean(Text(message, "name")), reply.RemoteEndPoint.Address.ToString(), (int)port, fingerprint, (int)(Number(message, "protocol") ?? 0));
            }
            catch (System.Text.Json.JsonException) { }
        }
        await asking.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return found.Values.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // Opens TLS to a helper; the helper's fingerprint is checked by the caller against what was paired.
    public static async Task<(FrameChannel Channel, string Fingerprint)> OpenAsync(ConnectionIdentity identity, string host, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("連不到 " + host + "：逾時"); }
            }
            var ssl = new SslStream(client.GetStream(), false);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                limit.CancelAfter(HandshakeTimeout);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "secondary", ClientCertificateContext = identity.Context,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }, limit.Token).ConfigureAwait(false);
            }
            if (ssl.RemoteCertificate is not { } certificate) throw new AuthenticationException("主電腦沒有提供憑證");
            return (new FrameChannel(ssl), ConnectionIdentity.FingerprintOf(certificate));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Pairs with the helper at host:port using the code shown on the main computer. Returns the stored helper.
    public static async Task<PairedPeer> PairAsync(ConnectionIdentity identity, PairedStore helpers, string deviceName, string host, int port, int code, CancellationToken token)
    {
        var (channel, fingerprint) = await OpenAsync(identity, host, port, token).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            await channel.SendAsync(Message("hello", ("protocol", ProtocolVersion), ("name", deviceName), ("intent", "pair")), token).ConfigureAwait(false);
            var start = await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false);
            ThrowIfError(start);
            if (TypeOf(start) != "pair-start") throw new InvalidDataException("主電腦沒有開始配對");
            if (!await Pairing.RunAsync(channel, true, code, identity.Fingerprint, fingerprint, token).ConfigureAwait(false))
                throw new ConnectionRefusedException("pair-failed", "配對碼不符");
            await channel.SendAsync(Message("pair-confirm"), token).ConfigureAwait(false);
            var done = await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false);
            ThrowIfError(done);
            if (TypeOf(done) != "pair-ok") throw new InvalidDataException("主電腦沒有確認配對");
            var peer = new PairedPeer(fingerprint, HelperServer.Clean(Text(done, "name") ?? Text(start, "name")), DateTimeOffset.UtcNow, host, port);
            if (!helpers.Save(peer)) throw new ConnectionRefusedException("full", $"這臺設備已配對 {PairedStore.MaxPeers} 臺主電腦；請先移除不用的。");
            return peer;
        }
    }

    // Opens a session with a paired helper. A different fingerprint at that address is refused, never trusted.
    public static async Task<FrameChannel> ConnectAsync(ConnectionIdentity identity, PairedPeer helper, string host, int port, string deviceName, CancellationToken token)
    {
        var (channel, fingerprint) = await OpenAsync(identity, host, port, token).ConfigureAwait(false);
        try
        {
            if (fingerprint != helper.Fingerprint)
                throw new ConnectionRefusedException("identity-changed", "這個位址的主電腦身分與配對時不同；為安全起見不連線。若主電腦重新安裝過，請移除後重新配對。");
            await channel.SendAsync(Message("hello", ("protocol", ProtocolVersion), ("name", deviceName), ("intent", "connect")), token).ConfigureAwait(false);
            var welcome = await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false);
            ThrowIfError(welcome);
            if (TypeOf(welcome) != "welcome") throw new InvalidDataException("主電腦回應不正確");
            return channel;
        }
        catch
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void ThrowIfError(JsonObject message)
    {
        if (TypeOf(message) == "error") throw new ConnectionRefusedException(Text(message, "code") ?? "error", Text(message, "message") ?? "主電腦拒絕連線");
    }
}
