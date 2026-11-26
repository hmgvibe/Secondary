using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Secondary.Core;

namespace Secondary.App;

public partial class MainWindow
{
    private bool sessionLocked, suspended, homeDisposed;
    public Presentation.HomeWidget ClockWidget { get; } = new();
    private void ConfigureClockWidget(HomeSettings options)
    {
        ClockWidget.Configure(new WidgetPlacement(options.X,options.Y,options.Size,options.ClockWidthScale,options.ClockHeightScale),ClockView.NaturalSize);
    }
    public bool HomeCustomizeOpen => HomeCustomizeOverlay.Visibility == Visibility.Visible;
    private System.Windows.IInputElement? homePreviousFocus;
    public void OpenHomeCustomize()
    {
        if (CurrentPage != "home" || HomeCustomizeOpen) return;
        LoadHomeEditor(model.Settings.Home ?? new HomeSettings());
        UpdateBackgroundEditor();
        HomeSettingsStatus.Text = ("套用后保存；关闭不会保留本次变更。");
        homePreviousFocus = System.Windows.Input.Keyboard.FocusedElement;
        HomeCustomizeOverlay.Visibility = Visibility.Visible;
        Frame.IsEnabled = NavigationBar.IsEnabled = false;
        HomeEditorScroll.ScrollToTop();
        UpdateHomeActivity();
        ClockStylePicker.Focus();
    }
    public void CloseHomeCustomize()
    {
        if (!HomeCustomizeOpen) return;
        HomeCustomizeOverlay.Visibility = Visibility.Collapsed;
        Frame.IsEnabled = NavigationBar.IsEnabled = true;
        UpdateHomeActivity();
        if (homePreviousFocus is not null) System.Windows.Input.Keyboard.Focus(homePreviousFocus);
        homePreviousFocus = null;
    }
    private void OnCloseHomeCustomize(object sender, RoutedEventArgs e) => CloseHomeCustomize();
    private void InitializeHome()
    {
        WidgetCanvas.Children.Remove(ClockView);
        ClockView.FitWidget = true;
        ClockWidget.Attach(WidgetCanvas,ClockView);
        ClockView.ConfigurationChanged += ConfigureClockWidget;
        ClockWidget.PlacementCommitted += placement =>
        {
            var current = model.Settings.Home ?? new HomeSettings();
            var updated = current with { X = placement.X, Y = placement.Y, Size = placement.Scale, ClockWidthScale = placement.WidthScale, ClockHeightScale = placement.HeightScale };
            if (!model.SaveHome(updated)) { HomeMediaStatus.Text = model.Status; ConfigureClockWidget(current); }
        };
        var options = model.Settings.Home ?? new HomeSettings();
        foreach (var widget in options.Widgets ?? []) AttachUtilityWidget(widget);
        LoadHomeEditor(options);
        ClockView.Configure(options); HomeBackground.Configure(options);
        HomeBackground.StatusChanged += OnBackgroundStatus;
        BackgroundPicker.SelectionChanged += (_, _) => UpdateBackgroundEditor();
        UpdateBackgroundEditor();
    }
    private void OnBackgroundStatus(string text)
    {
        HomeMediaStatus.Text = text;
        if (text.Length > 0) HomeSettingsStatus.Text = text;
        var options = model.Settings.Home ?? new HomeSettings();
        ClockView.Configure(HomeBackground.HasVisualBackground ? options : options with { Background = "Static" });
        foreach (var label in new[] { BrandLabel, StageLabel })
            if (HomeBackground.HasVisualBackground) label.Foreground = System.Windows.Media.Brushes.White;
            else label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
    }
    private void LoadHomeEditor(HomeSettings options)
    {
        ClockStylePicker.SelectedItem = ClockStylePicker.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == options.ClockStyle);
        BackgroundPicker.SelectedItem = BackgroundPicker.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == options.Background);
        ClockSizeSlider.Value = options.Size; ClockXSlider.Value = options.X; ClockYSlider.Value = options.Y;
        Clock24Toggle.IsChecked = options.Use24Hours; ClockSecondsToggle.IsChecked = options.ShowSeconds; ClockDateToggle.IsChecked = options.ShowDate;
        ClockColorInput.Text = options.Color; BackgroundPathInput.Text = options.MediaPath ?? "";
    }
    private void OnResetHome(object sender, RoutedEventArgs e)
    { LoadHomeEditor(new()); HomeSettingsStatus.Text = ("已恢复编辑栏位，按套用后保存。原背景文件不会删除。"); }
    private void OnCenterClock(object sender, RoutedEventArgs e)
    {
        ClockXSlider.Value = ClockYSlider.Value = .5;
        HomeSettingsStatus.Text = ("位置已归中；按套用后保存。");
    }
    private void UpdateBackgroundEditor()
    {
        var kind = (BackgroundPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "Static";
        BackgroundFileFields.Visibility = kind is "Image" or "Video" ? Visibility.Visible : Visibility.Collapsed;
        BackgroundHelp.Text = kind switch
        {
            "Image" => ("选择本机图片；只参照文件，不复制或上传。最大 80 MB，超大图片会缩小解码。"),
            "Video" => ("本机 MP4 H.264，循环静音；离开首页停止播放。无法解码时回退静态背景。"),
            _ => ("静态背景跟随明暗主题，无需选择文件。")
        };
    }
    private void OnBrowseBackground(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = ("选择首页背景"), Filter = ("图片或 MP4 视频|*.jpg;*.jpeg;*.png;*.bmp;*.mp4|图片|*.jpg;*.jpeg;*.png;*.bmp|MP4 视频|*.mp4"), CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (!HomeSettings.IsLocalPath(picker.FileName)) { HomeSettingsStatus.Text = ("请选择本机磁碟上的文件，不支持网络位置。"); return; }
        BackgroundPathInput.Text = picker.FileName;
        BackgroundPicker.SelectedIndex = Path.GetExtension(picker.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        HomeSettingsStatus.Text = ("已选择文件；按套用后才会载入。");
    }
    private void OnApplyHome(object sender, RoutedEventArgs e)
    {
        var color = ClockColorInput.Text.Trim();
        if (!HomeSettings.ValidColor(color)) { HomeSettingsStatus.Text = ("颜色请填 Auto 或 #RRGGBB，例如 #65DCEC。"); ClockColorInput.Focus(); return; }
        var background = (string)((ComboBoxItem)BackgroundPicker.SelectedItem).Tag;
        var path = BackgroundPathInput.Text;
        if (background is "Image" or "Video" && (!HomeSettings.IsLocalPath(path) || !File.Exists(path)))
        { HomeSettingsStatus.Text = ("请先选择存在的本机背景文件。"); return; }
        var options = new HomeSettings((string)((ComboBoxItem)ClockStylePicker.SelectedItem).Tag,
            ClockSizeSlider.Value, color, ClockXSlider.Value, ClockYSlider.Value,
            Clock24Toggle.IsChecked == true, ClockSecondsToggle.IsChecked == true, ClockDateToggle.IsChecked == true,
            background, string.IsNullOrEmpty(path) ? null : path, model.Settings.Home?.Widgets,
            ClockSizeSlider.Value == model.Settings.Home?.Size ? model.Settings.Home?.ClockWidthScale : null,
            ClockSizeSlider.Value == model.Settings.Home?.Size ? model.Settings.Home?.ClockHeightScale : null);
        if (!model.SaveHome(options)) { HomeSettingsStatus.Text = model.Status; return; }
        ClockView.Configure(options); HomeBackground.Configure(options);
        HomeSettingsStatus.Text = ("首页设置已保存。");
        CloseHomeCustomize();
    }
    private void UpdateHomeActivity()
    {
        ConfigureGameTelemetry();
        if (sessionLocked || suspended) StopSensorCapture();
        if (homeDisposed || ClockView is null || HomeBackground is null) return;
        var active = CurrentPage == "home" && !HomeCustomizeOpen && IsVisible && WindowState != WindowState.Minimized && !sessionLocked && !suspended;
        var motion = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        if (sessionLocked || suspended) Launchpad?.Cancel();
        ClockView.MotionAllowed = motion;
        if (!active) ClockWidget.CancelInteraction();
        ClockView.SetActive(active);
        foreach (var widget in utilityWidgets.Values)
        {
            if (!active) widget.Frame.CancelInteraction();
            widget.Content.SetActive(active);
        }
        HomeBackground.SetActive(active, motion);
        if (!motion) ClockView.BeginAnimation(Presentation.HomeClock.FlipProgressProperty, null);
        ClockView.InvalidateVisual();
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect) sessionLocked = true;
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect) sessionLocked = false;
        UpdateHomeActivity();
    });
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    { suspended = e.Mode == PowerModes.Suspend || (suspended && e.Mode != PowerModes.Resume); UpdateHomeActivity(); });
    private void DisposeHome()
    {
        homeDisposed = true;
        foreach (var widget in utilityWidgets.Values) { widget.Content.Dispose(); widget.Frame.Detach(); }
        utilityWidgets.Clear();
        SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        HomeBackground.StatusChanged -= OnBackgroundStatus; HomeBackground.Dispose(); ClockView.Dispose();
        ClockView.ConfigurationChanged -= ConfigureClockWidget;
    }
}
