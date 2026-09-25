using System;
using System.IO;
using System.Windows;
using Secondary.Core;

namespace Secondary.App;

public partial class App : Application
{
    private TrayService? tray;
    private DisplayService? displays;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root: dependencies are supplied explicitly, without a background host.
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "settings.json");
        var benchmark = e.Args.Length == 3 && e.Args[0] == "--measure";
        string? report = null;
        if (benchmark)
        {
            MessageBox.Show("目前為新版第一階段外觀預覽，未啟用真實監測。舊版效能測試已停用，避免將示意畫面誤作監測效能結果。", "Secondary");
            Shutdown();
            return;
        }
        var store = new SettingsStore(settingsPath);
        displays = new DisplayService();
        var model = new ShellViewModel(displays, store);
        var window = new MainWindow(model, registerHotkey: !benchmark);
        MainWindow = window;
        tray = new TrayService(window, () => Shutdown());
        window.Show();
        if (report is not null) _ = Monitoring.PerformanceRun.Run(window, model, report, e.Args[2] == "GameAfterFull");
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
        base.OnExit(e);
    }
}
