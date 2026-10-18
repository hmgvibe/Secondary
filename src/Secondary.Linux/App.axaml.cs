using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Linux.Platform;
using Secondary.Shared;

namespace Secondary.Linux;

public partial class App : Application
{
    public static StartupContext? Startup { get; set; }
    private readonly CancellationTokenSource stop = new();
    private DesktopPortal? portal;
    private TrayIcon? tray;
    private MainWindow? window;
    private ShellModel? model;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless tests start without a launch context and create their own windows.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Startup is { } startup)
        {
            model = new ShellModel(new SettingsStore(startup.Paths.SettingsPath));
            window = new MainWindow(model, shortcutsPath: startup.Paths.ShortcutsPath) { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://secondary/Assets/secondary.png"))) };
            desktop.MainWindow = window;
            CreateTray(desktop, window);
            // Opened fires inside Show, so subscribe first.
            if (startup.Options.StartupCheck is { } receipt) window.Opened += async (_, _) => await WriteStartupReceiptAsync(desktop, window, model, receipt, startup.Options.CheckSeconds);
            window.Show();
            startup.Instance?.Listen(command =>
            {
                Dispatcher.UIThread.Post(() => { if (command == InstanceCommand.ToggleGame) window.ToggleGame(); else window.ShowInterface(); });
                return true;
            });
            StartConnection(window, startup.Paths.DataDirectory);
            if (startup.Options.ToggleGame) window.ToggleGame();
            desktop.Exit += (_, _) => Cleanup();
            _ = ConnectDesktopAsync(window);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Packaging and acceptance check: wait for the desktop services to settle (without waiting on a
    // user's answer to a shortcut dialog), record what was detected, render the window and exit.
    private async Task WriteStartupReceiptAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, ShellModel model, string receipt, int seconds)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime; var started = DateTime.UtcNow;
        await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromMilliseconds(1500));
        for (var i = 0; i < 40 && !(window.DesktopServicesKnown && window.ShortcutOutcome is not null); i++) await Task.Delay(100);
        var frame = System.IO.Path.ChangeExtension(receipt, ".png");
        var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
        using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size))
        {
            bitmap.Render(window);
            bitmap.Save(frame, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        var screen = window.Screens.ScreenFromWindow(window);
        System.IO.File.WriteAllText(receipt, System.Text.Json.JsonSerializer.Serialize(new
        {
            Status = "Started", Page = window.CurrentPage, window.Bounds.Width, window.Bounds.Height,
            Displays = model.Displays.Select(d => d.Label).ToArray(), Selected = model.Selected?.Label,
            WindowScreen = screen?.DisplayName, Scaling = screen?.Scaling,
            window.DesktopServicesKnown, window.TrayAvailable, Shortcut = window.ShortcutOutcome?.ToString(), Hotkey = window.HotkeyStatusText,
            Accessibility = window.AccessibilityText, Launcher = window.LauncherText, PortalRegistration = portal is null ? "NoPortal" : portal.RegistrationError ?? "Registered", Platform = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Theme = window.ActualThemeVariant.ToString(), Frame = System.IO.Path.GetFileName(frame),
            Background = model.Settings.Home?.Background ?? "Static", BackgroundStatus = window.HomeBackground.Status, window.HomeBackground.HasVideo, VideoFrames = window.HomeBackground.VideoFramesShown,
            CpuPercentOfOneCore = Math.Round((process.TotalProcessorTime - cpuStart).TotalMilliseconds / Math.Max(1, (DateTime.UtcNow - started).TotalMilliseconds) * 100, 1),
            WorkingSetMiB = Math.Round(Environment.WorkingSet / 1048576d, 1),
            Game = window.LastGameSnapshot is { } game ? new
            {
                Updates = window.GameMonitoringUpdateCount, Cpu = game.System?.Cpu, CpuState = game.CpuState.ToString(), Hardware = game.HardwareStatus,
                Gpu = game.Hardware.Where(r => r.Identity.EndsWith("/load", StringComparison.Ordinal)).Select(r => $"{r.Metric}: {r.Value?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"} ({r.Reason})").ToArray(),
                FrameState = game.FrameState.ToString(), game.FrameStatus, game.Frames.Fps, game.Frames.LowOnePercent, game.Frames.Count, game.PerFrame
            } : null
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        window.AllowClose = true;
        desktop.Shutdown();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var show = new NativeMenuItem("顯示 Secondary");
        show.Click += (_, _) => window.ShowInterface();
        var game = new NativeMenuItem("進入／退出遊戲模式");
        game.Click += (_, _) => { window.ShowInterface(); window.ToggleGame(); };
        var quit = new NativeMenuItem("退出 Secondary");
        quit.Click += (_, _) => { window.AllowClose = true; desktop.Shutdown(); };
        var menu = new NativeMenu();
        menu.Items.Add(show); menu.Items.Add(game); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
        tray = new TrayIcon { Icon = window.Icon, ToolTipText = "Secondary", Menu = menu };
        tray.Clicked += (_, _) => window.ShowInterface();
        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    // Also used to reconnect after the launcher is installed: the portal reads the app ID per connection.
    private async Task ConnectDesktopAsync(MainWindow window)
    {
        portal?.Dispose();
        portal = null;
        var current = await DesktopPortal.ConnectAsync();
        portal = current;
        if (current is null) { window.SetDesktopServices(false, null); return; }
        var trayAvailable = await current.HasTrayWatcherAsync();
        AppCatalog.Current.Theme = await current.ReadStringAsync("org.gnome.desktop.interface", "icon-theme");
        await RefreshAccessibilityAsync(current, window);
        await current.WatchSettingsAsync((section, key) => { if (key is "high-contrast" or "enable-animations") _ = RefreshAccessibilityAsync(current, window); });
        Dispatcher.UIThread.Post(() => window.SetDesktopServices(trayAvailable,
            trigger => current.BindToggleGameAsync(trigger, () => Dispatcher.UIThread.Post(() => window.ToggleGame()), stop.Token),
            () => ConnectDesktopAsync(window)));
    }

    private static async Task RefreshAccessibilityAsync(DesktopPortal portal, MainWindow window)
    {
        var highContrast = await portal.ReadBoolAsync("org.gnome.desktop.a11y.interface", "high-contrast");
        var animations = await portal.ReadBoolAsync("org.gnome.desktop.interface", "enable-animations");
        Dispatcher.UIThread.Post(() => window.SetAccessibility(highContrast ?? false, animations ?? true));
    }

    // Connection mode on this device; a failure leaves the card explaining why and nothing else affected.
    private void StartConnection(MainWindow window, string dataDirectory)
    {
        string? problem = null;
        try
        {
            var folder = System.IO.Path.Combine(dataDirectory, "connection");
            connectionIdentity = Secondary.Shared.Connection.ConnectionIdentity.LoadOrCreate(System.IO.Path.Combine(folder, "identity.pfx"), Environment.MachineName);
            connection = new Secondary.Shared.Connection.ConnectionController(connectionIdentity, new Secondary.Shared.Connection.PairedStore(System.IO.Path.Combine(folder, "helpers.json")), Environment.MachineName);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Secondary.Shared.AppDiagnostics.Write("connection-identity", error);
            problem = "連線模式無法啟用：無法建立本機連線身分（" + error.Message + "）。";
        }
        window.AttachConnection(connection, problem);
        connection?.Start();
    }
    private Secondary.Shared.Connection.ConnectionIdentity? connectionIdentity;
    private Secondary.Shared.Connection.ConnectionController? connection;
    private void Cleanup()
    {
        try { connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        connectionIdentity?.Dispose();
        stop.Cancel();
        tray?.Dispose();
        portal?.Dispose();
        model?.Dispose();
    }
}
