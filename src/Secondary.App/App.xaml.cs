using System;
using System.IO;
using System.Windows;
using Secondary.Core;
using Secondary.Shared;
using Secondary.Shared.Connection;

namespace Secondary.App;

public partial class App : Application
{
    private TrayService? tray;
    private DisplayService? displays;
    private SingleInstanceCoordinator? instance;
    private ConnectionIdentity? connectionIdentity;
    private ConnectionController? connection;
    private string? benchmarkDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root: dependencies are supplied explicitly, without a background host.
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "settings.json");
        var benchmark = e.Args.Length > 0 && e.Args[0] == "--measure";
        // Sign-in launch: start silently into the tray without showing the dashboard.
        var background = Array.IndexOf(e.Args, "--background") >= 0;
        Monitoring.PerformanceRequest? measurement = null;
        if (benchmark)
        {
            try
            {
                measurement = Monitoring.PerformanceRun.Parse(e.Args);
                benchmarkDirectory = Path.Combine(Path.GetDirectoryName(measurement.Report)!, ".secondary-measure-profile-" + Guid.NewGuid().ToString("N"));
                settingsPath = Path.Combine(benchmarkDirectory, "settings.json");
            }
            catch (Exception error) when (error is ArgumentException or IOException)
            { MessageBox.Show("性能量测参数无效：" + error.Message, "Secondary"); Shutdown(2); return; }
        }
        else
        {
            instance = SingleInstanceCoordinator.Acquire(settingsPath);
            if (instance is null)
            {
                if (!SingleInstanceCoordinator.ShowExistingAsync(settingsPath).GetAwaiter().GetResult())
                    MessageBox.Show("Secondary 已在执行，但现有窗口暂时无法响应。请从系统托盘显示界面。", "Secondary");
                Shutdown(); return;
            }
        }
        var store = new SettingsStore(settingsPath);
        displays = new DisplayService();
        var model = new ShellViewModel(displays, store);
        var window = new MainWindow(model, registerHotkey: !benchmark,
            shortcutsPath: benchmarkDirectory is null ? null : Path.Combine(benchmarkDirectory, "shortcuts.json"));
        MainWindow = window;
        tray = new TrayService(window, () => Shutdown(), new Secondary.Windows.AppStartup());
        if (!benchmark) StartConnection(window, Path.GetDirectoryName(settingsPath)!);
        if (!background) window.Show();
        instance?.Listen(() => Dispatcher.BeginInvoke(() => window.ShowInterface()));
        if (!benchmark) _ = Secondary.Monitoring.PresentMonStream.RecoverAbandonedSessionsAsync(message =>
            Dispatcher.BeginInvoke(() => model.Status = message));
        if (measurement is not null) _ = Monitoring.PerformanceRun.Run(window, model, measurement);
    }
    // Connection mode on this device; a failure here leaves the card explaining why and nothing else affected.
    private void StartConnection(MainWindow window, string dataDirectory)
    {
        string? problem = null;
        try
        {
            var folder = Path.Combine(dataDirectory, "Connection");
            connectionIdentity = ConnectionIdentity.LoadOrCreate(Path.Combine(folder, "identity.pfx"), Environment.MachineName);
            connection = new ConnectionController(connectionIdentity, new PairedStore(Path.Combine(folder, "helpers.json")), Environment.MachineName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AppDiagnostics.Write("connection-identity", error);
            problem = "连接模式无法启用：无法建立本机连接身分（" + error.Message + "）。";
        }
        window.AttachConnection(connection, problem);
        connection?.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        try { connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        connectionIdentity?.Dispose();
        if (MainWindow is MainWindow window)
        {
            window.DisposeGameTelemetry();
            // Worker cleanup never waits on the dispatcher. Allow our ETW session to close before process exit.
            try { window.GameMonitoringCompletion.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
        }
        tray?.Dispose();
        displays?.Dispose();
        instance?.Dispose();
        if (benchmarkDirectory is not null)
        {
            // Only files in this uniquely owned benchmark directory are removed.
            try { if (Directory.Exists(benchmarkDirectory)) Directory.Delete(benchmarkDirectory, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        base.OnExit(e);
    }
}
