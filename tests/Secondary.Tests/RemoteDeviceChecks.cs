using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Secondary.Core;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

// Connection mode, stage 3: main-computer media and shortcuts, over loopback against a stand-in helper.
internal static class RemoteDeviceChecks
{
    internal static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static readonly RemoteMedia Song = new("Spotify.exe", "Clair de Lune", "Debussy", true, true, false, true, 42, 300, "cover-1");

    // Answers media and shortcut messages the way the connection helper does, with recorded calls.
    private sealed class FakeHelper
    {
        public int WatchOn, WatchOff, ArtworkRequests, Cancels;
        public string? LastCommand;
        public volatile bool HoldRuns;
        public List<ShortcutEntry> Entries = [new("game", "Game", "主電腦", "🎮", [new("Open", @"C:\Games\game.exe")])];
        public async Task Handle(string device, JsonObject message, FrameChannel channel, CancellationToken token)
        {
            switch (TypeOf(message))
            {
                case "media-watch" when message["on"]?.GetValue<bool>() == true:
                    WatchOn++;
                    await channel.SendAsync(RemoteMediaMessages.State(Song, "播放中"), token);
                    break;
                case "media-watch": WatchOff++; break;
                case "media-control":
                    LastCommand = Text(message, "command");
                    await channel.SendAsync(Reply(message, Message("media-reply", ("ok", true))), token);
                    break;
                case "media-artwork":
                    ArtworkRequests++;
                    var data = Text(message, "key") == "huge" ? Convert.ToBase64String(new byte[RemoteMediaMessages.MaxArtworkBytes + 10]) : Convert.ToBase64String([1, 2, 3]);
                    await channel.SendAsync(Reply(message, Message("media-reply", ("key", Text(message, "key")), ("data", data))), token);
                    break;
                case "shortcuts":
                    await channel.SendAsync(Reply(message, RemoteShortcuts.List(Entries, ["game"], null)), token);
                    break;
                case "shortcut-save":
                    var entry = RemoteShortcuts.ReadEntry(message["entry"]);
                    if (entry is not null) Entries.Add(entry);
                    await channel.SendAsync(Reply(message, Message("shortcut-reply", ("ok", entry is not null), ("error", entry is null ? "目標需為本機絕對路徑或 HTTP／HTTPS 網址。" : null))), token);
                    break;
                case "shortcut-run":
                    var run = Text(message, "run")!;
                    _ = Task.Run(async () =>
                    {
                        await channel.SendAsync(Message("shortcut-status", ("run", run), ("text", "等待主電腦確認：請到主電腦上確認或取消。")), token);
                        while (HoldRuns && !token.IsCancellationRequested && Cancels == 0) await Task.Delay(20, token);
                        var cancelled = Cancels > 0;
                        if (!cancelled) await channel.SendAsync(Message("shortcut-status", ("run", run), ("text", "在主電腦執行「Game」· 步驟 1/1")), token);
                        await channel.SendAsync(Reply(message, Message("shortcut-result", ("ok", !cancelled), ("text", cancelled ? "已取消" : "已在主電腦完成：Game"))), token);
                    }, token).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                case "shortcut-cancel": Cancels++; break;
            }
        }
    }

    private static async Task<bool> Until(Func<bool> done, int milliseconds = 5000)
    {
        for (var waited = 0; waited < milliseconds && !done(); waited += 20) await Task.Delay(20);
        return done();
    }
    private static JsonObject Parse(JsonObject message) => (JsonObject)JsonNode.Parse(message.ToJsonString())!;

