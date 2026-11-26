using System;
using System.Windows;
using Secondary.Core;

namespace Secondary.App;

public partial class MainWindow
{
    public string MeasurementSourceStatus => GameNotice.Text + " / " + TelemetryStatus.Text;
    internal string? MeasurementProblem(string scene)
    {
        if (SettingsOpen || HomeCustomizeOpen) return "量测时有设置页或自订弹窗开启。";
        var game = scene is "GameLow" or "GameHigh" or "GameSensors" or "Hidden" or "Minimized";
        if (CurrentPage != (game ? "game" : scene == "Shortcuts" ? "shortcuts" : "home")) return "量测页面与指定情境不一致。";
        if (game)
        {
            var high = scene is "GameHigh" or "GameSensors";
            if (SamplingPicker.SelectedIndex != (high ? 1 : 0) || GameSensorsToggle.IsChecked != (scene == "GameSensors")) return "采样或硬件助手选项与指定情境不一致。";
            if (scene is "Hidden" or "Minimized")
            {
                if (GameMonitoringActive || !GameMonitoringCompletion.IsCompletedSuccessfully) return "隐藏或最小化后，监测工作尚未完成停止。";
            }
            else if (!GameMonitoringActive) return "游戏监测工作未启动。";
        }
        if (scene == "HomeWidgets" && UtilityWidgetCount != 12) return "首页没有指定的十二个组件。";
        if (scene == "Shortcuts" && Launchpad.EntryCount != 200) return "快捷导航没有指定的二百笔入口。";
        if (scene == "Image" && (!HomeBackground.HasImage || HomeBackground.IsImageLoading || HomeBackground.Status.Length != 0)) return "图片背景尚未载入或已回退。";
        if (scene == "Video" && (!HomeBackground.HasMedia || !HomeBackground.MediaReady || HomeBackground.Status.Length != 0)) return "视频背景尚未播放或已回退。";
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
