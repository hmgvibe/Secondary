using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Monitoring;
using Secondary.Shared.Connection;
using static Program;
using static Secondary.Shared.Connection.ConnectionProtocol;

// The Linux connection card against a loopback helper: search, pair, connect, disconnect and forget.
internal static class ConnectionUiChecks
{
    private static bool PumpUntil(Func<bool> done, int milliseconds = 8000)
    {
        for (var waited = 0; waited < milliseconds && !done(); waited += 20) { Pump(); Thread.Sleep(20); }
        Pump();
        return done();
    }
    private static Button RowButton(StackPanel rows, string content) =>
        rows.Children.OfType<DockPanel>().SelectMany(r => ((StackPanel)r.Children[0]).Children.OfType<Button>()).First(b => (string?)b.Content == content);
    private static string RowText(StackPanel rows) => string.Join("\n", rows.Children.OfType<DockPanel>().Select(r => ((TextBlock)r.Children[1]).Text));
    private static void Press(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // A snapshot as the Windows helper sends it while a game runs on an NVIDIA GPU.
    private static RemoteGameSnapshot Sample(long session, bool sensors = true) => new(session, DateTimeOffset.UtcNow, false,
        37, 16UL << 30, 8UL << 30, 2048, 1024,
        TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available, "整機採樣 / 持續更新",
        55, TelemetrySourceState.Available, "GPU 引擎 / 持續更新",
        sensors ? [
            Reading("NVIDIA GeForce RTX 4060 / GPU Core", "/gpu-nvidia/0/load/0", 64, "%"), Reading("NVIDIA GeForce RTX 4060 / GPU Core", "/gpu-nvidia/0/temperature/0", 61, "°C"),
            Reading("NVIDIA GeForce RTX 4060 / GPU Core", "/gpu-nvidia/0/clock/0", 2505, "MHz"), Reading("NVIDIA GeForce RTX 4060 / GPU Package", "/gpu-nvidia/0/power/0", 95, "W"),
            Reading("NVIDIA GeForce RTX 4060 / GPU Memory Used", "/gpu-nvidia/0/smalldata/1", 4096, "MB"), Reading("NVIDIA GeForce RTX 4060 / GPU Memory Total", "/gpu-nvidia/0/smalldata/2", 8192, "MB"),
            Reading("Intel Core i7 / CPU Package", "/intelcpu/0/temperature/0", 72, "°C"), Reading("Intel Core i7 / Core #1", "/intelcpu/0/clock/1", 4800, "MHz")
        ] : [], sensors ? TelemetrySourceState.Available : TelemetrySourceState.Disabled, sensors ? "硬體助手 / 持續採樣" : "硬體助手未啟用",
        new FrameStatistics("0x1A2B", 600, 120, 90, 8.33, false), TelemetrySourceState.Available, "PresentMon / 目前活動交換鏈", false);
    private static MonitoringReading Reading(string metric, string identity, double value, string unit) =>
        new(metric, "LibreHardwareMonitor 0.9.6", identity, DateTimeOffset.UtcNow, value, unit, ReadingState.Available);

    // Stands in for the Windows connection helper: streams snapshots for the newest game request.
    private sealed class FakeHelper
    {
        public volatile RemoteGameRequest? Started;
        public int Stops;
        public volatile string? Hotkey;
        public volatile string? MediaCommand;
        public volatile bool Playing = true;
        public int Watching;
        public readonly List<ShortcutEntry> Shortcuts = [];
        public int Runs;
        public async Task Handle(string device, JsonObject message, FrameChannel channel, CancellationToken token)
        {
            switch (TypeOf(message))
            {
                case "game-start":
                    var request = RemoteGame.ReadStart(message)!;
                    Started = request;
                    _ = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested && ReferenceEquals(Started, request))
                        {
                            await channel.SendAsync(RemoteGame.Snapshot(Sample(request.Session, request.Sensors)), token);
                            await Task.Delay(150, token);
                        }
                    }, token).ContinueWith(_ => { }, TaskScheduler.Default);
                    break;
                case "game-stop": Interlocked.Increment(ref Stops); Started = null; break;
                case "processes": await channel.SendAsync(Reply(message, RemoteGame.ProcessList([new(4242, 99, "fakegame"), new(7, 1, "explorer")])), token); break;
                case "hotkey":
                    Hotkey = Text(message, "gesture");
                    await channel.SendAsync(RemoteGame.HotkeyStatus(true, $"在主電腦按 {Hotkey} 也能進出遊戲模式。"), token);
                    break;
                case "media-watch":
                    Watching += message["on"]?.GetValue<bool>() == true ? 1 : -1;
                    if (Watching > 0) await channel.SendAsync(Song(), token);
                    break;
                case "media-control":
                    MediaCommand = Text(message, "command"); Playing = !Playing;
                    await channel.SendAsync(Reply(message, Message("media-reply", ("ok", true))), token);
                    await channel.SendAsync(Song(), token);
                    break;
                case "shortcuts":
                    await channel.SendAsync(Reply(message, RemoteShortcuts.List(Shortcuts, [], null)), token);
                    break;
                case "shortcut-save":
                    var entry = RemoteShortcuts.ReadEntry(message["entry"]);
                    if (entry is not null) { Shortcuts.RemoveAll(e => e.Id == entry.Id); Shortcuts.Add(entry); }
                    await channel.SendAsync(Reply(message, Message("shortcut-reply", ("ok", entry is not null), ("error", entry is null ? "內容不符合 Windows 捷徑規則。" : null))), token);
                    break;
                case "shortcut-delete":
                    Shortcuts.RemoveAll(e => e.Id == Text(message, "shortcut"));
                    await channel.SendAsync(Reply(message, Message("shortcut-reply", ("ok", true))), token);
                    break;
                case "shortcut-run":
                    Runs++;
                    await channel.SendAsync(Message("shortcut-status", ("run", Text(message, "run")), ("text", "等待主電腦確認：請到主電腦上確認或取消。")), token);
                    await Task.Delay(300, token);
                    await channel.SendAsync(Reply(message, Message("shortcut-result", ("ok", true), ("text", "已在主電腦完成：" + Shortcuts.First(e => e.Id == Text(message, "shortcut")).Name))), token);
                    break;
            }
        }
    }

    private static JsonObject Song() => RemoteMediaMessages.State(new RemoteMedia("Spotify.exe", "Clair de Lune", "Debussy", true, true, true, true, 42, 300, ""), "播放中");

    // Windows sensor names become the identities the Linux game page reads.
    private static void CheckTranslate()
    {
        var readings = RemoteGameSource.Translate(Sample(1));
        double? Value(string identity) => readings.FirstOrDefault(r => r.Identity == identity)?.Value;
        Check(Value("/gpu-nvidia/0/load") == 64 && Value("/gpu-nvidia/0/temperature") == 61 && Value("/gpu-nvidia/0/clock") == 2505 && Value("/gpu-nvidia/0/power") == 95 &&
            Value("/gpu-nvidia/0/memory-used") == 4096 && Value("/gpu-nvidia/0/memory-total") == 8192 && readings.First().Metric == "NVIDIA GeForce RTX 4060 / load",
            "Main computer GPU sensors are renamed for the Linux game page");
        Check(Value("/cpu/0/temperature") == 72 && Value("/cpu/0/clock") == 4800, "Main computer CPU sensors are renamed for the Linux game page");
        var bare = RemoteGameSource.Translate(Sample(1, sensors: false));
        Check(bare.Single(r => r.Identity == "/gpu-main/0/load").Value == 55 && bare.Single(r => r.Identity == "/gpu-main/0/memory-used").Reason!.Contains("主電腦硬體感測", StringComparison.Ordinal),
            "Without sensors the busiest GPU engine is shown and the page explains how to get more");
    }

    private static void ExerciseGame(MainWindow window, ShellModel model, HelperServer server, FakeHelper helper, ConnectionController controller, ConnectionPanel card, string? output)
    {
        string Text(string name) => window.FindControl<TextBlock>(name)!.Text ?? "";
        bool Visible(string name) => window.FindControl<Control>(name)!.IsVisible;
        bool Values() { window.TickGame(); return Text("FpsValue") == "120" && Text("CpuValue") == "37%" && Text("GpuValue") == "64%" && Text("VramValue") == "50%"; }
        window.SetAccessibility(false, false);
        Check(PumpUntil(() => helper.Hotkey == model.Settings.Hotkey && card.StatusText.Contains("在主電腦按 " + model.Settings.Hotkey, StringComparison.Ordinal)),
            "The device's hotkey is sent to the main computer and the card says it works there");
        server.SendToActiveAsync(Message("toggle-game")).Wait();
        Check(PumpUntil(() => model.Modes.Mode == Secondary.Core.AppMode.Game), "Pressing the hotkey on the main computer puts the device in game mode");
        Check(PumpUntil(() => window.GameRemote && helper.Started is { Sensors: false, Target: null }), "While connected, the game page asks the main computer to sample instead of this device");
        Check(PumpUntil(() => { window.TickGame(); return Text("FpsValue") == "120" && Text("GpuValue") == "55%"; }), "Without sensors the busiest GPU engine is shown: " + Text("GpuValue"));
        Check(!Visible("MangoHudCard") && Visible("RemoteProcessPicker") && Visible("RemoteSensorsToggle") && Text("GameSourceCaption").Contains("Main PC", StringComparison.Ordinal),
            "In connection mode the MangoHud card gives way to the main computer's process picker and sensors");
        window.FindControl<CheckBox>("RemoteSensorsToggle")!.IsChecked = true;
        Check(PumpUntil(() => helper.Started is { Sensors: true }) && PumpUntil(Values),
            $"Turning on the main computer's sensors shows its GPU readings: FPS {Text("FpsValue")} CPU {Text("CpuValue")} GPU {Text("GpuValue")} VRAM {Text("VramValue")}");
        Check(Text("GameSourceState").StartsWith("REMOTE · LIVE", StringComparison.Ordinal) && Text("GameNotice").Contains("主電腦「Main PC」", StringComparison.Ordinal) && Text("GpuDetails").Contains("RTX 4060", StringComparison.Ordinal),
            "The page says the data comes from the main computer: " + Text("GameSourceState"));
        Press(window.FindControl<Button>("RemoteProcessPicker")!);
        Check(PumpUntil(() => window.RemoteProcessChoices.Length == 2) && window.RemoteProcessChoices[0].Name == "fakegame", "The process picker lists the main computer's processes");
        window.ChooseRemoteProcess(window.RemoteProcessChoices[0]);
        Check(PumpUntil(() => helper.Started is { Target: { Name: "fakegame", Id: 4242 } }) && (string?)window.FindControl<Button>("RemoteProcessPicker")!.Content == "fakegame · 4242",
            "Choosing a process samples it on the main computer");
        Check(PumpUntil(Values), "Readings continue after choosing a process");
        if (output is not null)
        {
            for (var i = 0; i < 10; i++) Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-connection-game.png"), PngBitmapEncoderOptions.Default);
        }
        server.SendToActiveAsync(Message("toggle-game")).Wait();
        Check(PumpUntil(() => model.Modes.Mode == Secondary.Core.AppMode.Normal && helper.Started is null), "Pressing the hotkey again leaves game mode and the main computer stops sampling");

        window.ToggleGame();
        Check(PumpUntil(() => window.GameRemote && helper.Started is not null), "Entering game mode on the device also uses the main computer");
        controller.Disconnect();
        Check(PumpUntil(() => !window.GameRemote && window.GameMonitoringActive) && PumpUntil(() => { window.TickGame(); return Text("GameNotice").Contains("已與主電腦中斷連線", StringComparison.Ordinal); }) &&
            Visible("MangoHudCard") && !Visible("RemoteProcessPicker"), "Losing the connection falls back to this device's data and says why");
        window.ToggleGame(); Pump();
        Press(RowButton(card.Rows, "連線"));
        Check(PumpUntil(() => server.ActiveDevice == "Surface"), "Reconnecting after the game checks");
    }

    // Stage 3: a media widget switched to the main computer shows and controls its player.
    private static void ExerciseMedia(FakeHelper helper, string? output)
    {
        var saved = new List<HomeWidgetSettings>();
        var widget = new Secondary.Linux.Home.UtilityWidget(new HomeWidgetSettings("media", "Media")) { Width = 340, Height = 320 };
        widget.SaveRequested += next => { saved.Add(next); return true; };
        var host = new Window { Content = widget, Width = 380, Height = 380 };
        host.Show(); Pump();
        try
        {
            widget.SetActive(true);
            Check((string?)widget.MediaSourceButton!.Content == "來源：本機", "A Linux media widget starts with this computer's player");
            Press(widget.MediaSourceButton);
            Check(saved.LastOrDefault()?.MediaSource == "Main" && (string?)widget.MediaSourceButton.Content == "來源：主電腦", "The source switch is saved with the widget");
            Check(PumpUntil(() => widget.MediaTitleText == "Clair de Lune" && widget.MediaStateText == "主電腦 · 播放中") && helper.Watching == 1,
                "The widget shows what the main computer is playing: " + widget.MediaTitleText + " / " + widget.MediaStateText);
            var control = widget.ControlMediaAsync(Secondary.Linux.Home.MediaCommand.Toggle);
            Check(PumpUntil(() => control.IsCompleted) && helper.MediaCommand == "toggle", "Play/pause on the widget controls the main computer's player");
            if (output is not null) { for (var i = 0; i < 5; i++) Pump(); host.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-connection-media.png"), PngBitmapEncoderOptions.Default); }
            Press(widget.MediaSourceButton);
            Check(saved.Last().MediaSource == "Local" && PumpUntil(() => helper.Watching == 0), "Switching back to this computer stops the main computer's reports");
        }
        finally { widget.SetActive(false); widget.Dispose(); host.Close(); Pump(); }
    }

    // Stage 3: main-computer shortcuts in the Linux launchpad; the main computer validates and runs them.
    private static void ExerciseShortcuts(MainWindow window, FakeHelper helper, string? output)
    {
        var launchpad = window.Launchpad;
        window.Navigate("shortcuts");
        Check(PumpUntil(() => launchpad.RemoteShortcutList is { Entries.Length: 0 }) && !launchpad.RemoteTiles.Any(), "The main computer starts with no shortcuts");
        // "＋ 新增" asks where the shortcut lives; choosing the main computer switches to its rules.
        var add = launchpad.Add();
        Check(PumpUntil(() => window.ShortcutEditorOpen) && window.OpenShortcutEditor is { Main: false, LocalChoice.IsChecked: true, MainChoice: not null },
            "While connected, the editor offers this device or the main computer, this device by default");
        var editor = window.OpenShortcutEditor!;
        editor.MainChoice!.IsChecked = true; Pump();
        editor.NameInput.Text = "Game"; editor.GroupInput.Text = "遊戲";
        editor.Steps[0].Target!.Text = @"C:\Games\game.exe";
        Check(editor.Main && window.SaveShortcutEditor(), "A Windows path is accepted for the main computer");
        Check(PumpUntil(() => add.IsCompleted) && helper.Shortcuts.SingleOrDefault()?.Name == "Game" && PumpUntil(() => launchpad.RemoteTiles.Count() == 1),
            "Choosing the main computer stores the shortcut there and shows its tile");
        var tile = launchpad.RemoteTiles.Single();
        var section = (StackPanel)((WrapPanel)tile.Parent!).Parent!;
        Check(((DockPanel)((Border)section.Children[0]).Child!).Children.OfType<TextBlock>().Any(t => t.Text == "遊戲") &&
            ((TextBlock)((StackPanel)tile.Content!).Children[2]).Text!.Contains("主電腦", StringComparison.Ordinal),
            "It sits in its own group, not a separate section, and its tile says 主電腦");
        var before = launchpad.EntryCount;
        var local = launchpad.Add();
        PumpUntil(() => window.ShortcutEditorOpen);
        window.OpenShortcutEditor!.NameInput.Text = "Docs"; window.OpenShortcutEditor.Steps[0].Target!.Text = "https://example.com/";
        Check(window.SaveShortcutEditor() && PumpUntil(() => local.IsCompleted) && launchpad.EntryCount == before + 1 && helper.Shortcuts.Count == 1, "Keeping 本機 stores the shortcut on this device");
        var bad = launchpad.SaveRemoteAsync(new ShortcutEntry("bad", "Bad", Steps: [new("Open", "game.exe")]));
        Check(PumpUntil(() => bad.IsCompleted) && !bad.Result && launchpad.ExecutionStatus.StartsWith("主電腦拒絕", StringComparison.Ordinal), "The main computer's refusal is shown");
        var keys = launchpad.EditRemote(new ShortcutEntry("keys", "Keys", Steps: [new("Keys", "Ctrl+S", ProcessName: "notepad", WindowTitle: "a.txt - Notepad")]));
        Check(PumpUntil(() => keys.IsCompleted) && launchpad.ExecutionStatus.Contains("Windows 設備編輯", StringComparison.Ordinal) && !window.ShortcutEditorOpen,
            "Shortcuts with window-key steps are not edited on Linux");
        var edit = launchpad.EditRemote(helper.Shortcuts[0]);
        Check(PumpUntil(() => window.ShortcutEditorOpen) && window.OpenShortcutEditor is { Main: true, MainChoice: not null }, "An existing main-computer shortcut opens in the editor with 主電腦 chosen and 本機 offered");
        window.CloseShortcutEditor(null); PumpUntil(() => edit.IsCompleted);
        Press(launchpad.RemoteTiles.Single());
        Check(PumpUntil(() => launchpad.ExecutionStatus.StartsWith("等待主電腦確認", StringComparison.Ordinal)), "The Linux device shows that the main computer is asking its user");
        Check(PumpUntil(() => launchpad.ExecutionStatus == "已在主電腦完成：Game" && !launchpad.IsExecuting) && helper.Runs == 1, "The main computer's result is shown: " + launchpad.ExecutionStatus);
        if (output is not null)
        {
            for (var i = 0; i < 10; i++) Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-connection-shortcuts.png"), PngBitmapEncoderOptions.Default);
        }
        var delete = launchpad.DeleteRemoteAsync(helper.Shortcuts[0].Id);
        Check(PumpUntil(() => delete.IsCompleted) && delete.Result && PumpUntil(() => !launchpad.RemoteTiles.Any()) && helper.Shortcuts.Count == 0, "A Linux device can delete a main-computer shortcut");

        // Editing moves a shortcut between this device and the main computer.
        var docs = launchpad.Entries.Single(e => e.Name == "Docs");
        var move = launchpad.Edit(docs);
        Check(PumpUntil(() => window.ShortcutEditorOpen) && window.OpenShortcutEditor is { Main: false, MainChoice: not null }, "Editing a local shortcut while connected offers the main computer");
        window.OpenShortcutEditor!.MainChoice!.IsChecked = true; Pump();
        Check(window.SaveShortcutEditor() && PumpUntil(() => move.IsCompleted) && helper.Shortcuts.SingleOrDefault()?.Name == "Docs" && !launchpad.Entries.Any(e => e.Name == "Docs"),
            "Choosing 主電腦 moves it there and removes it here");
        var back = launchpad.EditRemote(helper.Shortcuts.Single());
        PumpUntil(() => window.ShortcutEditorOpen);
        window.OpenShortcutEditor!.LocalChoice!.IsChecked = true; Pump();
        Check(window.SaveShortcutEditor() && PumpUntil(() => back.IsCompleted) && PumpUntil(() => helper.Shortcuts.Count == 0) && launchpad.Entries.Any(e => e.Name == "Docs"),
            "Choosing 本機 moves a main-computer shortcut back here");

        // A pending shortcut from Windows goes to the main computer when its tile is pressed, after asking.
        var file = Path.Combine(Path.GetTempPath(), "Secondary.Pending-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(file, ShortcutTransfer.Export([], "Vertical", [new ShortcutEntry("save", "Save", "工作", Steps: [new("Keys", "Ctrl+S", ProcessName: "notepad", WindowTitle: "a.txt - Notepad")])]));
            var import = launchpad.ImportFrom(file);
            PumpUntil(() => window.ConfirmOpen); window.AnswerConfirm(true);
            Check(PumpUntil(() => import.IsCompleted) && import.Result && launchpad.PendingTiles.Count() == 1, "An imported Windows shortcut shows as pending");
            Press(launchpad.PendingTiles.Single());
            Check(PumpUntil(() => window.ConfirmOpen), "Pressing it while connected asks before moving it to the main computer");
            window.AnswerConfirm(true);
            Check(PumpUntil(() => helper.Shortcuts.Any(s => s.Name == "Save")) && PumpUntil(() => launchpad.PendingEntries.Length == 0) && PumpUntil(() => launchpad.RemoteTiles.Count() == 1),
                "Accepting moves it to the main computer, where it can run");
        }
        finally { File.Delete(file); }
        window.Navigate("home"); Pump();
    }

    public static void Run(string? output)
    {
        CheckTranslate();
        var root = Path.Combine(Path.GetTempPath(), "Secondary.LinuxConnection-" + Guid.NewGuid().ToString("N"));
        var model = new ShellModel(new SettingsStore(Path.Combine(root, "settings.json")));
        // Local fallback reads an empty fixture tree, never this machine's folders.
        var window = new MainWindow(model) { Width = 1200, Height = 1000, TelemetryRoot = Path.Combine(root, "machine"), MangoHudFolder = Path.Combine(root, "mangohud") };
        window.Show(); Pump();
        int discoveryPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var helperIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "helper", "identity.pfx"), "Main PC");
        var devices = new PairedStore(Path.Combine(root, "helper", "devices.json"));
        var helper = new FakeHelper();
        var server = new HelperServer(helperIdentity, devices, "Main PC", IPAddress.Loopback, 0, discoveryPort) { MessageHandler = (d, m, c, t) => helper.Handle(d, m, c, t) };
        server.Start();
        using var deviceIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "device", "identity.pfx"), "Surface");
        var helpers = new PairedStore(Path.Combine(root, "device", "helpers.json"));
        var controller = new ConnectionController(deviceIdentity, helpers, "Surface", () => [new IPEndPoint(IPAddress.Loopback, discoveryPort)], TimeSpan.FromMilliseconds(500));
        try
        {
            window.AttachConnection(null, "連線模式無法啟用：測試用原因。");
            Check(window.ConnectionCard!.StatusText.Contains("測試用原因", StringComparison.Ordinal), "Without an identity the card explains why connection mode is unavailable");
            window.AttachConnection(controller);
            var card = window.ConnectionCard!;
            var panels = ((StackPanel)card.Parent!).Children;
            Check(panels.IndexOf(card) > 0 && panels[panels.IndexOf(card) - 1].GetVisualDescendantsOfType<TextBox>().Any(t => t.Name == "HotkeyInput"), "The connection card follows the hotkey card in settings");
            Check(card.StatusText == "未連線" && !card.DisconnectButton.IsEnabled, "A fresh device is not connected");
            // Opening settings searches once without a click.
            window.Navigate("settings");
            Check(PumpUntil(() => RowText(card.Rows).Contains("Main PC · 127.0.0.1 · 尚未配對", StringComparison.Ordinal)), "Opening settings searches and lists the helper: " + RowText(card.Rows));
            Press(RowButton(card.Rows, "配對…")); Pump();
            Check(card.HostInput.Text == "127.0.0.1:" + server.Port && card.PairText.Contains("Main PC", StringComparison.Ordinal), "Choosing a found helper fills in its address");
            card.CodeInput.Text = "1234";
            var pair = card.PairAsync(); PumpUntil(() => pair.IsCompleted);
            Check(card.PairText.Contains("6 位數", StringComparison.Ordinal), "A short code is rejected before connecting");
            var code = server.StartPairing();
            card.CodeInput.Text = code;
            pair = card.PairAsync();
            Check(PumpUntil(() => pair.IsCompleted && controller.State == LinkState.Connected) && card.PairText.StartsWith("配對成功", StringComparison.Ordinal) && devices.Find(deviceIdentity.Fingerprint)?.Name == "Surface",
                "The code pairs and connects the device: " + card.PairText);
            Check(PumpUntil(() => card.StatusText.StartsWith("已連線到「Main PC」", StringComparison.Ordinal) && RowText(card.Rows).Contains("已連線", StringComparison.Ordinal)) && card.DisconnectButton.IsEnabled,
                "The card shows the connection: " + card.StatusText.Replace('\n', ' '));
            if (output is not null)
            {
                Frame().Offset = new Avalonia.Vector(0, Math.Max(0, card.Bounds.Top - 40));
                for (var i = 0; i < 10; i++) Pump();
                window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-connection-card.png"), PngBitmapEncoderOptions.Default);
            }
            Press(card.DisconnectButton);
            Check(PumpUntil(() => controller.State == LinkState.Idle && server.ActiveDevice is null) && card.StatusText == "未連線", "Disconnecting ends the session on both sides");
            Press(RowButton(card.Rows, "連線"));
            Check(PumpUntil(() => server.ActiveDevice == "Surface"), "Connecting again uses the stored pairing");
            ExerciseGame(window, model, server, helper, controller, card, output);
            ExerciseMedia(helper, output);
            ExerciseShortcuts(window, helper, output);
            // 移除配對 asks in the window's own dialog first.
            Press(RowButton(card.Rows, "移除配對")); Pump();
            Check(window.ConfirmOpen, "Removing a pairing asks first");
            window.AnswerConfirm(false); Pump();
            Check(helpers.Peers.Count == 1, "Declining keeps the pairing");
            Press(RowButton(card.Rows, "移除配對")); Pump();
            window.AnswerConfirm(true);
            Check(PumpUntil(() => helpers.Peers.Count == 0 && devices.Peers.Count == 0 && controller.State == LinkState.Idle), "Removing the pairing on the device removes it on both sides");
            ScrollViewer Frame() => window.FindControl<ScrollViewer>("Frame")!;
        }
        finally
        {
            controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            window.AllowClose = true; window.Close(); Pump();
            model.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
