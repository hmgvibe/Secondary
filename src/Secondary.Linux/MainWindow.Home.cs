using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Secondary.Core;
using Secondary.Linux.Home;
using Secondary.Shared;

namespace Secondary.Linux;

public partial class MainWindow
{
    private static readonly LayoutSize UtilityNaturalSize = new(320, 300);
    private readonly Dictionary<string, (WidgetFrame Frame, UtilityWidget Content)> utilityWidgets = new();
    private ClockView clockView = null!;
    private TaskCompletionSource<bool>? confirmation;
    private IInputElement? homePreviousFocus;
    private readonly List<IDisposable> labelBindings = new();
    private bool sessionLocked, suspended, homeDisposed;
    public WidgetFrame ClockFrame { get; } = new();
    public ClockView Clock => clockView;
    public int UtilityWidgetCount => utilityWidgets.Count;
    public IReadOnlyDictionary<string, (WidgetFrame Frame, UtilityWidget Content)> UtilityWidgets => utilityWidgets;
    public bool HomeCustomizeOpen => HomeCustomizeOverlay.IsVisible;
    public bool ConfirmOpen => ConfirmOverlay.IsVisible;
    public bool HomeActive { get; private set; }
    // Tests may substitute a decoder; the app uses the system libvlc.
    public static Func<string, IVideoBackground> VideoFactory { get; set; } = Platform.LibVlcVideo.Create;
    public string HomeStatus => HomeMediaStatus.Text ?? "";