    private static async Task RunAsync(Action<bool, string> check)
    {
        CheckMessages(check);
        var root = Path.Combine(Path.GetTempPath(), "Secondary.RemoteDevice-" + Guid.NewGuid().ToString("N"));
        try { await CheckLoopback(check, root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void CheckMessages(Action<bool, string> check)
    {
        check(RemoteMediaMessages.ReadState(Parse(RemoteMediaMessages.State(Song, "播放中"))) is { Media: var media, Status: "播放中" } && media == Song, "A media state survives the wire");
        check(RemoteMediaMessages.ReadState(Parse(RemoteMediaMessages.State(null, "主電腦目前沒有播放中的媒體"))) is { Media: null }, "No player is reported as no media");
        check(RemoteMediaMessages.ReadState(Parse(RemoteMediaMessages.State(Song with { Title = new string('a', 400) }, "x"))) is null &&
            RemoteMediaMessages.ReadState(Parse(RemoteMediaMessages.State(Song with { Position = -1 }, "x"))) is null, "Oversized titles and negative positions are rejected");

        var approved = new ShortcutEntry("a", "App", Steps: [new("Open", @"C:\Tools\app.exe")], ApprovedFingerprint: "ABC");
        check(RemoteShortcuts.Entry(approved)["ApprovedFingerprint"] is null && RemoteShortcuts.ReadEntry(RemoteShortcuts.Entry(approved)) is { ApprovedFingerprint: null, Name: "App" },
            "Approval never travels with a shortcut");
        var forged = (JsonObject)RemoteShortcuts.Entry(approved); forged["ApprovedFingerprint"] = "FORGED";
        check(RemoteShortcuts.ReadEntry(forged)?.ApprovedFingerprint is null, "A device cannot mark a shortcut approved");
        check(RemoteShortcuts.ReadEntry(RemoteShortcuts.Entry(approved with { Steps = [new("Open", "relative\\app.exe")] })) is null &&
            RemoteShortcuts.ReadEntry(RemoteShortcuts.Entry(approved with { Steps = [new("Open", @"C:\x\run.vbs")] })) is null &&
            RemoteShortcuts.ReadEntry(RemoteShortcuts.Entry(approved with { Steps = [new("Open", @"\\server\share\app.exe")] })) is null,
            "Shortcuts must follow the Windows rules: local absolute paths, no other script types, no network paths");
        var list = RemoteShortcuts.ReadList(Parse(RemoteShortcuts.List([approved, approved, approved with { Id = "b", Steps = [new("Open", "nope")] }], ["a"], null)));
        check(list.Entries.Length == 1 && list.Approved.Contains("a"), "Shortcut lists drop duplicates and invalid entries");
    }

    private static async Task CheckLoopback(Action<bool, string> check, string root)
    {
        int discoveryPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var helperIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "helper", "identity.pfx"), "Main PC");
        var devices = new PairedStore(Path.Combine(root, "helper", "devices.json"));
        var helper = new FakeHelper();
        var server = new HelperServer(helperIdentity, devices, "Main PC", IPAddress.Loopback, 0, discoveryPort) { MessageHandler = (d, m, c, t) => helper.Handle(d, m, c, t) };
        server.Start();
        using var deviceIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "device", "identity.pfx"), "Surface");
        var helpers = new PairedStore(Path.Combine(root, "device", "helpers.json"));
        var controller = new ConnectionController(deviceIdentity, helpers, "Surface", () => [new IPEndPoint(IPAddress.Loopback, discoveryPort)], TimeSpan.FromMilliseconds(400));
        using var media = new RemoteMediaClient(controller);
        try
        {
            check(media.Status == "主電腦未連線" && media.Media is null, "Before connecting, main-computer media is unavailable");
            var paired = await DeviceLink.PairAsync(deviceIdentity, helpers, "Surface", "127.0.0.1", server.Port, int.Parse(server.StartPairing()), CancellationToken.None);
            controller.Connect(paired.Fingerprint);
            check(await Until(() => controller.State == LinkState.Connected), "The device connects");
            await Task.Delay(200);
            check(helper.WatchOn == 0, "Nothing is reported until a widget watches");

            var changes = 0;
            var first = media.Watch(() => Interlocked.Increment(ref changes));
            var second = media.Watch(() => { });
            check(await Until(() => media.Media?.Title == "Clair de Lune") && helper.WatchOn == 1 && changes > 0, "A watching widget gets the main computer's player, and two widgets share one subscription");
            check(await media.ControlAsync("toggle") && helper.LastCommand == "toggle", "Media controls reach the main computer");
            var threw = false;
            try { await media.ControlAsync("format-disk"); } catch (ArgumentException) { threw = true; }
            check(threw, "Only previous, toggle and next are sent");
            check((await media.ArtworkAsync("cover-1"))?.SequenceEqual(new byte[] { 1, 2, 3 }) == true && await media.ArtworkAsync("cover-1") is not null && helper.ArtworkRequests == 1,
                "The cover is fetched once per key");
            check(await media.ArtworkAsync("huge") is null, "A cover larger than the limit is refused");
            first.Dispose();
            await Task.Delay(150);
            check(helper.WatchOff == 0, "The subscription stays while a widget still watches");
            second.Dispose();
            check(await Until(() => helper.WatchOff == 1), "The last widget leaving stops the main computer's subscription");

            using var again = media.Watch(() => { });
            check(await Until(() => helper.WatchOn == 2), "Watching again resubscribes");
            controller.Disconnect();
            check(await Until(() => media.Media is null && media.Status == "主電腦未連線"), "A lost connection clears the main computer's player");
            controller.Connect(paired.Fingerprint);
            check(await Until(() => helper.WatchOn == 3 && media.Media is not null), "After reconnecting, a watching widget resubscribes");

            var clash = false;
            try { await controller.RequestAsync(Message("shortcut-delete", ("id", "game")), TimeSpan.FromSeconds(1)); } catch (ArgumentException) { clash = true; }
            check(clash, "A request cannot carry its own id field, which the request number would overwrite");
            var shortcuts = await RemoteShortcuts.ListAsync(controller);
            check(shortcuts.Entries.Single().Name == "Game" && shortcuts.Approved.Contains("game"), "The device lists the main computer's shortcuts");
            check(await RemoteShortcuts.SaveAsync(controller, new ShortcutEntry("web", "Docs", Steps: [new("Open", "https://example.com/")])) is null && helper.Entries.Count == 2, "A device can add a shortcut to the main computer");
            check(await RemoteShortcuts.SaveAsync(controller, new ShortcutEntry("bad", "Bad", Steps: [new("Open", "app.exe")])) is { } refused && refused.Contains("本機絕對路徑", StringComparison.Ordinal),
                "The main computer's refusal is shown to the device");

            var progress = new List<string>();
            var result = await RemoteShortcuts.RunAsync(controller, "game", text => { lock (progress) progress.Add(text); });
            check(result is (true, "已在主電腦完成：Game") && progress.Any(p => p.StartsWith("等待主電腦確認", StringComparison.Ordinal)) && progress.Any(p => p.Contains("步驟 1/1", StringComparison.Ordinal)),
                "Running a shortcut reports waiting for confirmation, the steps and the result");
            helper.HoldRuns = true;
            using (var cancel = new CancellationTokenSource())
            {
                var held = RemoteShortcuts.RunAsync(controller, "game", _ => cancel.Cancel(), cancel.Token);
                check((await held) is (false, "已取消") && helper.Cancels == 1, "Cancelling on the device stops the run on the main computer");
            }
            helper.Cancels = 0;
            var lost = RemoteShortcuts.RunAsync(controller, "game", _ => { });
            await Until(() => helper.HoldRuns && server.ActiveDevice is not null);
            await Task.Delay(200);
            controller.Disconnect();
            check((await lost) is (false, var why) && why.Contains("連線中斷", StringComparison.Ordinal), "A lost connection ends the run and says so");
        }
        finally
        {
            await controller.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
