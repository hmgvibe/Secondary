using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

// Connection mode over loopback: identities, pairing, discovery, sessions and refusals.
internal static class ConnectionChecks
{
    internal static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
    private static async Task<bool> Until(Func<bool> done, int milliseconds = 5000)
    {
        for (var waited = 0; waited < milliseconds && !done(); waited += 20) await Task.Delay(20);
        return done();
    }
    private static async Task<string?> Refusal(Func<Task> action)
    {
        try { await action(); return null; }
        catch (ConnectionRefusedException error) { return error.Code; }
    }

    private static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Secondary.Connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckIdentity(check, root);
            CheckStore(check, root);
            await CheckPairingRounds(check);
            await CheckSessions(check, root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckIdentity(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "identity", "identity.pfx");
        string first;
        using (var created = ConnectionIdentity.LoadOrCreate(path, "Main PC")) first = created.Fingerprint;
        using (var loaded = ConnectionIdentity.LoadOrCreate(path, "Main PC"))
            check(loaded.Fingerprint == first && first.Length == 64 && !loaded.Recreated && loaded.Certificate.HasPrivateKey, "A connection identity is created once and keeps its fingerprint");
        File.WriteAllText(path, "not a certificate");
        using (var replaced = ConnectionIdentity.LoadOrCreate(path, "Main PC"))
            check(replaced.Recreated && replaced.Fingerprint != first && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt").Length == 1, "An unreadable identity is kept aside and a new one is made");
        if (!OperatingSystem.IsWindows())
            check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "The private key file is readable by its owner only");
        check(ConnectionIdentity.Short(first).Split(' ').Length == 4, "Fingerprints are shown as four groups for comparison");
        check(Pairing.TryParse(" 012 345 ", out var code) && code == 12345 && !Pairing.TryParse("12345", out _) && !Pairing.TryParse("12a456", out _), "Pairing codes are exactly six digits, spaces allowed");
        check(Enumerable.Range(0, 50).Select(_ => Pairing.NewCode()).All(c => c.Length == 6 && c.All(char.IsAsciiDigit)), "New pairing codes are six digits");
    }

    private static void CheckStore(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "store", "peers.json");
        var store = new PairedStore(path);
        string Fingerprint(int i) => i.ToString("X64");
        for (var i = 0; i < PairedStore.MaxPeers; i++) check(store.Save(new PairedPeer(Fingerprint(i), "PC " + i, DateTimeOffset.UtcNow)), "Pairing " + (i + 1) + " is stored");
        check(!store.Save(new PairedPeer(Fingerprint(99), "PC 99", DateTimeOffset.UtcNow)), "A ninth pairing is refused");
        check(store.Save(new PairedPeer(Fingerprint(3), "Renamed", DateTimeOffset.UtcNow)) && store.Find(Fingerprint(3))!.Name == "Renamed", "Saving a known fingerprint replaces it");
        store.SetAutoConnect(Fingerprint(2));
        check(new PairedStore(path).Peers.Single(p => p.AutoConnect).Fingerprint == Fingerprint(2), "Pairings and the auto-connect choice survive a restart");
        check(store.Remove(Fingerprint(2)) && store.Find(Fingerprint(2)) is null && !store.Remove(Fingerprint(2)), "Pairings can be removed");
        File.WriteAllText(path, "{ broken");
        var recovered = new PairedStore(path);
        check(recovered.Peers.Count == 0 && recovered.LoadError is not null && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt").Length == 1, "A corrupt pairing file is kept aside and reported");
    }

