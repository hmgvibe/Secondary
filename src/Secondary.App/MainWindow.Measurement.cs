using System;
using System.Windows;
using Secondary.Core;

namespace Secondary.App;

public partial class MainWindow
{
    public string MeasurementSourceStatus => GameNotice.Text + " / " + TelemetryStatus.Text;
    internal string? MeasurementProblem(string scene)
    {
        if (SettingsOpen || HomeCustomizeOpen) return "量測時有設定頁或自訂彈窗開啟。";
        var game = scene is "GameLow" or "GameHigh" or "GameSensors" or "Hidden" or "Minimized";
        if (CurrentPage != (game ? "game" : scene == "Shortcuts" ? "shortcuts" : "home")) return "量測頁面與指定情境不一致。";
        if (game)
        {
            var high = scene is "GameHigh" or "GameSensors";
            if (SamplingPicker.SelectedIndex != (high ? 1 : 0) || GameSensorsToggle.IsChecked != (scene == "GameSensors")) return "採樣或硬體助手選項與指定情境不一致。";
            if (scene is "Hidden" or "Minimized")
            {
                if (GameMonitoringActive || !GameMonitoringCompletion.IsCompletedSuccessfully) return "隱藏或最小化後，監測工作尚未完成停止。";
            }
            else if (!GameMonitoringActive) return "遊戲監測工作未啟動。";
        }
        if (scene == "HomeWidgets" && UtilityWidgetCount != 12) return "首頁沒有指定的十二個組件。";
        if (scene == "Shortcuts" && Launchpad.EntryCount != 200) return "快捷導航沒有指定的二百筆入口。";
        if (scene == "Image" && (!HomeBackground.HasImage || HomeBackground.IsImageLoading || HomeBackground.Status.Length != 0)) return "圖片背景尚未載入或已回退。";
        if (scene == "Video" && (!HomeBackground.HasMedia || !HomeBackground.MediaReady || HomeBackground.Status.Length != 0)) return "影片背景尚未播放或已回退。";
        return null;
    }
    internal void PrepareMeasurement(string scene, string? media)
    {
        model.SaveRuntime(gameAnimation: false);
        if (scene is "GameLow" or "GameHigh" or "GameSensors" or "Hidden" or "Minimized")
        {
            SamplingPicker.SelectedIndex = scene is "GameHigh" or "GameSensors" ? 1 : 0;
            GameSensorsToggle.IsChecked = scene == "GameSensors";
            Navigate("enter-game");
            if (scene == "Hidden") Hide();
            else if (scene == "Minimized") WindowState = WindowState.Minimized;
        }
        else
        {
            Navigate("home");
            if (scene == "HomeWidgets")
                for (var i = 0; i < 12; i++) AddHomeWidget(HomeWidgetSettings.Kinds[i % HomeWidgetSettings.Kinds.Length]);
            else if (scene == "Shortcuts") { Launchpad.PrepareMeasurementEntries(); Navigate("shortcuts"); }
            else if (scene is "Image" or "Video")
            {
                var options = new HomeSettings(Background: scene, MediaPath: media);
                model.SaveHome(options); ClockView.Configure(options); HomeBackground.Configure(options);
            }
        }
    }
}
