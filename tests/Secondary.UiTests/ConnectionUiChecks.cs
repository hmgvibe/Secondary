using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Connect;
using Secondary.Core;
using Secondary.Monitoring;
using Secondary.Shared.Connection;

// Connection mode on Windows: the device card in settings and the helper's tray and pairing window, over loopback.
internal static class ConnectionUiChecks
{
    private sealed class FakeStartup : IStartupSetting
    {
        public bool Enabled { get; private set; }
        public void Set(bool enabled) => Enabled = enabled;
    }

    // Stands in for the main computer's samplers: a game at 120 FPS on an NVIDIA GPU.
    private sealed class FakeTelemetry : IGameTelemetry
    {
        public GameTelemetrySnapshot Read()
        {
            var now = DateTimeOffset.UtcNow;
            MonitoringReading Gpu(string metric, string identity, double value, string unit) =>
                new("NVIDIA GeForce RTX 4060 / " + metric, "LibreHardwareMonitor 0.9.6", identity, now, value, unit, ReadingState.Available);
            return new(new SystemSample(37, 16UL << 30, 8UL << 30, 2048, 1024, null, "", "", "", now,
                    CpuState: TelemetrySourceState.Available, MemoryState: TelemetrySourceState.Available, NetworkState: TelemetrySourceState.Available),
                55, "GPU 引擎 / 持續更新",
                new SensorSnapshot(1, [Gpu("GPU Core", "/gpu-nvidia/0/load/0", 64, "%"), Gpu("GPU Core", "/gpu-nvidia/0/temperature/0", 61, "°C"),
                    Gpu("GPU Memory Used", "/gpu-nvidia/0/smalldata/1", 4096, "MB"), Gpu("GPU Memory Total", "/gpu-nvidia/0/smalldata/2", 8192, "MB")]),
                "硬體助手 / 持續採樣", new FrameStatistics("0x1A2B", 600, 120, 90, 8.33, false), "PresentMon / 目前活動交換鏈", false,
                "整機採樣 / 持續更新", TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available, TelemetrySourceState.Available,
                CpuState: TelemetrySourceState.Available, MemoryState: TelemetrySourceState.Available, NetworkState: TelemetrySourceState.Available);
        }
        public async Task RunAsync(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        }
    }
    // Stands in for the main computer's player: one song with a small cover.
    private sealed class FakeMedia : IMediaProvider
    {
        public string? LastCommand;
        public volatile bool Playing = true;
        public IDisposable Subscribe(Action changed) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
        public Task<(RemoteMedia? Media, string Status)> ReadAsync(CancellationToken token) =>
            Task.FromResult<(RemoteMedia?, string)>((new RemoteMedia("Spotify.exe", "Clair de Lune", "Debussy", Playing, true, true, true, 42, 300, "cover-1"), Playing ? "播放中" : "已暫停"));
        public Task<bool> ControlAsync(string command, CancellationToken token) { LastCommand = command; Playing = !Playing; return Task.FromResult(true); }
        public Task<byte[]?> ArtworkAsync(string key, CancellationToken token)
        {
            var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            return Task.FromResult<byte[]?>(stream.ToArray());
        }
    }
    // Records what the main computer would run instead of starting programs.
    private sealed class FakeExecutor : IReviewingShortcutExecutor
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<ShortcutStep> Ran = new();
        public string Review(ShortcutEntry entry) => string.Join("\n", entry.Steps!.Select((s, i) => $"{i + 1}. {s.Kind}\n{s.Target}"));
        public Task Execute(ShortcutStep step, CancellationToken cancellation) { Ran.Enqueue(step); return Task.CompletedTask; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    // Async work in the card resumes on this dispatcher; pumping runs those continuations.
    private static bool Pump(Func<bool> done, int milliseconds = 8000)
    {
        for (var waited = 0; waited < milliseconds && !done(); waited += 20)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(20);
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        return done();
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static Button RowButton(StackPanel rows, string content) =>
        rows.Children.OfType<DockPanel>().SelectMany(r => ((StackPanel)r.Children[0]).Children.OfType<Button>()).First(b => (string)b.Content == content);
    private static string RowText(StackPanel rows) => string.Join("\n", rows.Children.OfType<DockPanel>().Select(r => ((TextBlock)r.Children[1]).Text));

    internal static void Run(MainWindow window, Action<bool, string> check, string? output)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = Path.Combine(Path.GetTempPath(), "Secondary.ConnectionUi-" + Guid.NewGuid().ToString("N"));
        try { Exercise(window, check, output, root); }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void Exercise(MainWindow window, Action<bool, string> check, string? output, string root)
    {
        window.AttachConnection(null, "連線模式無法啟用：測試用原因。");
        check(window.ConnectionCard!.StatusText.Contains("測試用原因", StringComparison.Ordinal), "Without an identity the card explains why connection mode is unavailable");

        int discoveryPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) discoveryPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var helperIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "helper", "identity.pfx"), "Main PC");
        var devices = new PairedStore(Path.Combine(root, "helper", "devices.json"));
        var server = new HelperServer(helperIdentity, devices, "Main PC", IPAddress.Loopback, 0, discoveryPort);
        server.Start();
        using var deviceIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(root, "device", "identity.pfx"), "Desk");
        var helpers = new PairedStore(Path.Combine(root, "device", "helpers.json"));
        var controller = new ConnectionController(deviceIdentity, helpers, "Desk", () => [new IPEndPoint(IPAddress.Loopback, discoveryPort)], TimeSpan.FromMilliseconds(500));
        var startup = new FakeStartup();
        var executor = new FakeExecutor();
        var media = new FakeMedia();
        using var tray = new ConnectTray(server, devices, startup, showIcon: false,
            games: new GameService(_ => new FakeTelemetry(), () => [new RemoteProcess(4242, 99, "fakegame"), new RemoteProcess(7, 1, "explorer")]),
            media: new MediaService(media), shortcuts: new ShortcutService(Path.Combine(root, "helper", "shortcuts.json"), executor));
        var confirmations = 0;
        tray.Confirm = _ => ++confirmations > 1;
        try
        {
            // The tray menu before any device.
            var items = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(i => i.Text ?? "").ToArray();
            check(items[0] == "等待設備連線" && items[1].StartsWith("主電腦：Main PC · ", StringComparison.Ordinal) && items.Contains("開始配對…") && items.Contains("已配對設備") && items.Contains("登入時啟動") && items.Contains("結束連接助手"),
                "The helper's tray menu shows status, identity, pairing, devices, sign-in start and exit: " + string.Join(" | ", items));
            var signIn = tray.Menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == "登入時啟動");
            signIn.PerformClick();
            check(startup.Enabled && signIn.Checked, "Sign-in start can be turned on from the tray");

            window.AttachConnection(controller, confirm: (_, _, _) => true);
            var card = window.ConnectionCard!;
            var panels = ((StackPanel)card.Parent).Children;
            check(panels.IndexOf(card) > 0 && ((FrameworkElement)panels[panels.IndexOf(card) - 1]).IsAncestorOf((DependencyObject)window.FindName("HotkeyInput")), "The connection card follows the hotkey card in settings");
            check(card.StatusText == "未連線" && !card.DisconnectButton.IsEnabled, "A fresh device is not connected");
            var search = card.SearchAsync();
            check(Pump(() => search.IsCompleted) && RowText(card.Rows).Contains("Main PC · 127.0.0.1 · 尚未配對", StringComparison.Ordinal), "Searching lists the helper as not yet paired: " + RowText(card.Rows));
            Click(RowButton(card.Rows, "配對…"));
            check(card.HostInput.Text == "127.0.0.1:" + server.Port && card.PairText.Contains("Main PC", StringComparison.Ordinal), "Choosing a found helper fills in its address");

            card.CodeInput.Text = "12345";
            var pair = card.PairAsync(); Pump(() => pair.IsCompleted);
            check(card.PairText.Contains("6 位數", StringComparison.Ordinal), "A short code is rejected before connecting");
            card.CodeInput.Text = "000000";
            pair = card.PairAsync(); Pump(() => pair.IsCompleted);
            check(card.PairText.Contains("尚未開始配對", StringComparison.Ordinal), "Pairing before the main computer opens it explains what to do: " + card.PairText);

            tray.OpenPairing();
            var form = tray.PairingWindow!;
            check(form.Visible && server.PairingCode is { } code && form.CodeText == code[..3] + " " + code[3..], "開始配對 opens a window with the code");
            if (output is not null) Capture(form, Path.Combine(output, "connect-pairing.png"));
            card.CodeInput.Text = server.PairingCode![..3] + " " + server.PairingCode[3..];
            pair = card.PairAsync();
            check(Pump(() => pair.IsCompleted && controller.State == LinkState.Connected) && card.PairText.StartsWith("配對成功", StringComparison.Ordinal) && card.CodeInput.Text == "",
                "The code from the window pairs and connects the device");
            check(Pump(() => form.StatusText.Contains("已配對「Desk」", StringComparison.Ordinal)) && form.CodeText == "— — —", "The pairing window reports the paired device and closes the code");
            form.Close();
            check(Pump(() => card.StatusText.StartsWith("已連線到「Main PC」", StringComparison.Ordinal)) && card.DisconnectButton.IsEnabled && RowText(card.Rows).Contains("已連線", StringComparison.Ordinal),
                "The card shows the connection: " + card.StatusText.Replace('\n', ' '));
            check(Pump(() => tray.StatusText == "已連線：Desk（127.0.0.1）") && tray.TrayText.Contains("已連線：Desk", StringComparison.Ordinal), "The tray shows the connected device");
            if (output is not null)
            {
                window.OpenSettings(); Pump(() => true, 200);
                Capture(card, Path.Combine(output, "connection-card.png"));
                window.CloseSettings();
            }

            Click(card.DisconnectButton);
            check(Pump(() => controller.State == LinkState.Idle && server.ActiveDevice is null) && card.StatusText == "未連線", "Disconnecting ends the session on both sides");
            Click(RowButton(card.Rows, "連線"));
            check(Pump(() => server.ActiveDevice == "Desk"), "Connecting again uses the stored pairing");
            ExerciseGame(window, check, output, tray, controller, card);
            ExerciseMedia(check, output, media);
            ExerciseShortcuts(window, check, output, tray, executor, Path.Combine(root, "helper", "shortcuts.json"));

            // Removal from the tray asks first; declining keeps the device.
            check(!tray.Remove(deviceIdentity.Fingerprint) && devices.Find(deviceIdentity.Fingerprint) is not null, "Declining the removal keeps the device");
            check(tray.Remove(deviceIdentity.Fingerprint) && Pump(() => controller.State == LinkState.Failed) && card.StatusText.Contains("移除", StringComparison.Ordinal) && card.Rows.Children.Count == 1,
                "Removing the device on the main computer disconnects it and the card explains why");

            server.StartPairing();
            card.HostInput.Text = "127.0.0.1:" + server.Port; card.CodeInput.Text = server.PairingCode!;
            pair = card.PairAsync();
            check(Pump(() => pair.IsCompleted && controller.State == LinkState.Connected), "The device can pair again");
            Click(RowButton(card.Rows, "移除配對"));
            check(Pump(() => devices.Peers.Count == 0 && helpers.Peers.Count == 0 && controller.State == LinkState.Idle), "移除配對 on the device removes the pairing on both sides");
        }
        finally
        {
            controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }

    // Stage 2: the main computer's hotkey, game telemetry and process list on the device's game page.
    private static void ExerciseGame(MainWindow window, Action<bool, string> check, string? output, ConnectTray tray, ConnectionController controller, Secondary.App.Presentation.ConnectionPanel card)
    {
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        void Press() => PostMessage(tray.Hotkey.Handle, 0x0312, tray.Hotkey.ActiveId, IntPtr.Zero);
        var visible = window.IsVisible;
        window.Show();
        window.ExitGame();
        try
        {
            // An unusual gesture, so the check does not collide with a hotkey already in use on this computer.
            controller.Hotkey = "Ctrl+Alt+Shift+F11";
            check(Pump(() => tray.Hotkey.Gesture == "Ctrl+Alt+Shift+F11" && card.StatusText.Contains("在主電腦按 Ctrl+Alt+Shift+F11", StringComparison.Ordinal)),
                "The device's hotkey is registered on the main computer and the card says so: " + card.StatusText.Replace('\n', ' '));
            tray.Build();
            check(tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Any(i => i.Text == tray.Hotkey.Status) && tray.Hotkey.Status.Contains("Ctrl+Alt+Shift+F11", StringComparison.Ordinal),
                "The tray shows the active hotkey");
            Press();
            check(Pump(() => window.IsGame), "Pressing the hotkey on the main computer puts the device in game mode");
            var high = ((ComboBox)window.FindName("SamplingPicker")).SelectedIndex == 1;
            check(Pump(() => window.GameRemote && tray.Games.Current is { } request && request.High == high), "While connected, the game page asks the main computer to sample at the page's rate instead of this device");
            check(Pump(() => Text("FpsValue") == "120" && Text("CpuValue") == "37%" && Text("GpuValue") == "64%" && Text("VramValue") == "50%"),
                $"The main computer's readings appear: FPS {Text("FpsValue")} CPU {Text("CpuValue")} GPU {Text("GpuValue")} VRAM {Text("VramValue")}");
            check(Text("GameSourceState").StartsWith("REMOTE · LIVE", StringComparison.Ordinal) && Text("GameNotice").Contains("主電腦「Main PC」", StringComparison.Ordinal),
                "The page says the data comes from the main computer: " + Text("GameSourceState"));
            check(tray.Games.Status.Contains("傳送中", StringComparison.Ordinal) && tray.Games.SnapshotsSent > 0, "The tray shows that the helper is streaming");

            var picker = (Button)window.FindName("GameProcessPicker");
            Click(picker);
            // The popup closes at once when the test window is not in the foreground, so the menu's contents are checked instead.
            check(Pump(() => picker.ContextMenu is { Items.Count: > 2 }), "The process picker builds its menu");
            var menu = picker.ContextMenu!;
            var items = menu.Items.OfType<MenuItem>().Select(i => i.Header as string).Where(h => h is not null).ToArray();
            check(items.Contains("fakegame · PID 4242") && items.Contains("explorer · PID 7"), "The process picker lists the main computer's processes: " + string.Join(", ", items));
            menu.Items.OfType<MenuItem>().First(i => i.Header as string == "fakegame · PID 4242").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            check(Pump(() => tray.Games.Current?.Target is { Name: "fakegame", Id: 4242 }) && (string)picker.Content == "fakegame · 4242", "Choosing a process samples it on the main computer");
            if (output is not null) { Pump(() => false, 1500); Capture((FrameworkElement)window.Content, Path.Combine(output, "connection-game.png")); }

            Press();
            check(Pump(() => !window.IsGame) && Pump(() => tray.Games.Current is null), "Pressing the hotkey again leaves game mode and the main computer stops sampling");

            window.Navigate("enter-game");
            check(Pump(() => window.GameRemote && tray.Games.Current is not null), "Entering game mode on the device also uses the main computer");
            controller.Disconnect();
            check(Pump(() => !window.GameRemote && window.GameMonitoringActive && Text("GameNotice").Contains("已與主電腦中斷連線", StringComparison.Ordinal)),
                "Losing the connection falls back to this device's data and says why");
            check(Pump(() => tray.Hotkey.Gesture is null && tray.Games.Current is null), "The main computer releases the hotkey and stops sampling when the device leaves");
        }
        finally
        {
            window.ExitGame();
            if (!visible) window.Hide();
        }
        Click(RowButton(card.Rows, "連線"));
        check(Pump(() => controller.State == LinkState.Connected && tray.Hotkey.Gesture is not null), "Reconnecting registers the hotkey again");
    }

    // Stage 3: a media widget switched to the main computer shows and controls its player.
    private static void ExerciseMedia(Action<bool, string> check, string? output, FakeMedia media)
    {
        var saved = new List<HomeWidgetSettings>();
        using var widget = new HomeUtilityWidget(new HomeWidgetSettings("media", "Media")) { Width = 340, Height = 320 };
        widget.SaveRequested += next => { saved.Add(next); return true; };
        var host = new Window { Content = widget, Width = 380, Height = 380, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -2000, Top = -2000 };
        host.Show();
        try
        {
            widget.SetActive(true);
            check((string)widget.MediaSourceButton!.Content == "來源：本機" && widget.Settings.MediaSource == "Local", "A media widget starts with this computer's player");
            Click(widget.MediaSourceButton);
            check(saved.LastOrDefault()?.MediaSource == "Main" && (string)widget.MediaSourceButton.Content == "來源：主電腦", "The source switch is saved with the widget");
            check(Pump(() => widget.MediaTitleText == "Clair de Lune" && widget.MediaStateText == "主電腦 · 播放中"), "The widget shows what the main computer is playing: " + widget.MediaTitleText);
            var control = widget.ControlMediaAsync("toggle");
            check(Pump(() => control.IsCompleted) && media.LastCommand == "toggle", "Play/pause on the widget controls the main computer's player");
            check(Pump(() => widget.MediaStateText == "主電腦 · 已暫停", 8000), "The widget follows the main computer's new state: " + widget.MediaStateText);
            if (output is not null) Capture(widget, Path.Combine(output, "connection-media.png"));
            Click(widget.MediaSourceButton);
            check(saved.Last().MediaSource == "Local" && (string)widget.MediaSourceButton.Content == "來源：本機", "The widget can switch back to this computer");
        }
        finally { widget.SetActive(false); host.Close(); }
    }

    // Stage 3: main-computer shortcuts are listed, edited and run from the device; the first run waits for the main computer.
    private static void ExerciseShortcuts(MainWindow window, Action<bool, string> check, string? output, ConnectTray tray, FakeExecutor executor, string store)
    {
        var launchpad = window.Launchpad;
        var visible = window.IsVisible;
        window.Show();
        try
        {
            window.Navigate("shortcuts");
            var refresh = launchpad.RefreshRemoteAsync();
            check(Pump(() => refresh.IsCompleted && launchpad.RemoteShortcutList is { Entries.Length: 0 }) && !launchpad.RemoteTiles.Any(), "The main computer starts with no shortcuts");
            // "＋ 新增" offers the main computer as a location; the stand-in editor picks it.
            var game = new ShortcutEntry("game", "Game", "遊戲", "🎮", [new("Open", @"C:\Games\game.exe"), new("Wait", Seconds: 1)]);
            (string? Name, bool Main, bool CanChoose)? offered = null;
            var editor = launchpad.EditDialog;
            launchpad.EditDialog = (entry, mainName, main, canChoose) => { offered = (mainName, main, canChoose); return (game with { Id = entry.Id }, true); };
            try { launchpad.Add(); } finally { launchpad.EditDialog = editor; }
            check(offered is ("Main PC", false, true), "While connected, a new shortcut can be stored here or on the main computer, here by default");
            check(Pump(() => tray.Shortcuts.Entries.SingleOrDefault()?.Name == "Game") && File.Exists(store), "Choosing the main computer stores the shortcut there");
            game = tray.Shortcuts.Entries.Single();
            check(Pump(() => launchpad.RemoteTiles.Count() == 1), "The main computer's shortcut appears among the launchpad tiles");
            var tile = launchpad.RemoteTiles.Single();
            var section = (StackPanel)((WrapPanel)tile.Parent).Parent;
            check(((DockPanel)((Border)section.Children[0]).Child).Children.OfType<TextBlock>().Any(t => t.Text == "遊戲") &&
                ((TextBlock)((StackPanel)tile.Content).Children[2]).Text.Contains("主電腦", StringComparison.Ordinal),
                "It sits in its own group, not a separate section, and its tile says 主電腦");
            var bad = launchpad.SaveRemoteAsync(game with { Id = "bad", Steps = [new("Open", "game.exe")] });
            check(Pump(() => bad.IsCompleted) && !bad.Result && launchpad.ExecutionStatus.StartsWith("主電腦拒絕", StringComparison.Ordinal), "The main computer refuses a shortcut that breaks its rules: " + launchpad.ExecutionStatus);
            tile = launchpad.RemoteTiles.Single();

            Click(tile);
            check(Pump(() => tray.ConfirmWindow is not null) && launchpad.ExecutionStatus.StartsWith("等待主電腦確認", StringComparison.Ordinal),
                $"The first run waits for confirmation on the main computer: {launchpad.ExecutionStatus} (window {tray.ConfirmWindow is not null}, helper {tray.Shortcuts.Status}, executing {launchpad.IsExecuting})");
            var form = tray.ConfirmWindow!;
            check(form.Heading.Contains("「Desk」", StringComparison.Ordinal) && form.Heading.Contains("「Game」", StringComparison.Ordinal) && form.DetailsText.Contains(@"C:\Games\game.exe", StringComparison.Ordinal),
                "The main computer's dialog names the device, the shortcut and its targets");
            check(executor.Ran.IsEmpty, "Nothing runs before the main computer confirms");
            if (output is not null) Capture(form, Path.Combine(output, "connect-confirm.png"));
            form.Decide(true);
            check(Pump(() => launchpad.ExecutionStatus == "已在主電腦完成：Game" && !launchpad.IsExecuting, 8000) && executor.Ran.Count == 1, "Confirming on the main computer runs the steps: " + launchpad.ExecutionStatus);
            check(new ShortcutLibrary(store).Load().Single().ApprovedFingerprint is not null && Pump(() => launchpad.RemoteShortcutList?.Approved.Contains(game.Id) == true), "The main computer remembers the confirmation");

            tile = launchpad.RemoteTiles.Single();
            Click(tile);
            check(Pump(() => launchpad.ExecutionStatus == "已在主電腦完成：Game" && !launchpad.IsExecuting, 8000) && tray.ConfirmWindow is null && executor.Ran.Count == 2, "A confirmed shortcut runs without asking again");

            var changed = launchpad.SaveRemoteAsync(game with { Steps = [new("Open", @"C:\Games\other.exe")] });
            check(Pump(() => changed.IsCompleted) && changed.Result && Pump(() => launchpad.RemoteShortcutList?.Approved.Contains(game.Id) == true), "Editing keeps the approval record on the main computer");
            tile = launchpad.RemoteTiles.Single();
            Click(tile);
            check(Pump(() => tray.ConfirmWindow is not null), "Changed steps need confirmation again");
            tray.ConfirmWindow!.Decide(false);
            check(Pump(() => launchpad.ExecutionStatus.StartsWith("主電腦上已取消", StringComparison.Ordinal) && !launchpad.IsExecuting) && executor.Ran.Count == 2, "Declining on the main computer runs nothing");

            window.Navigate("enter-game");
            var refused = launchpad.LaunchRemote(game);
            check(Pump(() => refused.IsCompleted) && launchpad.ExecutionStatus.StartsWith("目前不能執行", StringComparison.Ordinal), "Main-computer shortcuts do not run in game mode");
            window.ExitGame();
            window.Navigate("shortcuts");

            var delete = launchpad.DeleteRemoteAsync(game.Id);
            check(Pump(() => delete.IsCompleted) && delete.Result && tray.Shortcuts.Entries.Length == 0, "A device can delete a main-computer shortcut");
        }
        finally
        {
            window.ExitGame();
            if (!visible) window.Hide();
        }
    }

    private static void Capture(System.Windows.Forms.Control control, string path)
    {
        using var bitmap = new System.Drawing.Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, control.Width, control.Height));
        bitmap.Save(path);
    }
    private static void Capture(FrameworkElement element, string path)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
