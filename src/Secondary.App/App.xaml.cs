using System;
using System.IO;
using System.Windows;
using Secondary.Core;

namespace Secondary.App;

public partial class App : Application
{
    private TrayService? tray;
    private DisplayService? displays;
    private SingleInstanceCoordinator? instance;
    private string? benchmarkDirectory;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root: dependencies are supplied explicitly, without a background host.
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "settings.json");
        var benchmark = e.Args.Length > 0 && e.Args[0] == "--measure";
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
            { MessageBox.Show("效能量測參數無效：" + error.Message, "Secondary"); Shutdown(2); return; }
        }
        else
        {
            instance = SingleInstanceCoordinator.Acquire(settingsPath);
            if (instance is null)
            {
                if (!SingleInstanceCoordinator.ShowExistingAsync(settingsPath).GetAwaiter().GetResult())
                    MessageBox.Show("Secondary 已在執行，但現有視窗暫時無法回應。請從系統匣顯示介面。", "Secondary");
                Shutdown(); return;
            }
        }
        var store = new SettingsStore(settingsPath);
        displays = new DisplayService();
        var model = new ShellViewModel(displays, store);
        var window = new MainWindow(model, registerHotkey: !benchmark,
            shortcutsPath: benchmarkDirectory is null ? null : Path.Combine(benchmarkDirectory, "shortcuts.json"));
        MainWindow = window;
        tray = new TrayService(window, () => Shutdown());
        window.Show();
        instance?.Listen(() => Dispatcher.BeginInvoke(() => window.ShowInterface()));
        if (!benchmark) _ = Monitoring.PresentMonStream.RecoverAbandonedSessionsAsync(message =>
            Dispatcher.BeginInvoke(() => model.Status = message));
        if (measurement is not null) _ = Monitoring.PerformanceRun.Run(window, model, measurement);
    }
    protected override void OnExit(ExitEventArgs e)
    {
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
