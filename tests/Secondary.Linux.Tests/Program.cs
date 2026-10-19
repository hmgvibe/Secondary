using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Platform;
using Secondary.Shared;

internal static class Program
{
    private static int passed;
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }

    // Arguments: [output directory for rendered frames]
    private static int Main(string[] args)
    {
        // Game-mode checks create the MangoHud folder under XDG_DATA_HOME; keep that out of the real home.
        var dataHome = Path.Combine(Path.GetTempPath(), "secondary-tests-data-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
        try
        {
            TestPaths();
            if (args.Contains("--live-game")) { GameChecks.RunLive(); return 0; }
            if (args.Contains("--live-mpris")) { HomeChecks.RunLiveMpris(); Console.WriteLine($"{passed} live MPRIS checks passed."); return 0; }
            TestLaunch();
            TestDisplays();
            TestLauncher();
            TestSingleInstance();
            AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var output = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            TestShell(output);
            HomeChecks.Run(output);
            ShortcutChecks.Run(output);
            AppChecks.Run(output);
            GameChecks.Run(output);
            ConnectionUiChecks.Run(output);
            Console.WriteLine($"{passed} Linux shell checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(dataHome)) Directory.Delete(dataHome, true); }
    }

    private static void TestPaths()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\home\ray" : "/home/ray";
        var root = OperatingSystem.IsWindows() ? @"C:\xdg" : "/xdg";
        var defaults = AppPaths.ForXdg(_ => null, home);
        Check(defaults.SettingsPath == Path.Combine(home, ".config", "secondary", "settings.json"), "XDG defaults place settings under ~/.config/secondary");
        Check(defaults.LogsDirectory == Path.Combine(home, ".local", "state", "secondary", "logs"), "XDG defaults place logs under ~/.local/state/secondary/logs");
        var custom = AppPaths.ForXdg(v => v == "XDG_CONFIG_HOME" ? Path.Combine(root, "config") : v == "XDG_STATE_HOME" ? Path.Combine(root, "state") : null, home);
        Check(custom.ShortcutsPath == Path.Combine(root, "config", "secondary", "shortcuts.json") && custom.LogsDirectory == Path.Combine(root, "state", "secondary", "logs"), "Absolute XDG variables are honoured");
        var relative = AppPaths.ForXdg(v => v.StartsWith("XDG_", StringComparison.Ordinal) ? "relative/dir" : null, home);
        Check(relative == defaults, "Relative XDG variables are ignored as the specification requires");
        var windows = AppPaths.ForWindows(@"C:\Users\ray\AppData\Local");
        Check(windows.SettingsPath == Path.Combine(@"C:\Users\ray\AppData\Local", "Secondary", "settings.json") && windows.LogsDirectory == Path.Combine(@"C:\Users\ray\AppData\Local", "Secondary", "Logs"), "Windows keeps the existing %LOCALAPPDATA%\\Secondary layout");
    }

    private static void TestLaunch()
    {
        Check(LaunchOptions.Parse([]) == new LaunchOptions(false, null), "No arguments start normally");
        Check(LaunchOptions.Parse(["--toggle-game"]).ToggleGame, "--toggle-game is recognised");
        Check(LaunchOptions.Parse(["--profile=tmp/p"]).Profile == Path.GetFullPath("tmp/p"), "--profile resolves to an absolute directory");
        Check(LaunchOptions.Parse(["--startup-check=out/r.json"]) is { StartupCheck: var receipt, Profile: null } && receipt == Path.GetFullPath("out/r.json"), "--startup-check takes a receipt path and keeps the everyday profile untouched");
        Check(LaunchOptions.Parse(["--startup-check=r.json", "--check-seconds=15"]).CheckSeconds == 15, "--check-seconds sets how long a startup check runs");
        foreach (var bad in new[] { new[] { "--bogus" }, new[] { "--toggle-game", "--toggle-game" }, new[] { "--profile=" }, new[] { "--profile=a", "--profile=b" }, new[] { "--startup-check=" }, new[] { "--check-seconds=5" }, new[] { "--startup-check=r.json", "--check-seconds=0" }, new[] { "--startup-check=r.json", "--check-seconds=121" } })
        {
            var rejected = false; try { LaunchOptions.Parse(bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unknown, empty or duplicate arguments are rejected: " + string.Join(' ', bad));
        }
        Check(LaunchCommand.ToggleGame(v => v == "APPIMAGE" ? "/home/ray/Apps/Secondary.AppImage" : null, "/tmp/.mount_x/usr/bin/secondary") == "/home/ray/Apps/Secondary.AppImage --toggle-game", "AppImage launches use the image file, not the temporary mount");
        Check(LaunchCommand.ToggleGame(_ => null, "/opt/secondary/secondary") == "/opt/secondary/secondary --toggle-game", "Plain installs use the process path");
        Check(LaunchCommand.ToggleGame(v => v == "APPIMAGE" ? "/home/ray/My Apps/it's.AppImage" : null, null) == "'/home/ray/My Apps/it'\\''s.AppImage' --toggle-game", "Paths with spaces and quotes are shell-quoted");
        Check(LaunchCommand.ToggleGame(v => v == "APPIMAGE" ? "relative.AppImage" : null, "/opt/s/secondary") == "/opt/s/secondary --toggle-game", "A relative APPIMAGE value is not trusted");
        HotkeyGesture.TryParse("Ctrl+Alt+G", out var standard);
        HotkeyGesture.TryParse("Ctrl+Shift+F5", out var function);
        Check(ShortcutTrigger.ToXdg(standard!) == "CTRL+ALT+g" && ShortcutTrigger.ToXdg(function!) == "CTRL+SHIFT+F5", "Hotkeys convert to the XDG shortcut trigger format");
    }

    private static void TestDisplays()
    {
        var displays = DisplayCatalog.From([("DP-1", new PixelRect(0, 0, 2560, 1440), true), ("HDMI-1", new PixelRect(2560, 0, 1920, 1080), false)]);
        Check(displays.Select(d => d.Id).SequenceEqual(["name:DP-1", "name:HDMI-1"]) && displays[1].Label.Contains("副螢幕"), "Connector names become stable display identities");
        var unnamed = DisplayCatalog.From([(null, new PixelRect(0, 0, 1920, 1080), true), ("Same", new PixelRect(1920, 0, 1280, 800), false), ("Same", new PixelRect(3200, 0, 1280, 800), false)]);
        Check(unnamed[0].Id == "rect:0,0,1920x1080" && unnamed[1].Id != unnamed[2].Id && unnamed[0].Name == "螢幕 1", "Missing or duplicate names fall back to distinct geometry identities");
    }

    private static void TestLauncher()
    {
        Check(DesktopLauncher.Exec("/opt/secondary/secondary") == "/opt/secondary/secondary", "Plain launcher paths stay unquoted");
        Check(DesktopLauncher.Exec(@"/home/ray/My Apps/a$b""c`d%e\f") == @"""/home/ray/My Apps/a\\$b\\""c\\`d%%e\\\\f""", "Launcher Exec follows the desktop entry quoting and escaping rules");
        var data = Path.Combine(Path.GetTempPath(), "Secondary.Launcher-" + Guid.NewGuid().ToString("N"));
        try
        {
            var home = OperatingSystem.IsWindows() ? @"C:\home\ray" : "/home/ray";
            var fromEnvironment = DesktopLauncher.FromEnvironment(v => v == "APPIMAGE" ? "/apps/Secondary.AppImage" : null, home, "/tmp/.mount/secondary");
            Check(fromEnvironment.DesktopFile == Path.Combine(home, ".local", "share", "applications", "io.github.hmgvibe.Secondary.desktop") && fromEnvironment.Program == "/apps/Secondary.AppImage", "Launcher defaults to ~/.local/share and the AppImage file");
            var launcher = new DesktopLauncher(data, "/apps/Secondary.AppImage");
            Check(launcher.State == LauncherState.Missing, "No launcher is assumed before the user adds one");
            launcher.Install(new MemoryStream([1, 2, 3]));
            Check(launcher.State == LauncherState.Current && File.ReadAllBytes(launcher.IconFile).SequenceEqual(new byte[] { 1, 2, 3 }), "Adding writes the desktop entry and icon");
            var text = File.ReadAllText(launcher.DesktopFile);
            Check(text.Contains("Exec=/apps/Secondary.AppImage\n") && text.Contains("Exec=/apps/Secondary.AppImage --toggle-game") && text.Contains("Icon=io.github.hmgvibe.Secondary"), "The desktop entry launches the app and offers a game-mode action");
            Check(new DesktopLauncher(data, "/elsewhere/secondary").State == LauncherState.Elsewhere, "A launcher for another location is reported for update");
            Check(!Directory.EnumerateFiles(data, "*.tmp-*", SearchOption.AllDirectories).Any(), "Atomic writes leave no temporary files");
            launcher.Remove();
            Check(launcher.State == LauncherState.Missing && !File.Exists(launcher.IconFile), "Removing deletes only the launcher and icon");
            var system = Path.Combine(data, "system");
            var packaged = new DesktopLauncher(Path.Combine(data, "user"), "/opt/secondary/secondary", [Path.Combine(data, "empty"), system]);
            Check(packaged.State == LauncherState.Missing, "Without a system entry the launcher is still missing");
            Directory.CreateDirectory(Path.Combine(system, "applications"));
            File.WriteAllText(Path.Combine(system, "applications", "io.github.hmgvibe.Secondary.desktop"), DesktopLauncher.Content("secondary"));
            Check(packaged.State == LauncherState.Packaged, "A system entry from the .deb counts as already added");
            Directory.CreateDirectory(Path.Combine(data, "user", "applications"));
            File.WriteAllText(packaged.DesktopFile, DesktopLauncher.Content("/old/Secondary-v1.0.2.AppImage"));
            Check(packaged.State == LauncherState.Shadowed, "An old AppImage entry hiding the .deb's entry is reported for removal");
            packaged.Remove();
            Check(packaged.State == LauncherState.Packaged, "Removing the old entry brings back the .deb's entry");
            var dirs = DesktopLauncher.FromEnvironment(v => v == "XDG_DATA_DIRS" ? system + ":relative:" : null, home, "/opt/secondary/secondary");
            Check(dirs.State == (OperatingSystem.IsWindows() ? LauncherState.Missing : LauncherState.Packaged), "XDG_DATA_DIRS is searched, ignoring relative entries");
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
    }

    private static void TestSingleInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LinuxChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = Path.Combine(directory, "settings.json");
            var received = new List<InstanceCommand>();
            using (var owner = SingleInstanceCoordinator.Acquire(profile))
            {
                Check(owner is not null, "First instance owns the settings profile");
                owner!.Listen(c => { lock (received) received.Add(c); return true; });
                Check(Task.Run(() => { using var second = SingleInstanceCoordinator.Acquire(profile); return second is null; }).GetAwaiter().GetResult(), "A second instance cannot become another writer");
                Check(SingleInstanceCoordinator.SendAsync(profile, InstanceCommand.ToggleGame).GetAwaiter().GetResult(), "Toggle-game request reaches the running instance");
                Check(SingleInstanceCoordinator.ShowExistingAsync(profile).GetAwaiter().GetResult(), "Show request reaches the running instance");
                lock (received) Check(received.SequenceEqual([InstanceCommand.ToggleGame, InstanceCommand.Show]), "Commands arrive in order and unchanged");
            }
            using (var showOnly = SingleInstanceCoordinator.Acquire(profile))
            {
                var shows = 0;
                showOnly!.Listen(() => Interlocked.Increment(ref shows));
                Check(!SingleInstanceCoordinator.SendAsync(profile, InstanceCommand.ToggleGame).GetAwaiter().GetResult() && shows == 0, "A show-only listener (Windows) declines toggle-game without acting");
                Check(SingleInstanceCoordinator.ShowExistingAsync(profile).GetAwaiter().GetResult() && shows == 1, "A show-only listener still answers show after declining");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestShell(string? output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LinuxShell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (output is not null) Directory.CreateDirectory(output);
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var model = new ShellModel(new SettingsStore(settingsPath));
            var launcherData = Path.Combine(directory, "data");
            var window = new MainWindow(model, new DesktopLauncher(launcherData, "/apps/Secondary.AppImage")) { Width = 1200, Height = 820 };
            window.Show();
            Pump();
            RadioButton[] Items() => window.FindControl<StackPanel>("NavigationItems")!.Children.OfType<RadioButton>().ToArray();
            string[] Keys() => Items().Select(i => (string)i.Tag!).ToArray();
            Check(Keys().SequenceEqual(["home", "shortcuts", "game", "settings"]), "Daily navigation is 首頁｜快捷導航｜遊戲模式｜設定");
            Check(window.CurrentPage == "home" && window.FindControl<Grid>("HomeLayer")!.IsVisible && !window.FindControl<ScrollViewer>("Frame")!.IsVisible, "Home is the first page and does not scroll");
            Check(window.ClockRunning, "Home clock ticks while home is visible");
            Check(Items().All(i => Automation(i).Length > 0 && ToolTip.GetTip(i) is string), "Every navigation item has an accessible name and tooltip");

            window.Navigate("shortcuts");
            Check(window.CurrentPage == "shortcuts" && !window.ClockRunning, "Leaving home stops the clock");
            window.Navigate("game");
            Pump();
            Check(model.Modes.Mode == AppMode.Game && Keys().SequenceEqual(["game", "exit", "settings"]), "Game mode shows 監測｜退出｜設定");
            Check(Brush(window, "AccentBrush") == Color.Parse("#65DCEC") && window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark, "Game mode uses the dark game palette");
            Check(new SettingsStore(settingsPath).Load().Mode == AppMode.Game, "Entering game mode is saved");
            window.Navigate("settings");
            Check(window.CurrentPage == "settings" && model.Modes.Mode == AppMode.Game, "Settings opens inside game mode without leaving it");
            window.Navigate("exit");
            Check(model.Modes.Mode == AppMode.Normal && window.CurrentPage == "shortcuts", "Exiting game mode returns to the previous daily page");
            window.ToggleGame(); window.ToggleGame();
            Check(model.Modes.Mode == AppMode.Normal && window.CurrentPage == "shortcuts", "Toggle-game enters and leaves symmetrically");

            window.Navigate("settings");
            Check(window.SettingsOpen && window.FindControl<StackPanel>("SettingsPage")!.IsVisible, "Settings is an independent page");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Check(!window.SettingsOpen && window.CurrentPage == "shortcuts", "Esc closes settings back to the previous page");

            window.Navigate("settings");
            window.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = 2;
            Pump();
            Check(window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark && Brush(window, "CanvasBrush") == Color.Parse("#111B29"), "Dark choice applies the daily dark palette");
            window.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = 1;
            Pump();
            Check(Brush(window, "CanvasBrush") == Color.Parse("#E7EDF5") && new SettingsStore(settingsPath).Load().Theme == "Light", "Light choice applies and is saved");
            window.SetAccessibility(true, true);
            Check(Brush(window, "TextBrush") == Colors.Black && !window.AnimationsEnabled, "High contrast forces maximum contrast and disables effects");
            window.SetAccessibility(false, false);
            Check(Brush(window, "TextBrush") == Color.Parse("#162A37") && !window.AnimationsEnabled, "Reduced motion is honoured without high contrast");
            window.SetAccessibility(false, true);

            var hotkey = window.FindControl<TextBox>("HotkeyInput")!;
            var hotkeyStatus = window.FindControl<TextBlock>("HotkeyStatus")!;
            hotkey.Text = "G";
            Click(window, "套用快捷鍵");
            Check(hotkeyStatus.Text!.StartsWith("格式例如", StringComparison.Ordinal) && model.Settings.Hotkey == "Ctrl+Alt+G", "Invalid hotkeys are rejected without saving");
            hotkey.Text = "ctrl+shift+f5";
            Click(window, "套用快捷鍵");
            Pump();
            Check(model.Settings.Hotkey == "Ctrl+Shift+F5" && hotkeyStatus.Text!.Contains("請使用下方指令", StringComparison.Ordinal), "Valid hotkeys are saved; without a portal the command fallback is offered");
            Check(window.FindControl<TextBox>("ToggleCommand")!.Text!.EndsWith(" --toggle-game", StringComparison.Ordinal), "The toggle command is shown for desktop shortcut binding");

            var reconnects = 0;
            var answer = new ShortcutResult(ShortcutState.NotAllowed, "桌面不允許這個程式綁定全域快捷鍵。");
            window.SetDesktopServices(true, _ => Task.FromResult(answer), () => { reconnects++; return Task.CompletedTask; });
            Pump();
            Check(window.ShortcutOutcome == ShortcutState.NotAllowed && hotkeyStatus.Text!.Contains("桌面整合", StringComparison.Ordinal), "A refused shortcut without a launcher points to desktop integration");
            Click(window, "加入應用程式選單");
            Check(File.Exists(Path.Combine(launcherData, "applications", "io.github.hmgvibe.Secondary.desktop")) && reconnects == 1, "Adding the launcher writes the entry and reconnects to the portal");
            Check(window.LauncherText.StartsWith("已加入應用程式選單", StringComparison.Ordinal) && !window.FindControl<Button>("InstallLauncherButton")!.IsEnabled, "Launcher status reports the current program");
            answer = new ShortcutResult(ShortcutState.Bound, "已由桌面綁定：Ctrl+Alt+G");
            window.SetDesktopServices(true, _ => Task.FromResult(answer), null);
            Pump();
            Check(window.ShortcutOutcome == ShortcutState.Bound && hotkeyStatus.Text == "已由桌面綁定：Ctrl+Alt+G", "A bound shortcut shows the desktop's own description");
            Click(window, "從選單移除");
            Check(!File.Exists(Path.Combine(launcherData, "applications", "io.github.hmgvibe.Secondary.desktop")) && window.FindControl<Button>("InstallLauncherButton")!.IsEnabled, "The launcher can be removed again");
            window.SetDesktopServices(false, null);
            Pump();

            var closing = window.IsVisible;
            window.Close();
            Pump();
            Check(closing && window.IsVisible && window.WindowState == WindowState.Minimized, "Without a tray, closing minimizes instead of hiding the only way back");
            window.ShowInterface();
            Pump();
            Check(window.WindowState == WindowState.Normal && window.IsVisible, "The window can be shown again");

            window.Navigate("home");
            window.WindowState = WindowState.Minimized;
            Pump();
            Check(!window.ClockRunning, "Minimizing stops the home clock");
            window.WindowState = WindowState.Normal;
            Pump();
            Check(window.ClockRunning, "Restoring home resumes the clock");

            foreach (var position in new[] { "Bottom", "Top", "Left", "Right" })
            {
                model.SaveRuntime(navigationSize: "Small", navigationPosition: position);
                window.Width = 560; window.Height = 480;
                window.ApplyResponsiveLayout(560, 480);
                Pump();
                var item = Items()[0];
                var root = window.FindControl<Grid>("ShellRoot")!;
                var topLeft = item.TranslatePoint(new Point(0, 0), root)!.Value;
                var bottomRight = item.TranslatePoint(new Point(item.Bounds.Width, item.Bounds.Height), root)!.Value;
                var size = bottomRight - topLeft;
                // Leave room for layout rounding at 125%, 150% and 175% scaling.
                Check(size.X >= 44.5 && size.Y >= 44.5, $"Small navigation keeps more than a 44 DIP target at {position} ({size.X:0.##}×{size.Y:0.##})");
                var bar = window.FindControl<LayoutTransformControl>("NavigationScale")!;
                var barTopLeft = bar.TranslatePoint(new Point(0, 0), root)!.Value;
                Check(barTopLeft.X >= 0 && barTopLeft.Y >= 0 && barTopLeft.X + bar.Bounds.Width <= 560.5 && barTopLeft.Y + bar.Bounds.Height <= 480.5, $"Navigation stays inside a 560×480 window at {position}");
                Control frame = window.FindControl<ScrollViewer>("Frame")!.IsVisible ? window.FindControl<ScrollViewer>("Frame")! : window.FindControl<Grid>("HomeLayer")!;
                var frameRect = new Rect(frame.TranslatePoint(new Point(0, 0), root)!.Value, frame.Bounds.Size);
                Check(!frameRect.Intersects(new Rect(barTopLeft, bar.Bounds.Size)), $"Navigation never covers the visible page content at {position}");
            }
            model.SaveRuntime(navigationSize: "Standard", navigationPosition: "Bottom");

            var combinations = 0;
            foreach (var (width, height) in new[] { (480.0, 420.0), (560.0, 480.0), (800.0, 600.0), (1200.0, 820.0), (1920.0, 1080.0), (2560.0, 1440.0) })
            foreach (var theme in new[] { 1, 2 })
            foreach (var page in new[] { "home", "shortcuts", "game", "settings" })
            {
                window.FindControl<ComboBox>("ThemePicker")!.SelectedIndex = theme;
                if (page == "game") { if (model.Modes.Mode != AppMode.Game) window.Navigate("game"); }
                else { if (model.Modes.Mode == AppMode.Game) window.Navigate("exit"); window.Navigate(page); }
                window.Width = width; window.Height = height;
                window.ApplyResponsiveLayout(width, height);
                Pump();
                var content = window.FindControl<Panel>("ContentArea")!;
                var frame = window.FindControl<ScrollViewer>("Frame")!;
                if (page == "home")
                {
                    // Home is a fixed canvas: every widget must stay inside it.
                    var canvas = window.FindControl<Canvas>("WidgetCanvas")!;
                    var outside = canvas.Children.OfType<Control>().Any(c => Canvas.GetLeft(c) < -0.5 || Canvas.GetLeft(c) + c.Bounds.Width > canvas.Bounds.Width + 0.5);
                    if (outside || window.FindControl<Grid>("HomeLayer")!.Bounds.Width > width)
                        throw new Exception($"FAIL: home widgets overflow at {width}×{height} theme {theme}");
                }
                else if (content.Bounds.Width > width || frame.Extent.Width > frame.Viewport.Width + 0.5)
                    throw new Exception($"FAIL: horizontal overflow at {width}×{height} {page} theme {theme}");
                if (width <= 1600 && content.Bounds.Width > 1600) throw new Exception("FAIL: content exceeds the 1600 DIP limit");
                combinations++;
                if (output is not null && (width, height) is (1200, 820) or (560, 480))
                    window.CaptureRenderedFrame()?.Save(Path.Combine(output, $"linux-{page}-{(theme == 1 ? "light" : "dark")}-{width:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            Check(combinations == 48, "48 viewport × theme × page combinations lay out without horizontal overflow");
            if (model.Modes.Mode == AppMode.Game) window.Navigate("exit");

            window.AllowClose = true;
            window.Close();
            model.Dispose();
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static string Automation(Control c) => Avalonia.Automation.AutomationProperties.GetName(c) ?? "";
    private static Color Brush(Window window, string key) => window.Resources[key] is ImmutableSolidColorBrush b ? b.Color : default;
    internal static void Click(Window window, string content)
    {
        var button = window.GetVisualDescendantsOfType<Button>().First(b => b.Content as string == content);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : Avalonia.Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>();
}
