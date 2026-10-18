using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Secondary.Core;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

// Connection mode, stage 2: game snapshots, the remote process list and the synced hotkey, over loopback.
internal static class RemoteGameChecks
{
    internal static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    // A snapshot as the helper would send it while a game runs on an NVIDIA GPU.
    internal static RemoteGameSnapshot Sample(long session, double cpu = 37) => new(session, DateTimeOffset.UtcNow, false,
        cpu, 16UL << 30, 8UL << 30, 2048, 1024,
        TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available, "整機採樣 / 持續更新",
        55, TelemetrySourceState.Available, "GPU 引擎 / 持續更新",
        [
            new("NVIDIA GeForce RTX 4060 / GPU Core", "LibreHardwareMonitor 0.9.6", "/gpu-nvidia/0/load/0", DateTimeOffset.UtcNow, 64, "%", ReadingState.Available),
            new("NVIDIA GeForce RTX 4060 / GPU Core", "LibreHardwareMonitor 0.9.6", "/gpu-nvidia/0/temperature/0", DateTimeOffset.UtcNow, 61, "°C", ReadingState.Available),
            new("NVIDIA GeForce RTX 4060 / GPU Memory Used", "LibreHardwareMonitor 0.9.6", "/gpu-nvidia/0/smalldata/1", DateTimeOffset.UtcNow, 4096, "MB", ReadingState.Available),
            new("NVIDIA GeForce RTX 4060 / GPU Memory Total", "LibreHardwareMonitor 0.9.6", "/gpu-nvidia/0/smalldata/2", DateTimeOffset.UtcNow, 8192, "MB", ReadingState.Available),
            new("Intel Core i7 / CPU Package", "LibreHardwareMonitor 0.9.6", "/intelcpu/0/temperature/0", DateTimeOffset.UtcNow, 72, "°C", ReadingState.Available)
        ], TelemetrySourceState.Available, "硬體助手 / 持續採樣",
        new FrameStatistics("0x1A2B", 600, 120, 90, 8.33, false), TelemetrySourceState.Available, "PresentMon / 目前活動交換鏈", false);

