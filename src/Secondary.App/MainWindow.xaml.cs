using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Secondary.App.Presentation;
using Secondary.Core;

namespace Secondary.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel model;
    private string? activeDisplay;
    private bool fullscreen;
    private readonly ThemeService theme;
    private ThemeChoice themeChoice = ThemeChoice.System;
    private GlobalHotkey? hotkey;
    private string? registeredHotkey;
    private bool updatingMode, updatingNavigation, initializing = true;
    private Storyboard? transition;
    private IInputElement? previousFocus;
    public bool TransitionRunning => transition is not null;
    private bool settingsOpen;
    public bool SettingsOpen => settingsOpen;
    public string CurrentPage => SettingsOpen ? "settings" : model.Modes.Current;
    public bool AllowClose { get; set; }
    public MainWindow(ShellViewModel model, bool registerHotkey = true, string? shortcutsPath = null)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        theme = new ThemeService(this);
        themeChoice = Enum.Parse<ThemeChoice>(model.Settings.Theme);
        ThemePicker.SelectedIndex = (int)themeChoice;
        HotkeyInput.Text = model.Settings.Hotkey;
        GameAnimationToggle.IsChecked = model.Settings.GameAnimation;
        NavigationSizePicker.SelectedIndex = model.Settings.NavigationSize == "Small" ? 1 : 0;
        NavigationPositionPicker.SelectedItem = NavigationPositionPicker.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == model.Settings.NavigationPosition);
        InitializeHome();
        Launchpad = new Presentation.LaunchpadView(shortcutsPath ?? (registerHotkey
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Secondary","shortcuts.json")
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Secondary-tests-" + Guid.NewGuid().ToString("N"),"shortcuts.json")));
        Launchpad.CanRun = () => !IsGame && IsVisible && WindowState != WindowState.Minimized;
        ShortcutsPage.Children.Add(Launchpad);
        ContentScroll.AllowDrop = true;
        ContentScroll.Background = Brushes.Transparent;
        ContentScroll.PreviewDragOver += (_, e) => { if (CurrentPage == "shortcuts") Launchpad.HandleExternalDrag(e,false); };
        ContentScroll.PreviewDrop += (_, e) => { if (CurrentPage == "shortcuts") Launchpad.HandleExternalDrag(e,true); };
        SetAppearance((ShellAppearance)model.Modes.Mode);
        initializing = false;
        ShellRoot.SizeChanged += (_, _) => ApplyResponsiveLayout(ShellRoot.ActualWidth, ShellRoot.ActualHeight);
        ContentScroll.SizeChanged += (_, e) => SizeComponents(e.NewSize.Width);
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelTransition(); UpdateMonitoringVisibility(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) CancelTransition(); UpdateMonitoringVisibility(); };
        SourceInitialized += (_, _) =>
        {
            theme.Apply(model.Presentation.Appearance, themeChoice);
            if (registerHotkey)
            {
                hotkey = new GlobalHotkey(new WindowInteropHelper(this).Handle, ToggleGame);
                ApplyHotkey(model.Settings.Hotkey);
            }
        };
        model.TopologyChanged += OnTopologyChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Loaded += (_, _) =>
        {
            var saved = DisplayPolicy.FindSaved(model.Displays, model.Settings.DisplayId);
            if (saved is not null) EnterFullscreen(saved);
            else if (model.Settings.DisplayId is not null) model.Status = "上次的顯示器未連接，請選擇目前可用的顯示器。";
            if (IsGame) StartGameTransition();
            ConfigureGameTelemetry();
        };
    }
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
            Dispatcher.BeginInvoke(CancelTransition);
        Dispatcher.BeginInvoke(UpdateHomeActivity);
    }
    public void ShowPicker()
    {
        ShowInterface();
        OpenSettings();
        DisplayPicker.Focus();
    }
    public Presentation.ConnectionPanel? ConnectionCard { get; private set; }
    // Connection mode sits after the hotkey card; tests attach a loopback controller.
    public void AttachConnection(Secondary.Shared.Connection.ConnectionController? controller, string? unavailable = null, Func<string, string, string, bool>? confirm = null)
    {
        if (ConnectionCard is not null) SettingsPanels.Children.Remove(ConnectionCard);
        if (connection is not null) { connection.Changed -= OnConnectionChanged; connection.ToggleGameRequested -= OnRemoteToggle; }
        connection = controller;
        // Media widgets set to the main computer and the launchpad's main-computer section use this connection.
        Secondary.Shared.Connection.RemoteMediaClient.Current?.Dispose();
        Secondary.Shared.Connection.RemoteMediaClient.Current = controller is null ? null : new(controller);
        Launchpad.AttachRemote(controller);
        if (controller is not null)
        {
            // The main computer uses this device's hotkey while connected, and its game page follows the connection.
            controller.Hotkey = registeredHotkey ?? model.Settings.Hotkey;
            controller.Changed += OnConnectionChanged;
            controller.ToggleGameRequested += OnRemoteToggle;
        }
        ConnectionCard = new Presentation.ConnectionPanel(controller, confirm ?? ((title, text, accept) => Presentation.ShortcutDialogs.Confirm(this, title, text, accept)), unavailable);
        var hotkey = SettingsPanels.Children.OfType<UIElement>().FirstOrDefault(p => p.IsAncestorOf(HotkeyInput));
        SettingsPanels.Children.Insert(hotkey is null ? SettingsPanels.Children.Count : SettingsPanels.Children.IndexOf(hotkey) + 1, ConnectionCard);
    }
    private void OnConnectionChanged() => Dispatcher.BeginInvoke(ConfigureGameTelemetry);
    private void OnRemoteToggle() => Dispatcher.BeginInvoke(ToggleGame);
    public void ShowInterface()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void BuildNavigation()
    {
        updatingNavigation = true;
        NavigationItems.Children.Clear();
        if (IsGame)
        {
            AddNavigation("game", "遊戲監測", "\uE9D9");
            AddNavigation("exit-game", "退出遊戲模式", "\uE8BB");
            AddNavigation("settings", "設定", "\uE713");
        }
        else
        {
            AddNavigation("home", "首頁", "\uE80F");
            AddNavigation("shortcuts", "快捷導航", "\uE71D");
            AddNavigation("enter-game", "遊戲模式", "\uE7FC");
            AddNavigation("settings", "設定", "\uE713");
        }
        updatingNavigation = false;
        ShowCurrentPage();
        ApplyResponsiveLayout(ShellRoot.ActualWidth > 0 ? ShellRoot.ActualWidth : Width,
            ShellRoot.ActualHeight > 0 ? ShellRoot.ActualHeight : Height);
    }
    private void AddNavigation(string key, string name, string glyph)
    {
        var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 26 };
        icon.IsHitTestVisible = false;
        var tooltip = new ToolTip
        {
            Content = name,
            Style = (Style)FindResource("NavigationToolTip"),
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    model.Settings.NavigationPosition switch
                    {
                        "Top" => new Point((target.Width - popup.Width) / 2, target.Height + 8),
                        "Left" => new Point(target.Width + 8, (target.Height - popup.Height) / 2),
                        "Right" => new Point(-popup.Width - 8, (target.Height - popup.Height) / 2),
                        _ => new Point((target.Width - popup.Width) / 2, -popup.Height - 8)
                    },
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
            }
        };
        var button = new RadioButton { GroupName = "Sections", Tag = key, Content = icon, ToolTip = tooltip, Style = (Style)FindResource("NavigationButton") };
        ToolTipService.SetInitialShowDelay(button, 250);
        ToolTipService.SetBetweenShowDelay(button, 50);
        ToolTipService.SetShowDuration(button, 60000);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, "Navigation." + key);
        button.Checked += (_, _) => { if (!updatingNavigation) Navigate(key); };
        NavigationItems.Children.Add(button);
    }

    public void Navigate(string key)
    {
        if (key is "settings" or "more") { OpenSettings(); return; }
        if (key == "enter-game") { SetAppearance(ShellAppearance.Game); return; }
        if (key == "exit-game") { ExitGame(); return; }
        if (!model.Modes.Navigate(key)) return;
        CloseSettings();
        ShowCurrentPage();
        model.SaveRuntime();
    }
    private void ShowCurrentPage()
    {
        var key = CurrentPage;
        if (key != "settings") StopSensorCapture();
        HomePage.Visibility = key == "home" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = key == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        GamePage.Visibility = key == "game" ? Visibility.Visible : Visibility.Collapsed;
        GameGrid.Visibility = key == "game" && !SystemParameters.HighContrast ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.Visibility = key == "home" ? Visibility.Collapsed : Visibility.Visible;
        updatingNavigation = true;
        foreach (var button in NavigationItems.Children.OfType<RadioButton>())
            button.IsChecked = (string)button.Tag == (SettingsOpen ? "settings" : key);
        updatingNavigation = false;
        ContentScroll.ScrollToTop();
        UpdateHomeActivity();
    }
    public void OpenSettings()
    {
        CloseHomeCustomize();
        if (SettingsOpen) return;
        CancelTransition();
        previousFocus = Keyboard.FocusedElement;
        settingsOpen = true;
        ShowCurrentPage();
        UpdateMonitoringVisibility();
        BackSettingsButton.Focus();
    }
    public void CloseSettings()
    {
        if (!SettingsOpen) return;
        settingsOpen = false;
        ShowCurrentPage();
        UpdateMonitoringVisibility();
        if (previousFocus is not null) Keyboard.Focus(previousFocus);
        previousFocus = null;
    }
    private void OnCloseSettings(object sender, RoutedEventArgs e) => CloseSettings();
    private void OnHomeCustomize(object sender, RoutedEventArgs e)
    {
        OpenHomeCustomize();
    }
    public Presentation.LaunchpadView Launchpad { get; }
    private void OnAnimationPreference(object sender, RoutedEventArgs e)
    {
        if (initializing) return;
        model.SaveRuntime(gameAnimation: GameAnimationToggle.IsChecked == true);
        if (!model.Settings.GameAnimation) CancelTransition();
    }
    public void SetAppearance(ShellAppearance appearance)
    {
        CloseHomeCustomize();
        if (updatingMode) return;
        var entering = appearance == ShellAppearance.Game && model.Modes.Mode != AppMode.Game;
        if (appearance == ShellAppearance.Game) Launchpad.Cancel();
        updatingMode = true;
        CancelTransition();
        CloseSettings();
        model.Modes.Switch((AppMode)appearance);
        model.Presentation.Appearance = appearance;
        theme.Apply(appearance, themeChoice);
        BuildNavigation();
        model.SaveRuntime();
        updatingMode = false;
        if (entering && IsLoaded) StartGameTransition();
    }
    public void ToggleGame() => SetAppearance(IsGame ? ShellAppearance.Normal : ShellAppearance.Game);
    public void ExitGame() { if (IsGame) SetAppearance(ShellAppearance.Normal); }
    public bool IsGame => model.Modes.Mode == AppMode.Game;
    private void UpdateMonitoringVisibility()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) StopSensorCapture();
        if (!IsVisible || WindowState == WindowState.Minimized) Launchpad?.Cancel();
        model.SetMonitoringVisible(IsVisible && !SettingsOpen && WindowState != WindowState.Minimized);
        UpdateHomeActivity();
    }
    public void StartGameTransition()
    {
        CancelTransition();
        if (!IsGame || SettingsOpen || !IsVisible || WindowState == WindowState.Minimized || !model.Settings.GameAnimation ||
            !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        GameTransition.Visibility = Visibility.Visible;
        var board = new Storyboard();
        void Add(DependencyObject target, DependencyProperty property, DoubleAnimation animation)
        {
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            board.Children.Add(animation);
        }
        Add(GameTransition, OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(550) });
        Add(ScanTransform, TranslateTransform.YProperty, new DoubleAnimation(0, Math.Max(600, ShellRoot.ActualHeight), TimeSpan.FromMilliseconds(850)));
        var slide = new TranslateTransform();
        GamePage.RenderTransform = slide;
        Add(slide, TranslateTransform.YProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(550), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Add(GamePage, OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(550) });
        transition = board;
        board.Completed += (_, _) => { if (ReferenceEquals(transition, board)) CancelTransition(); };
        board.Begin(this, true);
    }
    public void CancelTransition()
    {
        var previous = transition;
        transition = null;
        previous?.Remove(this);
        GameTransition.Visibility = Visibility.Collapsed;
        GameTransition.Opacity = 1;
        GamePage.Opacity = 1;
        GamePage.RenderTransform = Transform.Identity;
        ScanTransform.Y = 0;
    }
    private void OnSaveHotkey(object sender, RoutedEventArgs e) => ApplyHotkey(HotkeyInput.Text);
    private void OnResetHotkey(object sender, RoutedEventArgs e) { HotkeyInput.Text = "Ctrl+Alt+G"; ApplyHotkey(HotkeyInput.Text); }
    private void ApplyHotkey(string text)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture))
        { HotkeyStatus.Text = "請使用 Ctrl 或 Alt，加上字母、數字或 F1–F11，例如 Ctrl+Alt+G。"; return; }
        var canonical = gesture!.ToString();
        if (canonical != registeredHotkey && (hotkey is null || !hotkey.Set(gesture)))
        {
            HotkeyStatus.Text = "快捷鍵被佔用或無法註冊。" + (registeredHotkey is null ? "目前沒有生效的全域快捷鍵；仍可用按鈕及系統匣退出。" : "原快捷鍵 " + registeredHotkey + " 仍有效。");
            model.Status = HotkeyStatus.Text;
            return;
        }
        registeredHotkey = canonical;
        HotkeyInput.Text = canonical;
        HotkeyStatus.Text = "已啟用 " + canonical + "：手動進入／退出遊戲模式（背景也有效）。";
        model.SaveRuntime(hotkey: canonical);
        if (connection is not null) connection.Hotkey = canonical;
    }
    private void OnThemeChoice(object sender, SelectionChangedEventArgs e)
    {
        if (theme is null || ThemePicker.SelectedItem is not ComboBoxItem item) return;
        themeChoice = Enum.Parse<ThemeChoice>((string)item.Tag);
        theme.Apply(model.Presentation.Appearance, themeChoice);
        model.SaveRuntime(theme: themeChoice.ToString());
    }

    private void OnNavigationSizeChanged(object sender, SizeChangedEventArgs e)
        => NavigationBar.CornerRadius = new CornerRadius(Math.Min(e.NewSize.Width, e.NewSize.Height) / 2);
    private void OnNavigationPreference(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || NavigationSizePicker.SelectedItem is not ComboBoxItem size || NavigationPositionPicker.SelectedItem is not ComboBoxItem position) return;
        model.SaveRuntime(navigationSize: (string)size.Tag, navigationPosition: (string)position.Tag);
        ApplyResponsiveLayout(ShellRoot.ActualWidth, ShellRoot.ActualHeight);
    }
    public void ApplyResponsiveLayout(double width, double height = 800)
    {
        shellHeight = height;
        HomeCustomizeDialog.Width = Math.Max(0, Math.Min(720, width - 32));
        HomeCustomizeDialog.Height = Math.Max(0, Math.Min(820, height - 32));
        var compact = width < 720 || height < 580;
        var size = compact ? 44.0 : 64.0;
        var position = model.Settings.NavigationPosition;
        var vertical = position is "Left" or "Right";
        var scale = model.Settings.NavigationSize == "Small" ? .8 : 1;
        size = System.Math.Max(size, 44 / scale);
        NavigationBar.LayoutTransform = new ScaleTransform(scale, scale);
        NavigationItems.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        NavigationBar.HorizontalAlignment = position == "Left" ? HorizontalAlignment.Left : position == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        NavigationBar.VerticalAlignment = position == "Top" ? VerticalAlignment.Top : position == "Bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        NavigationBar.Margin = new Thickness(16);
        NavigationBar.Padding = new Thickness(compact ? 8 : 10);
        foreach (var item in NavigationItems.Children.OfType<RadioButton>())
        {
            item.Width = item.Height = size;
            var gap = compact ? 3 : 6;
            item.Margin = vertical ? new Thickness(0, gap, 0, gap) : new Thickness(gap, 0, gap, 0);
            ((TextBlock)item.Content).FontSize = compact ? 22 : 26;
        }
        var reserve = (size + NavigationBar.Padding.Top * 2) * scale + 40;
        var gutter = width < 700 ? 16 : 24;
        Frame.Margin = new Thickness(position == "Left" ? reserve : gutter, position == "Top" ? reserve : 16, position == "Right" ? reserve : gutter, position == "Bottom" ? reserve : 16);
        SizeComponents(Math.Max(0, width - Frame.Margin.Left - Frame.Margin.Right));
    }
    private void SizeComponents(double viewportWidth)
    {
        ContentArea.Width = Math.Min(1600, Math.Max(0, viewportWidth - 24 - (viewportWidth < 720 ? 32 : 64)));
        var narrow = ContentArea.Width < 820;
        foreach (var panel in SettingsPanels.Children.OfType<Border>())
            panel.Width = Math.Max(0, Math.Min(800, ContentArea.Width - 16));
        HardwareColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
        AnalysisColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AnalysisPreview, narrow ? 0 : 1);
        Grid.SetRow(AnalysisPreview, narrow ? 1 : 0);
        HardwarePreview.Margin = new Thickness(0, 0, narrow ? 0 : 28, 0);
        ApplyGameLayout(ContentArea.Width < 700 || shellHeight < 900);
    }
    public void EnterFullscreen(DisplayInfo display)
    {
        Show();
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        fullscreen = true;
        activeDisplay = display.Id;
        Place(display.X, display.Y, display.Width, display.Height);
        model.Status = "已在 " + display.Label + " 全螢幕顯示。按 Esc 返回一般視窗。";
        model.SaveSelection(display);
        Activate();
    }
    private void Place(int x, int y, int width, int height) =>
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010 | 0x0020);
    public void ReturnToWindow()
    {
        fullscreen = false;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        var primary = DisplayPolicy.Fallback(model.Displays);
        if (primary is not null)
        {
            var width = Math.Min(1200, primary.Width);
            var height = Math.Min(820, primary.Height);
            Place(primary.X + (primary.Width - width) / 2, primary.Y + (primary.Height - height) / 2, width, height);
        }
        model.Status = "一般視窗模式。選擇顯示器可再次進入全螢幕。";
    }
    private void OnTopologyChanged()
    {
        if (!fullscreen) return;
        var display = DisplayPolicy.FindSaved(model.Displays, activeDisplay);
        if (display is null)
        {
            ReturnToWindow();
            Show();
            Activate();
            model.Status = "副螢幕已斷開，已返回主螢幕。重新連接後可手動選擇。";
        }
        else Place(display.X, display.Y, display.Width, display.Height);
    }
    private void OnFullscreen(object sender, RoutedEventArgs e) { if (model.Selected is not null) EnterFullscreen(model.Selected); }
    private void OnWindowed(object sender, RoutedEventArgs e) => ReturnToWindow();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key != Key.Escape) return; if (CancelWidgetInteraction()) { } else if (HomeCustomizeOpen) CloseHomeCustomize(); else if (SettingsOpen) CloseSettings(); else if (TransitionRunning) CancelTransition(); else ReturnToWindow(); e.Handled = true; }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { DisposeGameTelemetry(); StopSensorCapture(); Launchpad.Dispose(); CancelTransition(); DisposeHome(); SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged; hotkey?.Dispose(); theme.Dispose(); model.TopologyChanged -= OnTopologyChanged; model.Dispose(); base.OnClosed(e); }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
