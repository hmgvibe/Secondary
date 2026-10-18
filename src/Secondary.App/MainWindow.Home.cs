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
        HomeSettingsStatus.Text = "套用後儲存；關閉不會保留本次變更。";
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
    { LoadHomeEditor(new()); HomeSettingsStatus.Text = "已恢復編輯欄位，按套用後儲存。原背景檔案不會刪除。"; }
    private void OnCenterClock(object sender, RoutedEventArgs e)
    {
        ClockXSlider.Value = ClockYSlider.Value = .5;
        HomeSettingsStatus.Text = "位置已歸中；按套用後儲存。";
    }
    private void UpdateBackgroundEditor()
    {
        var kind = (BackgroundPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "Static";
        BackgroundFileFields.Visibility = kind is "Image" or "Video" ? Visibility.Visible : Visibility.Collapsed;
        BackgroundHelp.Text = kind switch
        {
            "Image" => "選擇本機圖片；只參照檔案，不複製或上傳。最大 80 MB，超大圖片會縮小解碼。",
            "Video" => "本機 MP4 H.264，循環靜音；離開首頁停止播放。無法解碼時回退靜態背景。",
            _ => "靜態背景跟隨明暗主題，無需選擇檔案。"
        };
    }
    private void OnBrowseBackground(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "選擇首頁背景", Filter = "圖片或 MP4 影片|*.jpg;*.jpeg;*.png;*.bmp;*.mp4|圖片|*.jpg;*.jpeg;*.png;*.bmp|MP4 影片|*.mp4", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        if (!HomeSettings.IsLocalPath(picker.FileName)) { HomeSettingsStatus.Text = "請選擇本機磁碟上的檔案，不支援網路位置。"; return; }
        BackgroundPathInput.Text = picker.FileName;
        BackgroundPicker.SelectedIndex = Path.GetExtension(picker.FileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        HomeSettingsStatus.Text = "已選擇檔案；按套用後才會載入。";
    }
    private void OnApplyHome(object sender, RoutedEventArgs e)
    {
        var color = ClockColorInput.Text.Trim();
        if (!HomeSettings.ValidColor(color)) { HomeSettingsStatus.Text = "顏色請填 Auto 或 #RRGGBB，例如 #65DCEC。"; ClockColorInput.Focus(); return; }
        var background = (string)((ComboBoxItem)BackgroundPicker.SelectedItem).Tag;
        var path = BackgroundPathInput.Text;
        if (background is "Image" or "Video" && (!HomeSettings.IsLocalPath(path) || !File.Exists(path)))
        { HomeSettingsStatus.Text = "請先選擇存在的本機背景檔案。"; return; }
        var options = new HomeSettings((string)((ComboBoxItem)ClockStylePicker.SelectedItem).Tag,
            ClockSizeSlider.Value, color, ClockXSlider.Value, ClockYSlider.Value,
            Clock24Toggle.IsChecked == true, ClockSecondsToggle.IsChecked == true, ClockDateToggle.IsChecked == true,
            background, string.IsNullOrEmpty(path) ? null : path, model.Settings.Home?.Widgets,
            ClockSizeSlider.Value == model.Settings.Home?.Size ? model.Settings.Home?.ClockWidthScale : null,
            ClockSizeSlider.Value == model.Settings.Home?.Size ? model.Settings.Home?.ClockHeightScale : null);
        if (!model.SaveHome(options)) { HomeSettingsStatus.Text = model.Status; return; }
        ClockView.Configure(options); HomeBackground.Configure(options);
        HomeSettingsStatus.Text = "首頁設定已儲存。";
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