    // Stands in for the connection helper: records requests and streams snapshots for the newest game-start.
    private sealed class FakeHelper
    {
        public RemoteGameRequest? Started;
        public int Starts, Stops;
        public string? Hotkey;
        public CancellationToken SessionToken;
        public volatile bool Streaming = true;
        public FrameChannel? Channel;
        public async Task Handle(string device, JsonObject message, FrameChannel channel, CancellationToken token)
        {
            SessionToken = token; Channel = channel;
            switch (TypeOf(message))
            {
                case "game-start":
                    var request = RemoteGame.ReadStart(message)!;
                    Started = request; Starts++;
                    _ = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested && Started == request)
                        {
                            if (Streaming) await channel.SendAsync(RemoteGame.Snapshot(Sample(request.Session)), token);
                            await Task.Delay(150, token);
                        }
                    }, token).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                case "game-stop": Stops++; Started = null; break;
                case "processes": await channel.SendAsync(Reply(message, RemoteGame.ProcessList([new(4242, 99, "fakegame"), new(7, 1, "explorer")])), token); break;
                case "hotkey":
                    Hotkey = Text(message, "gesture");
                    await channel.SendAsync(RemoteGame.HotkeyStatus(true, $"在主電腦按 {Hotkey} 也能進出遊戲模式。"), token);
                    break;
            }
        }
    }

    private static async Task<bool> Until(Func<bool> done, int milliseconds = 5000)
    {
        for (var waited = 0; waited < milliseconds && !done(); waited += 20) await Task.Delay(20);
        return done();
    }

    private static async Task RunAsync(Action<bool, string> check)
    {
        CheckMessages(check);
        var root = Path.Combine(Path.GetTempPath(), "Secondary.RemoteGame-" + Guid.NewGuid().ToString("N"));
        try { await CheckLoopback(check, root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void CheckMessages(Action<bool, string> check)
    {
        var request = new RemoteGameRequest(5, true, false, new RemoteProcess(4242, 638000000000000000, "game.exe"));
        check(RemoteGame.ReadStart(Parse(RemoteGame.Start(request))) == request, "A game request survives the wire");
        check(RemoteGame.ReadStart(Parse(RemoteGame.Start(request with { Target = null })))?.Target is null, "A request without a process monitors the whole machine");
        check(RemoteGame.ReadStart(Message("game-start", ("high", true))) is null, "A request without a session is ignored");

        var sample = Sample(5);
        var back = RemoteGame.ReadSnapshot(Parse(RemoteGame.Snapshot(sample)));
        check(back is { Session: 5, Cpu: 37, Gpu: 55, Frames.Fps: 120, Frames.LowOnePercent: 90 } && back.Sensors.Length == 5 && back.Frames.SwapChain == "0x1A2B" && back.TotalMemory == 16UL << 30,
            "A snapshot survives the wire with its readings and frame statistics");
        check(RemoteGame.ReadSnapshot(Parse(RemoteGame.Snapshot(sample with { Cpu = double.NaN, Frames = sample.Frames with { Fps = double.PositiveInfinity } }))) is { Cpu: null, Frames.Fps: null },
            "Values that are not finite are sent as missing");
        check(RemoteGame.ReadSnapshot(Parse(RemoteGame.Snapshot(sample with { Cpu = 150 }))) is null, "A CPU reading above 100% is rejected");
        check(RemoteGame.ReadSnapshot(Parse(RemoteGame.Snapshot(sample with { AvailableMemory = 32UL << 30 }))) is null, "More free memory than total memory is rejected");
        var badState = Parse(RemoteGame.Snapshot(sample));
        badState["snapshot"]!["FrameState"] = 42;
        check(RemoteGame.ReadSnapshot(badState) is null && RemoteGame.ReadSnapshot(Message("game-snapshot", ("snapshot", "nope"))) is null, "Unknown states and malformed snapshots are dropped");
        var many = sample with { Sensors = Enumerable.Range(0, 200).Select(i => sample.Sensors[0] with { Identity = "/gpu-nvidia/0/load/" + i }).ToArray() };
        check(RemoteGame.ReadSnapshot(Parse(RemoteGame.Snapshot(many)))?.Sensors.Length == RemoteGame.MaxSensors, "A snapshot carries at most 64 sensor readings");

        MonitoringReading Reading(string identity, string metric, double value, string unit) => new(metric, "LibreHardwareMonitor 0.9.6", identity, DateTimeOffset.UtcNow, value, unit, ReadingState.Available);
        var compact = RemoteGame.CompactSensors([
            Reading("/gpu-nvidia/0/load/0", "RTX / GPU Core", 50, "%"), Reading("/gpu-nvidia/0/load/1", "RTX / GPU Video Engine", 3, "%"),
            Reading("/intelcpu/0/temperature/1", "i7 / Core #1", 70, "°C"), Reading("/intelcpu/0/temperature/2", "i7 / Core #2", 81, "°C"),
            Reading("/intelcpu/0/clock/1", "i7 / Core #1", 4800, "MHz"), Reading("/intelcpu/0/load/0", "i7 / CPU Total", 30, "%"),
            Reading("/lpc/nct6798d/fan/0", "Board / Fan #1", 900, "RPM")]);
        check(compact.Length == 3 && compact.Any(r => r.Metric == "RTX / GPU Core") && compact.Single(r => r.Unit == "°C").Value == 81 && compact.Any(r => r.Unit == "MHz"),
            "Only the readings the game page shows are sent, with the hottest CPU value per unit");

        var list = RemoteGame.ProcessList(Enumerable.Range(1, 1500).Select(i => new RemoteProcess(i, i, "p" + i)));
        check(RemoteGame.ReadProcessList(Parse(list)).Length == RemoteGame.MaxProcesses, "Process lists are capped at 1000 entries");
        var hostile = Message("process-list", ("processes", new JsonArray(new JsonObject { ["id"] = -1, ["started"] = 1, ["name"] = "x" }, new JsonObject { ["id"] = 3, ["started"] = 1, ["name"] = "" }, new JsonObject { ["id"] = 4, ["started"] = 1, ["name"] = "ok" })));
        check(RemoteGame.ReadProcessList(Parse(hostile)).Select(p => p.Name).SequenceEqual(["ok"]), "Processes with invalid ids or names are skipped");
    }
    private static JsonObject Parse(JsonObject message) => (JsonObject)JsonNode.Parse(message.ToJsonString())!;

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
        try
        {
            var paired = await DeviceLink.PairAsync(deviceIdentity, helpers, "Surface", "127.0.0.1", server.Port, int.Parse(server.StartPairing()), CancellationToken.None);
            var toggles = 0;
            controller.ToggleGameRequested += () => Interlocked.Increment(ref toggles);
            controller.Hotkey = "Ctrl+Alt+Shift+F11";
            controller.Connect(paired.Fingerprint);
            check(await Until(() => controller.State == LinkState.Connected) && await Until(() => helper.Hotkey == "Ctrl+Alt+Shift+F11"), "Connecting sends the device's hotkey to the main computer");
            check(await Until(() => controller.StatusLine.Contains("在主電腦按 Ctrl+Alt+Shift+F11", StringComparison.Ordinal)), "The card shows whether the hotkey works on the main computer");
            controller.Hotkey = "Ctrl+Alt+H";
            check(await Until(() => helper.Hotkey == "Ctrl+Alt+H"), "Changing the hotkey on the device updates the main computer");
            check(await server.SendToActiveAsync(Message("toggle-game")) && await Until(() => toggles == 1), "The main computer's hotkey asks the device to switch game mode");

            var processes = await RemoteGame.ListProcessesAsync(controller);
            check(processes.Select(p => p.Name).SequenceEqual(["fakegame", "explorer"]) && processes[0] is { Id: 4242, Started: 99 }, "The device lists the main computer's processes");
            var timedOut = false;
            try { await controller.RequestAsync(Message("unanswered"), TimeSpan.FromMilliseconds(300)); }
            catch (TimeoutException) { timedOut = true; }
            check(timedOut, "A request the main computer never answers times out");

            var client = new RemoteGameClient(controller, false, true, processes[0]);
            check(client.Read() is { Snapshot: null, Stale: false } waiting && waiting.Status.Contains("等待", StringComparison.Ordinal), "Before the first snapshot the page waits for the main computer");
            using var stop = new CancellationTokenSource();
            var running = client.RunAsync(stop.Token);
            check(await Until(() => helper.Started is { Sensors: true, High: false, Target.Name: "fakegame" } started && started.Session == client.Session),
                "Showing the game page asks the main computer to sample, with the chosen process and options");
            check(await Until(() => client.Read() is { Stale: false, Snapshot.Cpu: 37 }), "Snapshots from the main computer reach the device");

            helper.Streaming = false;
            await helper.Channel!.SendAsync(RemoteGame.Snapshot(Sample(client.Session + 1000, 99)), CancellationToken.None);
            await Task.Delay(300);
            check(client.Read().Snapshot?.Cpu == 37, "Snapshots for another request are ignored");
            check(await Until(() => client.Read().Stale, 6000) && client.Read().Status.Contains("過期", StringComparison.Ordinal), "Without new snapshots the data turns stale");
            helper.Streaming = true;
            check(await Until(() => !client.Read().Stale), "Fresh snapshots end the stale state");

            var sessionToken = helper.SessionToken;
            var starts = helper.Starts;
            controller.Disconnect();
            check(await Until(() => controller.State == LinkState.Idle) && client.Read() is { Snapshot: null } gone && gone.Status.Contains("中斷", StringComparison.Ordinal), "A lost connection shows no values");
            check(await Until(() => sessionToken.IsCancellationRequested), "The helper's work for that connection is cancelled when it ends");
            check(await Until(() => controller.HotkeyStatus is null), "A disconnected device no longer reports the main computer's hotkey: " + controller.HotkeyStatus);
            controller.Connect(paired.Fingerprint);
            check(await Until(() => helper.Starts == starts + 1) && await Until(() => client.Read() is { Stale: false, Snapshot: not null }), "After reconnecting the device asks to sample again");
            check(await Until(() => helper.Hotkey == "Ctrl+Alt+H"), "After reconnecting the hotkey is sent again");

            stop.Cancel();
            await running;
            check(await Until(() => helper.Stops == 1 && helper.Started is null), "Leaving the game page tells the main computer to stop sampling");
        }
        finally
        {
            await controller.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