    // The commit-reveal rounds over a plain loopback socket: matching codes and views succeed, anything else fails.
    private static async Task CheckPairingRounds(Action<bool, string> check)
    {
        async Task<(bool Device, bool Helper)> Run(int deviceCode, int helperCode, string deviceSeesHelper)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var server = await listener.AcceptTcpClientAsync();
            var device = new FrameChannel(client.GetStream());
            var helper = new FrameChannel(server.GetStream());
            async Task<bool> Safe(Task<bool> run) { try { return await run; } catch (ConnectionRefusedException) { return false; } catch (IOException) { return false; } catch (ObjectDisposedException) { return false; } }
            var deviceRun = Safe(Pairing.RunAsync(device, true, deviceCode, "D".PadLeft(64, 'D'), deviceSeesHelper, CancellationToken.None));
            var helperRun = Safe(Pairing.RunAsync(helper, false, helperCode, "H".PadLeft(64, 'H'), "D".PadLeft(64, 'D'), CancellationToken.None));
            var helperResult = await helperRun;
            if (!helperResult) client.Close();
            var deviceResult = await deviceRun;
            if (!deviceResult) server.Close();
            return (deviceResult, helperResult);
        }
        var helperFingerprint = "H".PadLeft(64, 'H');
        check(await Run(123456, 123456, helperFingerprint) == (true, true), "Matching codes pair in 20 commit-reveal rounds");
        check(await Run(123456, 123457, helperFingerprint) is (false, false), "A code differing in one digit fails on both sides");
        check(await Run(123456, 123456, "M".PadLeft(64, 'M')) is (false, false), "A different certificate in the middle fails even with the right code");
    }

    private static async Task CheckSessions(Action<bool, string> check, string root)
    {
        var discoveryPort = FreeUdpPort();
        using var helperIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "helper", "identity.pfx"), "Main PC");
        var devicesStore = new PairedStore(Path.Combine(root, "helper", "devices.json"));
        var server = new HelperServer(helperIdentity, devicesStore, "Main PC", IPAddress.Loopback, 0, discoveryPort);
        server.Start();
        var targets = new[] { new IPEndPoint(IPAddress.Loopback, discoveryPort) };
        using var deviceIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "device", "identity.pfx"), "Surface");
        var helpersStore = new PairedStore(Path.Combine(root, "device", "helpers.json"));
        var host = "127.0.0.1";
        try
        {
            var found = await DeviceLink.DiscoverAsync(targets, TimeSpan.FromMilliseconds(600), CancellationToken.None);
            check(found.Count == 1 && found[0] is { Name: "Main PC", Port: var port, Protocol: ProtocolVersion } && port == server.Port && found[0].Fingerprint == helperIdentity.Fingerprint,
                "Discovery finds the helper with its name, port and fingerprint");
            var stranger = new PairedPeer(helperIdentity.Fingerprint, "Main PC", DateTimeOffset.UtcNow, host, server.Port);
            check(await Refusal(() => DeviceLink.ConnectAsync(deviceIdentity, stranger, host, server.Port, "Surface", CancellationToken.None)) == "not-paired", "An unpaired device is refused");
            check(await Refusal(() => DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, 1, CancellationToken.None)) == "pairing-closed", "Pairing is refused until the user opens it on the main computer");

            var code = int.Parse(server.StartPairing());
            var wrong = (code + 1) % Pairing.CodeLimit;
            // Either side may notice the mismatch first, so the helper's count is awaited.
            check(await Refusal(() => DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, wrong, CancellationToken.None)) == "pair-failed"
                && await Until(() => server.PairingStatus.Contains("剩 2 次", StringComparison.Ordinal)), "A wrong code fails and counts down");
            await Refusal(() => DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, wrong, CancellationToken.None));
            await Until(() => server.PairingStatus.Contains("剩 1 次", StringComparison.Ordinal));
            await Refusal(() => DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, wrong, CancellationToken.None));
            check(await Until(() => server.PairingCode is null) && server.PairingStatus.Contains("作廢", StringComparison.Ordinal), "Three wrong codes void the pairing code");
            check(await Refusal(() => DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, code, CancellationToken.None)) == "pairing-closed", "The voided code no longer works");
            check(helpersStore.Peers.Count == 0 && devicesStore.Peers.Count == 0, "Failed pairing stores nothing on either side");

            code = int.Parse(server.StartPairing());
            var paired = await DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, code, CancellationToken.None);
            check(paired.Fingerprint == helperIdentity.Fingerprint && devicesStore.Find(deviceIdentity.Fingerprint)?.Name == "Surface" && server.PairingCode is null && server.PairingStatus.Contains("Surface", StringComparison.Ordinal),
                "The right code pairs both sides and closes pairing");

            var controller = new ConnectionController(deviceIdentity, helpersStore, "Surface", () => targets, TimeSpan.FromMilliseconds(600));
            await using (controller)
            {
                controller.Connect(paired.Fingerprint);
                check(await Until(() => controller.State == LinkState.Connected) && await Until(() => server.ActiveDevice == "Surface"), "A paired device connects and the helper shows it");
                check(await Until(() => controller.LatencyMs is not null) && controller.Helpers.Single().Connected, "Heartbeats measure the round trip");
                check(helpersStore.Find(paired.Fingerprint)!.AutoConnect, "The connected helper is remembered for the next start");

                using var otherIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "other", "identity.pfx"), "Laptop");
                devicesStore.Save(new PairedPeer(otherIdentity.Fingerprint, "Laptop", DateTimeOffset.UtcNow));
                check(await Refusal(() => DeviceLink.ConnectAsync(otherIdentity, paired, host, server.Port, "Laptop", CancellationToken.None)) == "busy", "Only one device is connected at a time");

                var (raw, _) = await DeviceLink.OpenAsync(deviceIdentity, host, server.Port, CancellationToken.None);
                await using (raw)
                {
                    await raw.SendAsync(Message("hello", ("protocol", 99), ("name", "Future"), ("intent", "connect")), CancellationToken.None);
                    var reply = await raw.ReceiveAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                    check(Text(reply, "code") == "version" && Text(reply, "message")!.Contains("版本不相容", StringComparison.Ordinal), "A different protocol version is refused with an explanation");
                }

                var impostor = paired with { Fingerprint = new string('A', 64) };
                check(await Refusal(() => DeviceLink.ConnectAsync(deviceIdentity, impostor, host, server.Port, "Surface", CancellationToken.None)) == "identity-changed", "A helper whose certificate changed is not trusted");

                // The helper removing the device ends the session, and the device stops retrying.
                server.Forget(deviceIdentity.Fingerprint);
                check(await Until(() => controller.State == LinkState.Failed, 10000) && helpersStore.Find(paired.Fingerprint) is null && controller.Status.Contains("移除", StringComparison.Ordinal),
                    "Removal on the main computer disconnects the device and drops its pairing");

                // Pair again, then forget from the device side: the helper drops the device too.
                code = int.Parse(server.StartPairing());
                paired = await DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, code, CancellationToken.None);
                controller.Connect(paired.Fingerprint);
                check(await Until(() => controller.State == LinkState.Connected), "A re-paired device connects again");
                controller.Forget(paired.Fingerprint);
                check(await Until(() => devicesStore.Find(deviceIdentity.Fingerprint) is null) && await Until(() => server.ActiveDevice is null) && controller.State == LinkState.Idle,
                    "Forgetting on the device also removes it on the main computer");

                code = int.Parse(server.StartPairing());
                paired = await DeviceLink.PairAsync(deviceIdentity, helpersStore, "Surface", host, server.Port, code, CancellationToken.None);
                controller.Connect(paired.Fingerprint);
                await Until(() => controller.State == LinkState.Connected);
                controller.Disconnect();
                check(await Until(() => server.ActiveDevice is null) && controller.State == LinkState.Idle && !helpersStore.Find(paired.Fingerprint)!.AutoConnect,
                    "Disconnecting ends the session and is not auto-connected next time");
                controller.Connect(paired.Fingerprint);
                await Until(() => controller.State == LinkState.Connected);
            }

            // A helper that goes away leaves the device retrying, not failed.
            var retrying = new ConnectionController(deviceIdentity, helpersStore, "Surface", () => targets, TimeSpan.FromMilliseconds(300));
            await using (retrying)
            {
                retrying.Start();
                check(await Until(() => retrying.State == LinkState.Connected), "Start reconnects to the remembered helper");
                await server.DisposeAsync();
                check(await Until(() => retrying.State == LinkState.Waiting, 25000) && retrying.Status.Contains("秒後重試", StringComparison.Ordinal), "Losing the helper leads to automatic retries");
            }
        }
        finally { await server.DisposeAsync(); }
    }
}
