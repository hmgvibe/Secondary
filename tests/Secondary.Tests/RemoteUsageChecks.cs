using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Secondary.Shared;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

// Connection mode, stage 4: main-computer AI usage over loopback against a stand-in helper.
internal static class RemoteUsageChecks
{
    internal static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static readonly DateTimeOffset Resets = DateTimeOffset.FromUnixTimeSeconds(1792516800);
    private static readonly AiUsageSnapshot Sample = new(
        new ProviderUsage(new UsageWindow(49, Resets), new UsageWindow(100, Resets.AddDays(4), true), Resets.AddHours(-1)),
        new ProviderUsage(null, new UsageWindow(6.5, Resets.AddDays(2)), Resets.AddHours(-2)), "主電腦未允許線上用量（在連接助手選單開啟）");

    private sealed class FakeHelper
    {
        public readonly List<UsageRequest?> Requests = [];
        public volatile bool Silent;
        public DateTimeOffset Clock = DateTimeOffset.UtcNow.AddMinutes(3);
        public async Task Handle(string device, JsonObject message, FrameChannel channel, CancellationToken token)
        {
            if (TypeOf(message) != "usage-watch") return;
            var request = RemoteUsageMessages.ReadWatch(message);
            lock (Requests) Requests.Add(request);
            if (request is not null && !Silent) await channel.SendAsync(RemoteUsageMessages.State(Sample, Clock, "主電腦用量"), token);
        }
        public UsageRequest?[] Seen { get { lock (Requests) return Requests.ToArray(); } }
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
        var root = Path.Combine(Path.GetTempPath(), "Secondary.RemoteUsage-" + Guid.NewGuid().ToString("N"));
        try { await CheckLoopback(check, root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void CheckMessages(Action<bool, string> check)
    {
        var sent = DateTimeOffset.FromUnixTimeMilliseconds(1792500000123);
        var state = RemoteUsageMessages.ReadState(Parse(RemoteUsageMessages.State(Sample, sent, "主電腦用量")));
        check(state is { Sent: var at, Status: "主電腦用量", Snapshot: var snapshot } && at == sent && snapshot.ClaudeOnlineProblem == Sample.ClaudeOnlineProblem &&
            snapshot.Claude == Sample.Claude && snapshot.Codex == Sample.Codex, "A usage state survives the wire");
        check(RemoteUsageMessages.ReadState(Parse(RemoteUsageMessages.State(new(new(null, null, null), new(null, null, null)), sent, "x"))) is { Snapshot: { Claude.HasData: false, Codex.HasData: false, ClaudeOnlineProblem: null } },
            "An empty usage state is reported as no data");
        foreach (var (name, change) in new (string, Action<JsonObject>)[]
        {
            ("percentage above 100", m => m["claude"]!["five-hour"]!["used"] = 140),
            ("negative percentage", m => m["codex"]!["weekly"]!["used"] = -1),
            ("text percentage", m => m["codex"]!["weekly"]!["used"] = "50"),
            ("reset beyond year 9999", m => m["claude"]!["weekly"]!["resets"] = 999999999999),
            ("non-boolean reached", m => m["claude"]!["weekly"]!["reached"] = "yes"),
            ("missing provider", m => m.Remove("codex")),
            ("oversized status", m => m["status"] = new string('x', 400)),
            ("oversized problem", m => m["claude-online-problem"] = new string('x', 400)),
            ("missing sent time", m => m.Remove("sent")),
        })
        {
            var message = Parse(RemoteUsageMessages.State(Sample, sent, "主電腦用量"));
            change(message);
            check(RemoteUsageMessages.ReadState(message) is null, "A usage state with " + name + " is rejected");
        }
        check(RemoteUsageMessages.ReadWatch(Parse(RemoteUsageMessages.Watch(new UsageRequest(true, false, true)))) == new UsageRequest(true, false, true) &&
            RemoteUsageMessages.ReadWatch(Parse(RemoteUsageMessages.Watch(null))) is null, "Usage watch requests carry which services to read");
        check(new UsageRequest(true, false, false).Or(new UsageRequest(false, true, true)) == new UsageRequest(true, true, true), "Requests from several widgets combine");
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
        using var usage = new RemoteUsageClient(controller, answerTimeout: TimeSpan.FromMilliseconds(600));
        try
        {
            check(usage.Status == "主電腦未連線" && usage.Snapshot is null && !usage.Configured, "Before pairing, main-computer usage is unavailable and connection mode is not set up");
            var paired = await DeviceLink.PairAsync(deviceIdentity, helpers, "Surface", "127.0.0.1", server.Port, int.Parse(server.StartPairing()), CancellationToken.None);
            controller.Connect(paired.Fingerprint);
            check(await Until(() => controller.State == LinkState.Connected) && usage.Configured, "The device connects and counts as set up for connection mode");
            await Task.Delay(200);
            check(helper.Seen.Length == 0, "Nothing is read on the main computer until a widget watches");

            var changes = 0;
            var codexOnly = usage.Watch(new UsageRequest(false, true, false), () => Interlocked.Increment(ref changes));
            check(await Until(() => usage.Snapshot is not null) && helper.Seen is [{ Claude: false, Codex: true, ClaudeOnline: false }] && changes > 0,
                "A watching widget asks only for the services it shows and gets the main computer's usage");
            check(usage.Snapshot!.Claude == Sample.Claude && usage.Status == "主電腦用量", "The device keeps the reported usage and status");
            check(Math.Abs((usage.MainNow - helper.Clock).TotalSeconds) < 5, "Times are shown on the main computer's clock");
            var claudeOnline = usage.Watch(new UsageRequest(true, false, true), () => { });
            check(await Until(() => helper.Seen.Length == 2) && helper.Seen[1] == new UsageRequest(true, true, true), "A second widget widens the one request to both services");
            claudeOnline.Dispose();
            check(await Until(() => helper.Seen.Length == 3) && helper.Seen[2] == new UsageRequest(false, true, false), "When a widget leaves, the request narrows again");
            codexOnly.Dispose();
            check(await Until(() => helper.Seen.Length == 4) && helper.Seen[3] is null, "The last widget leaving stops the main computer's reading");

            using (usage.Watch(new UsageRequest(true, true, false), () => { }))
            {
                check(await Until(() => helper.Seen.Length == 5), "Watching again asks the main computer again");
                controller.Disconnect();
                check(await Until(() => usage.Status == "主電腦未連線" && usage.Snapshot is null), "Disconnecting clears the main computer's usage");
                controller.Connect(paired.Fingerprint);
                check(await Until(() => helper.Seen.Length == 6 && usage.Snapshot is not null) && helper.Seen[5] == new UsageRequest(true, true, false),
                    "After reconnecting, a still-watching widget's request is sent again");
            }
            check(await Until(() => helper.Seen.Length == 7), "Leaving after the reconnect stops the reading");

            // A helper from before this stage never answers.
            helper.Silent = true;
            controller.Disconnect();
            check(await Until(() => usage.Snapshot is null), "The old reading is gone after disconnecting");
            controller.Connect(paired.Fingerprint);
            check(await Until(() => controller.State == LinkState.Connected), "The device reconnects");
            using (usage.Watch(new UsageRequest(true, true, false), () => { }))
                check(await Until(() => usage.Status.Contains("版本較舊", StringComparison.Ordinal)) && usage.Snapshot is null, "A helper that never answers is reported as too old");
        }
        finally
        {
            await controller.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