    private void InitializeHome()
    {
        clockView = new ClockView();
        ClockFrame.Attach(WidgetCanvas, clockView);
        ClockFrame.TouchSelected += () => ClearTouchSelection(ClockFrame);
        // A tap on empty canvas ends a touch selection.
        WidgetCanvas.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, WidgetCanvas)) ClearTouchSelection(null); };
        clockView.ConfigurationChanged += ConfigureClockFrame;
        ClockFrame.PlacementCommitted += placement =>
        {
            var current = model.Settings.Home ?? new HomeSettings();
            var updated = current with { X = placement.X, Y = placement.Y, Size = placement.Scale, ClockWidthScale = placement.WidthScale, ClockHeightScale = placement.HeightScale };
            if (!model.SaveHome(updated)) { HomeMediaStatus.Text = model.Status; ConfigureClockFrame(current); }
        };
        var options = model.Settings.Home ?? new HomeSettings();
        foreach (var widget in options.Widgets ?? []) AttachUtilityWidget(widget);
        LoadHomeEditor(options);
        clockView.Configure(options);
        HomeBackground.VideoFactory = VideoFactory;
        HomeBackground.Configure(options);
        HomeBackground.StatusChanged += OnBackgroundStatus;
        foreach (var (slider, readout) in new[] { (ClockSizeSlider, ClockSizeReadout), (ClockXSlider, ClockXReadout), (ClockYSlider, ClockYReadout) })
        {
            slider.ValueChanged += (_, _) => readout.Text = slider.Value.ToString("P0", CultureInfo.InvariantCulture);
            readout.Text = slider.Value.ToString("P0", CultureInfo.InvariantCulture);
        }
        UpdateBackgroundEditor();
        WidgetCanvas.SizeChanged += (_, _) => ConfigureClockFrame(clockView.Options);
    }
    private void ConfigureClockFrame(HomeSettings options) =>
        ClockFrame.Configure(new WidgetPlacement(options.X, options.Y, options.Size, options.ClockWidthScale, options.ClockHeightScale), clockView.NaturalSize);

    private void OnBackgroundStatus(string text)
    {
        HomeMediaStatus.Text = text;
        HomeHint.IsVisible = text.Length == 0;
        if (text.Length > 0) HomeSettingsStatus.Text = text;
        var options = model.Settings.Home ?? new HomeSettings();
        clockView.Configure(HomeBackground.HasVisualBackground ? options : options with { Background = "Static" });
        // Labels over a picture or animation stay white; otherwise they follow the theme.
        foreach (var binding in labelBindings) binding.Dispose();
        labelBindings.Clear();
        foreach (var label in new[] { BrandLabel, StageLabel })
        {
            label.ClearValue(TextBlock.ForegroundProperty);
            if (HomeBackground.HasVisualBackground) label.Foreground = Brushes.White;
            else labelBindings.Add(label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("MutedBrush")));
        }
    }

    // Home timers, media and background run only while home is actually on screen.
    private void UpdateHomeActivity()
    {
        UpdateGameActivity();
        if (homeDisposed || clockView is null) return;
        var active = CurrentPage == "home" && !HomeCustomizeOpen && IsVisible && WindowState != WindowState.Minimized && !sessionLocked && !suspended;
        HomeActive = active;
        var motion = AnimationsEnabled;
        clockView.MotionAllowed = motion;
        clockView.HighContrast = highContrast;
        if (!active) { ClockFrame.CancelInteraction(); ClearTouchSelection(null); }
        clockView.SetActive(active);
        foreach (var widget in utilityWidgets.Values)
        {
            if (!active) widget.Frame.CancelInteraction();
            widget.Content.SetActive(active);
        }
        HomeBackground.SetActive(active, motion, highContrast);
    }
    // Locking or sleeping also cancels a running macro, as on Windows.
    public void SetSessionLocked(bool locked) { sessionLocked = locked; if (locked) Launchpad.Cancel(); UpdateHomeActivity(); }
    public void SetSuspended(bool sleeping) { suspended = sleeping; if (sleeping) Launchpad.Cancel(); UpdateHomeActivity(); }

    private void OnHomeCustomize(object? sender, RoutedEventArgs e) => OpenHomeCustomize();
    public void OpenHomeCustomize()
    {
        if (CurrentPage != "home" || HomeCustomizeOpen) return;
        LoadHomeEditor(model.Settings.Home ?? new HomeSettings());
        UpdateBackgroundEditor();
        HomeSettingsStatus.Text = "套用後儲存；關閉不會保留本次變更。";
        homePreviousFocus = FocusManager?.GetFocusedElement();
        HomeCustomizeOverlay.IsVisible = true;
        HomeLayer.IsEnabled = NavigationScale.IsEnabled = false;
        HomeEditorScroll.Offset = default;
        UpdateHomeActivity();
        ClockStylePicker.Focus();
    }
    public void CloseHomeCustomize()
    {
        if (!HomeCustomizeOpen) return;
        HomeCustomizeOverlay.IsVisible = false;
        HomeLayer.IsEnabled = NavigationScale.IsEnabled = true;
        UpdateHomeActivity();
        homePreviousFocus?.Focus();
        homePreviousFocus = null;
    }
    private void OnCloseHomeCustomize(object? sender, RoutedEventArgs e) => CloseHomeCustomize();
    private void LoadHomeEditor(HomeSettings options)
    {
        ClockStylePicker.SelectedItem = ClockStylePicker.Items.OfType<ComboBoxItem>().First(i => (string?)i.Tag == options.ClockStyle);
        BackgroundPicker.SelectedItem = BackgroundPicker.Items.OfType<ComboBoxItem>().First(i => (string?)i.Tag == options.Background);
        ClockSizeSlider.Value = options.Size; ClockXSlider.Value = options.X; ClockYSlider.Value = options.Y;
        Clock24Toggle.IsChecked = options.Use24Hours; ClockSecondsToggle.IsChecked = options.ShowSeconds; ClockDateToggle.IsChecked = options.ShowDate;
        ClockColorInput.Text = options.Color; BackgroundPathInput.Text = options.MediaPath ?? "";
    }
    private void OnResetHome(object? sender, RoutedEventArgs e) { LoadHomeEditor(new()); HomeSettingsStatus.Text = "已恢復編輯欄位，按套用後儲存。原背景檔案不會刪除。"; }
    private void OnCenterClock(object? sender, RoutedEventArgs e) { ClockXSlider.Value = ClockYSlider.Value = .5; HomeSettingsStatus.Text = "位置已歸中；按套用後儲存。"; }
    private void OnBackgroundKindChanged(object? sender, SelectionChangedEventArgs e) => UpdateBackgroundEditor();
    private void UpdateBackgroundEditor()
    {
        if (BackgroundFileFields is null) return;
        var kind = (BackgroundPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "Static";
        BackgroundFileFields.IsVisible = kind is "Image" or "Video";
        BackgroundHelp.Text = kind switch
        {
            "Image" => "選擇本機圖片；只參照檔案，不複製或上傳。最大 80 MB，超大圖片會縮小解碼。",
            "Video" => "本機 MP4 H.264，循環靜音；需要系統已安裝的 VLC 播放元件（libvlc）。離開首頁停止播放；無法解碼時回退靜態背景。",
            _ => "靜態背景跟隨明暗主題，無需選擇檔案。"
        };
    }
    private async void OnBrowseBackground(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇首頁背景", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("圖片或 MP4 影片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.mp4"] },
                new FilePickerFileType("圖片") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"] }, new FilePickerFileType("MP4 影片") { Patterns = ["*.mp4"] }]
        });
        if (files.Count == 0) return;
        SelectBackgroundFile(files[0].TryGetLocalPath());
    }
    public void SelectBackgroundFile(string? path)
    {
        if (!HomeSettings.IsLocalPath(path)) { HomeSettingsStatus.Text = "請選擇本機磁碟上的檔案，不支援網路位置。"; return; }
        BackgroundPathInput.Text = path;
        BackgroundPicker.SelectedIndex = Path.GetExtension(path!).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        HomeSettingsStatus.Text = "已選擇檔案；按套用後才會載入。";
    }
    private void OnApplyHome(object? sender, RoutedEventArgs e) => ApplyHome();
    public bool ApplyHome()
    {
        var color = (ClockColorInput.Text ?? "").Trim();
        if (!HomeSettings.ValidColor(color)) { HomeSettingsStatus.Text = "顏色請填 Auto 或 #RRGGBB，例如 #65DCEC。"; ClockColorInput.Focus(); return false; }
        var background = (string)((ComboBoxItem)BackgroundPicker.SelectedItem!).Tag!;
        var path = BackgroundPathInput.Text ?? "";
        if (background is "Image" or "Video" && (!HomeSettings.IsLocalPath(path) || !File.Exists(path)))
        { HomeSettingsStatus.Text = "請先選擇存在的本機背景檔案。"; return false; }
        var current = model.Settings.Home;
        var options = new HomeSettings((string)((ComboBoxItem)ClockStylePicker.SelectedItem!).Tag!,
            ClockSizeSlider.Value, color, ClockXSlider.Value, ClockYSlider.Value,
            Clock24Toggle.IsChecked == true, ClockSecondsToggle.IsChecked == true, ClockDateToggle.IsChecked == true,
            background, path.Length == 0 ? null : path, current?.Widgets,
            ClockSizeSlider.Value == current?.Size ? current?.ClockWidthScale : null,
            ClockSizeSlider.Value == current?.Size ? current?.ClockHeightScale : null);
        if (!model.SaveHome(options)) { HomeSettingsStatus.Text = model.Status; return false; }
        clockView.Configure(options); HomeBackground.Configure(options);
        HomeSettingsStatus.Text = "首頁設定已儲存。";
        CloseHomeCustomize();
        return true;
    }

    private void OnAddWidget(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var kind in HomeWidgetSettings.Kinds)
        {
            var header = new StackPanel { MinWidth = 240 };
            header.Children.Add(new TextBlock { Text = UtilityWidget.Title(kind), FontSize = 15, FontWeight = FontWeight.SemiBold });
            var description = new TextBlock { Text = UtilityWidget.Description(kind), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("MutedBrush"));
            header.Children.Add(description);
            var item = new MenuItem { Header = header, IsEnabled = utilityWidgets.Count < 12 };
            AutomationProperties.SetName(item, UtilityWidget.Title(kind));
            item.Click += (_, _) => AddHomeWidget(kind);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        if (utilityWidgets.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "選取現有組件", IsEnabled = false });
            var ordinal = 0;
            foreach (var widget in utilityWidgets.Values)
            {
                var item = new MenuItem { Header = $"{++ordinal}. {UtilityWidget.Title(widget.Content.Settings.Kind)}" };
                item.Click += (_, _) => { BringToFront(widget.Frame); widget.Frame.SelectForEditing(); };
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuItem { Header = $"已加入 {utilityWidgets.Count} / 12  ·  可重複新增", IsEnabled = false });
        menu.ShowAt((Control)sender!);
    }
    private void BringToFront(WidgetFrame frame)
    {
        foreach (var other in utilityWidgets.Values) other.Frame.ZIndex = 0;
        frame.ZIndex = 1;
    }
    public bool AddHomeWidget(string kind)
    {
        if (!HomeWidgetSettings.Supported(kind) || utilityWidgets.Count >= 12) return false;
        var home = model.Settings.Home ?? new();
        var occupied = WidgetCanvas.Children.OfType<WidgetFrame>().Select(frame => new LayoutRect(
            Math.Max(0, Canvas.GetLeft(frame)), Math.Max(0, Canvas.GetTop(frame)), Settled(frame.Width), Settled(frame.Height))).ToArray();
        var placement = WidgetLayout.FindNewPlacement(new LayoutSize(WidgetCanvas.Bounds.Width, WidgetCanvas.Bounds.Height), occupied, utilityWidgets.Count, out var crowded);
        var widget = new HomeWidgetSettings(Guid.NewGuid().ToString("N"), kind, placement);
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Append(widget).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        AttachUtilityWidget(widget);
        BringToFront(utilityWidgets[widget.Id].Frame);
        utilityWidgets[widget.Id].Frame.SelectForEditing();
        HomeMediaStatus.Text = crowded ? "首頁空間已滿；新組件已放在最上層，可拖動調整或從組件清單選取。" : "";
        HomeHint.IsVisible = !crowded;
        UpdateHomeActivity();
        return true;
    }
    // Frames size themselves explicitly; their rendered bounds lag until the next layout pass.
    private static double Settled(double size) => double.IsFinite(size) ? Math.Max(0, size) : 0;
    private void AttachUtilityWidget(HomeWidgetSettings settings)
    {
        var frame = new WidgetFrame();
        var content = new UtilityWidget(settings);
        frame.Attach(WidgetCanvas, content, UtilityWidget.Title(settings.Kind));
        frame.Configure(settings.Placement ?? new(), UtilityNaturalSize);
        utilityWidgets.Add(settings.Id, (frame, content));
        frame.ZIndex = 1;
        frame.Activated += () => BringToFront(frame);
        frame.TouchSelected += () => ClearTouchSelection(frame);
        frame.PlacementCommitted += placement =>
        {
            var current = (model.Settings.Home?.Widgets ?? []).FirstOrDefault(w => w.Id == settings.Id);
            if (current is not null && !SaveUtilityWidget(current with { Placement = placement })) frame.Configure(current.Placement ?? new(), UtilityNaturalSize);
        };
        content.SaveRequested += next =>
        {
            var current = (model.Settings.Home?.Widgets ?? []).FirstOrDefault(w => w.Id == settings.Id);
            return current is not null && SaveUtilityWidget(next with { Placement = current.Placement });
        };
        var remove = new MenuItem { Header = "移除" + UtilityWidget.Title(settings.Kind) };
        remove.Click += async (_, _) =>
        {
            if (await ConfirmAsync("移除組件", "移除此組件？其中的資料及未儲存編輯也會一併移除。", "移除")) RemoveHomeWidget(settings.Id);
        };
        frame.ContextMenu = new ContextMenu { Items = { remove } };
        if (settings.Kind == "AiUsage")
        {
            var online = new MenuItem { Header = OnlineUsageHeader, ToggleType = MenuItemToggleType.CheckBox, IsChecked = content.Settings.ClaudeOnline };
            online.Click += async (_, _) =>
            {
                var enable = !content.Settings.ClaudeOnline;
                if (enable && !await ConfirmAsync("啟用 Claude 線上用量", OnlineUsageWarning, "啟用")) { online.IsChecked = false; return; }
                content.SetClaudeOnline(enable);
                online.IsChecked = content.Settings.ClaudeOnline;
            };
            frame.ContextMenu.Items.Insert(0, online);
        }
    }
    internal const string OnlineUsageHeader = "Claude 線上用量（非官方）";
    private const string OnlineUsageWarning =
        "Secondary 會讀取 ~/.claude/.credentials.json 裡 Claude Code CLI 的登入 token，最多每 5 分鐘傳送一次到 api.anthropic.com 的未公開用量端點，以取得五小時與每週百分比。\n\n" +
        "這個端點沒有公開文件，可能隨時失效或改變。token 只送往該端點，不會儲存或顯示。Claude 桌面版的登入不在這個檔案，只用桌面版時會顯示「無法取得」。";
    private bool SaveUtilityWidget(HomeWidgetSettings next)
    {
        var home = model.Settings.Home ?? new();
        var saved = model.SaveHome(home with { Widgets = (home.Widgets ?? []).Select(w => w.Id == next.Id ? next : w).ToArray() });
        if (!saved) HomeMediaStatus.Text = model.Status;
        return saved;
    }
    public bool RemoveHomeWidget(string id)
    {
        if (!utilityWidgets.TryGetValue(id, out var widget)) return false;
        var home = model.Settings.Home ?? new();
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Where(w => w.Id != id).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        widget.Content.Dispose(); widget.Frame.Detach(); utilityWidgets.Remove(id);
        return true;
    }
    private void ClearTouchSelection(WidgetFrame? keep)
    {
        foreach (var frame in utilityWidgets.Values.Select(w => w.Frame).Append(ClockFrame))
            if (frame != keep) frame.ClearTouchSelection();
    }
    private bool CancelWidgetInteraction()
    {
        var frames = utilityWidgets.Values.Select(w => w.Frame).Append(ClockFrame).Where(w => w.IsInteracting).ToArray();
        foreach (var frame in frames) frame.CancelInteraction();
        return frames.Length > 0;
    }

    // In-window confirmation; Avalonia has no system message box on Linux.
    public Task<bool> ConfirmAsync(string title, string message, string accept)
    {
        confirmation?.TrySetResult(false);
        confirmation = new TaskCompletionSource<bool>();
        ConfirmTitle.Text = title; ConfirmMessage.Text = message; ConfirmAccept.Content = accept;
        ConfirmOverlay.IsVisible = true;
        ConfirmCancel.Focus();
        return confirmation.Task;
    }
    private void CloseConfirm(bool result)
    {
        ConfirmOverlay.IsVisible = false;
        var pending = confirmation; confirmation = null;
        pending?.TrySetResult(result);
    }
    public string ConfirmText => ConfirmMessage.Text ?? "";
    public void AnswerConfirm(bool accept) { if (ConfirmOpen) CloseConfirm(accept); }
    private void OnConfirmAccept(object? sender, RoutedEventArgs e) => CloseConfirm(true);
    private void OnConfirmCancel(object? sender, RoutedEventArgs e) => CloseConfirm(false);

    private void DisposeHome()
    {
        homeDisposed = true;
        foreach (var widget in utilityWidgets.Values) { widget.Content.Dispose(); widget.Frame.Detach(); }
        utilityWidgets.Clear();
        HomeBackground.StatusChanged -= OnBackgroundStatus;
        HomeBackground.Dispose(); clockView.Dispose();
        clockView.ConfigurationChanged -= ConfigureClockFrame;
    }
}
