using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Secondary.Core;
using Secondary.Linux.Platform;

namespace Secondary.Linux;

public partial class MainWindow : Window, Shortcuts.ILaunchpadHost
{
    private static class Icons
    {
        public const string Home = "M4,11 L12,4 L20,11 L20,20 L14,20 L14,14 L10,14 L10,20 L4,20 Z";
        public const string Shortcuts = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z";
        public const string Game = "M7,7 H17 A5,5 0 0 1 22,12 V14 A4,4 0 0 1 15.5,17.5 L14,15.5 H10 L8.5,17.5 A4,4 0 0 1 2,14 V12 A5,5 0 0 1 7,7 Z";
        public const string Settings = "M3,6 H21 V8 H3 Z M3,11 H21 V13 H3 Z M3,16 H21 V18 H3 Z M7,4.5 A2.5,2.5 0 1 1 7,9.5 A2.5,2.5 0 1 1 7,4.5 Z M16,9.5 A2.5,2.5 0 1 1 16,14.5 A2.5,2.5 0 1 1 16,9.5 Z M10,14.5 A2.5,2.5 0 1 1 10,19.5 A2.5,2.5 0 1 1 10,14.5 Z";
        public const string Monitor = "M4,13 H7 V20 H4 Z M10.5,8 H13.5 V20 H10.5 Z M17,4 H20 V20 H17 Z";
        public const string Exit = "M4,4 H13 V6 H6 V18 H13 V20 H4 Z M15,8 L20,12 L15,16 V13 H9 V11 H15 Z";
    }
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-TW");
    private readonly ShellModel model;
    private ThemeChoice themeChoice;
    private bool settingsOpen, initializing = true, highContrast;
    private Func<string, Task<ShortcutResult>>? bindShortcut;
    private Func<Task>? reconnectDesktop;
    private readonly DesktopLauncher launcher;
    public bool AllowClose { get; set; }
    public bool TrayAvailable { get; private set; }
    public bool DesktopServicesKnown { get; private set; }
    public ShortcutState? ShortcutOutcome { get; private set; }
    public string HotkeyStatusText => HotkeyStatus.Text ?? "";
    public string AccessibilityText => AccessibilityStatus.Text ?? "";
    public string LauncherText => LauncherStatus.Text ?? "";
    public bool AnimationsEnabled { get; private set; } = true;
    public bool SettingsOpen => settingsOpen;
    public string CurrentPage => settingsOpen ? "settings" : model.Modes.Current;
    public bool ClockRunning => clockView.IsRunning;

    public MainWindow() : this(new ShellModel(new SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "secondary-designer.json")))) { }
    public MainWindow(ShellModel model, DesktopLauncher? launcher = null, string? shortcutsPath = null)
    {
        this.model = model;
        this.launcher = launcher ?? DesktopLauncher.FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.ProcessPath);
        InitializeComponent();
        DataContext = model;
        themeChoice = Enum.Parse<ThemeChoice>(model.Settings.Theme);
        ThemePicker.SelectedIndex = (int)themeChoice;
        NavigationSizePicker.SelectedIndex = model.Settings.NavigationSize == "Small" ? 1 : 0;
        NavigationPositionPicker.SelectedIndex = Array.IndexOf(new[] { "Bottom", "Top", "Left", "Right" }, model.Settings.NavigationPosition) is var p and >= 0 ? p : 0;
        GameAnimationToggle.IsChecked = model.Settings.GameAnimation;
        HotkeyInput.Text = model.Settings.Hotkey;
        ToggleCommand.Text = LaunchCommand.ToggleGame(Environment.GetEnvironmentVariable, Environment.ProcessPath);
        SetAccessibility(false, true);
        UpdateLauncherStatus();
        Screens.Changed += (_, _) => RefreshDisplays();
        RefreshDisplays();
        InitializeHome();
        InitializeShortcuts(shortcutsPath);
        InitializeGame();
        BuildNavigation();
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        ApplyTheme();
        ShowPage();
        initializing = false;
    }

    public void RefreshDisplays() => model.SetDisplays(DisplayCatalog.From(Screens.All));

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
        {
            UpdateHomeActivity();
            if (!IsVisible || WindowState == WindowState.Minimized) Launchpad?.Cancel();
        }
    }

    public void ApplyResponsiveLayout(double width, double height = 800)
    {
        var compact = width < 720 || height < 580;
        var size = compact ? 44.0 : 64.0;
        var position = model.Settings.NavigationPosition;
        var vertical = position is "Left" or "Right";
        var scale = model.Settings.NavigationSize == "Small" ? .8 : 1;
        // Layout rounding at fractional display scaling can shave one device pixel; keep a margin above 44 DIP.
        if (scale < 1) size = Math.Max(size, Math.Ceiling(44 / scale) + 1);
        NavigationScale.LayoutTransform = new ScaleTransform(scale, scale);
        NavigationItems.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        NavigationScale.HorizontalAlignment = position == "Left" ? HorizontalAlignment.Left : position == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        NavigationScale.VerticalAlignment = position == "Top" ? VerticalAlignment.Top : position == "Bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        NavigationBar.Padding = new Thickness(compact ? 8 : 10);
        foreach (var item in NavigationItems.Children.OfType<RadioButton>())
        {
            item.Width = item.Height = size;
            var gap = compact ? 3 : 6;
            item.Margin = vertical ? new Thickness(0, gap) : new Thickness(gap, 0);
            if (item.Content is PathIcon icon) icon.Width = icon.Height = compact ? 22 : 26;
        }
        var reserve = (size + NavigationBar.Padding.Top * 2) * scale + 40;
        var gutter = width < 700 ? 16 : 24;
        Frame.Margin = new Thickness(position == "Left" ? reserve : gutter, position == "Top" ? reserve : 16, position == "Right" ? reserve : gutter, position == "Bottom" ? reserve : 16);
        // Home is a fixed canvas, not scrolling content; it shares the same reserved edges.
        HomeLayer.Margin = Frame.Margin;
        HomeCustomizeDialog.Width = Math.Max(0, Math.Min(720, width - 32));
        HomeCustomizeDialog.Height = Math.Max(0, Math.Min(820, height - 32));
        ShortcutEditorDialog.Width = Math.Max(0, Math.Min(800, width - 32));
        var viewport = Math.Max(0, width - Frame.Margin.Left - Frame.Margin.Right);
        ContentArea.Width = Math.Min(1600, Math.Max(0, viewport - (viewport < 720 ? 32 : 64)));
        ApplyGameLayout(ContentArea.Width, height);
    }

    private void BuildNavigation()
    {
        NavigationItems.Children.Clear();
        if (model.Modes.Mode == AppMode.Game)
        {
            AddNavigation("game", "監測", Icons.Monitor);
            AddNavigation("exit", "退出遊戲模式", Icons.Exit);
        }
        else
        {
            AddNavigation("home", "首頁", Icons.Home);
            AddNavigation("shortcuts", "快捷導航", Icons.Shortcuts);
            AddNavigation("game", "遊戲模式", Icons.Game);
        }
        AddNavigation("settings", "設定", Icons.Settings);
        ApplyResponsiveLayout(Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height);
    }
    private void AddNavigation(string key, string label, string icon)
    {
        var button = new RadioButton { GroupName = "Sections", Tag = key, Content = new PathIcon { Data = Geometry.Parse(icon) } };
        button.Classes.Add("nav");
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => Navigate(key);
        NavigationItems.Children.Add(button);
    }
    public void Navigate(string key)
    {
        switch (key)
        {
            case "settings": settingsOpen = true; break;
            case "exit": ExitGame(); return;
            case "game" when model.Modes.Mode != AppMode.Game: EnterGame(); return;
            default:
                settingsOpen = false;
                model.Modes.Navigate(key);
                model.SaveRuntime();
                break;
        }
        ShowPage();
    }
    public void ToggleGame() { if (model.Modes.Mode == AppMode.Game) ExitGame(); else EnterGame(); }
    private void EnterGame() { settingsOpen = false; Launchpad.Cancel(); CloseShortcutEditor(null); CloseAppPicker(false); model.Modes.Switch(AppMode.Game); OnModeChanged(); StartGameTransition(); }
    private void ExitGame() { settingsOpen = false; model.Modes.Switch(AppMode.Normal); OnModeChanged(); }
    private void OnModeChanged()
    {
        model.SaveRuntime();
        BuildNavigation();
        ApplyTheme();
        ShowPage();
    }
    private void ShowPage()
    {
        var page = CurrentPage;
        HomeLayer.IsVisible = page == "home";
        Frame.IsVisible = page != "home";
        if (page != "home") CloseHomeCustomize();
        ShortcutsPage.IsVisible = page == "shortcuts";
        GamePage.IsVisible = page == "game";
        SettingsPage.IsVisible = page == "settings";
        if (page == "settings") _ = ConnectionCard?.SearchOnceAsync();
        if (page == "shortcuts") _ = Launchpad.RefreshRemoteAsync();
        foreach (var item in NavigationItems.Children.OfType<RadioButton>()) item.IsChecked = (string?)item.Tag == page;
        Frame.Offset = default;
        UpdateHomeActivity();
    }
    private void OnCloseSettings(object? sender, RoutedEventArgs e) => CloseSettings();
    private void CloseSettings() { settingsOpen = false; ShowPage(); }

    private void ApplyTheme()
    {
        RequestedThemeVariant = model.Modes.Mode == AppMode.Game ? ThemeVariant.Dark
            : themeChoice switch { ThemeChoice.Light => ThemeVariant.Light, ThemeChoice.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
        ApplyPalette();
    }
    private void ApplyPalette()
    {
        Palette.Apply(Resources, ActualThemeVariant == ThemeVariant.Dark, model.Modes.Mode == AppMode.Game, highContrast);
        Ambient.IsVisible = model.Modes.Mode != AppMode.Game && !highContrast;
    }
    public void SetAccessibility(bool highContrast, bool animations)
    {
        this.highContrast = highContrast;
        AnimationsEnabled = animations && !highContrast;
        AccessibilityStatus.Text = $"系統高對比：{(highContrast ? "開" : "關")} · 系統動畫：{(animations ? "開" : "關")}";
        ApplyPalette();
        UpdateHomeActivity();
    }
    private void OnThemeChoice(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing || ThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        themeChoice = Enum.Parse<ThemeChoice>(tag);
        model.SaveRuntime(theme: tag);
        ApplyTheme();
    }
    private void OnNavigationPreference(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing || NavigationSizePicker.SelectedItem is not ComboBoxItem { Tag: string size } || NavigationPositionPicker.SelectedItem is not ComboBoxItem { Tag: string position }) return;
        model.SaveRuntime(navigationSize: size, navigationPosition: position);
        ApplyResponsiveLayout(Bounds.Width, Bounds.Height);
    }
    private void OnAnimationPreference(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        model.SaveRuntime(gameAnimation: GameAnimationToggle.IsChecked == true);
        if (!model.Settings.GameAnimation) CancelTransition();
    }
    private void OnRetrySaveSettings(object? sender, RoutedEventArgs e) => model.RetrySaveSettings();

    public void SetDesktopServices(bool trayAvailable, Func<string, Task<ShortcutResult>>? bind, Func<Task>? reconnect = null)
    {
        TrayAvailable = trayAvailable;
        DesktopServicesKnown = true;
        bindShortcut = bind;
        reconnectDesktop = reconnect;
        _ = BindShortcutAsync();
    }
    private async Task BindShortcutAsync()
    {
        if (!HotkeyGesture.TryParse(model.Settings.Hotkey, out var gesture)) gesture = HotkeyGesture.TryParse("Ctrl+Alt+G", out var fallback) ? fallback : null;
        if (bindShortcut is null || gesture is null) { HotkeyStatus.Text = "找不到桌面入口服務；請使用下方指令綁定自訂快捷鍵。"; return; }
        HotkeyStatus.Text = "正在向桌面要求 " + gesture + "，請留意系統對話框。";
        var result = await bindShortcut(ShortcutTrigger.ToXdg(gesture));
        ShortcutOutcome = result.State;
        HotkeyStatus.Text = result.State switch
        {
            ShortcutState.NotAllowed when launcher.State is LauncherState.Missing or LauncherState.Elsewhere => "GNOME 需要先在下方「桌面整合」加入應用程式選單，才允許綁定全域快捷鍵；也可使用下方指令。",
            ShortcutState.Unsupported or ShortcutState.NotAllowed => result.Detail + "請使用下方指令綁定自訂快捷鍵。",
            _ => result.Detail
        };
    }
    private void OnSaveHotkey(object? sender, RoutedEventArgs e) => ApplyHotkey(HotkeyInput.Text);
    private void OnResetHotkey(object? sender, RoutedEventArgs e) { HotkeyInput.Text = "Ctrl+Alt+G"; ApplyHotkey("Ctrl+Alt+G"); }
    private void ApplyHotkey(string? text)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture)) { HotkeyStatus.Text = "格式例如 Ctrl+Alt+G，需包含 Ctrl 或 Alt，按鍵為 A–Z、0–9 或 F1–F11。"; return; }
        model.SaveRuntime(hotkey: gesture!.ToString());
        if (connection is not null) connection.Hotkey = gesture.ToString();
        _ = BindShortcutAsync();
    }
    private void UpdateLauncherStatus()
    {
        var state = launcher.State;
        LauncherStatus.Text = state switch
        {
            LauncherState.Current => "已加入應用程式選單：" + launcher.Program,
            LauncherState.Elsewhere => "選單中的 Secondary 指向其他位置；按「更新應用程式選單」改為目前的程式。",
            LauncherState.Packaged => "安裝包已加入應用程式選單，不需要另外加入。",
            LauncherState.Shadowed => "之前加入的選單項目蓋過了安裝包的項目，可能指向已刪除的檔案；按「從選單移除」改用安裝包的項目。",
            _ => "尚未加入應用程式選單。"
        };
        InstallLauncherButton.Content = state == LauncherState.Elsewhere ? "更新應用程式選單" : "加入應用程式選單";
        InstallLauncherButton.IsEnabled = state is LauncherState.Missing or LauncherState.Elsewhere;
        RemoveLauncherButton.IsEnabled = state is LauncherState.Current or LauncherState.Elsewhere or LauncherState.Shadowed;
    }
    private async void OnInstallLauncher(object? sender, RoutedEventArgs e)
    {
        try
        {
            using (var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://secondary/Assets/secondary.png"))) launcher.Install(icon);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { LauncherStatus.Text = "無法寫入應用程式選單：" + error.Message; return; }
        UpdateLauncherStatus();
        // The portal reads the application ID once per connection, so reconnect before binding again.
        if (reconnectDesktop is not null) { HotkeyStatus.Text = "已加入選單，正在重新向桌面要求快捷鍵。"; await reconnectDesktop(); }
    }
    private void OnRemoveLauncher(object? sender, RoutedEventArgs e)
    {
        try { launcher.Remove(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { LauncherStatus.Text = "無法移除：" + error.Message; return; }
        UpdateLauncherStatus();
    }
    private async void OnCopyCommand(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null || ToggleCommand.Text is not { } command) return;
        await Clipboard.SetTextAsync(command);
        HotkeyStatus.Text = "已複製指令。";
    }

    private async void OnFullscreen(object? sender, RoutedEventArgs e)
    {
        if (model.Selected is not { } display) return;
        var screens = Screens.All;
        var index = Array.FindIndex(DisplayCatalog.From(screens), d => d.Id == display.Id);
        if (index < 0) { model.Status = "找不到選擇的顯示器，請重新選擇。"; RefreshDisplays(); return; }
        var target = screens[index];
        model.SaveSelection(display);
        WindowState = WindowState.Normal;
        Position = new PixelPoint(target.WorkingArea.X + 32, target.WorkingArea.Y + 32);
        WindowState = WindowState.FullScreen;
        // The compositor may ignore the move; report where the window actually ended up.
        await Task.Delay(600);
        model.Status = Screens.ScreenFromWindow(this)?.Bounds == target.Bounds
            ? "已在 " + display.Name + " 全螢幕。Esc 返回一般視窗。"
            : "桌面未允許移動視窗。請先把視窗拖到 " + display.Name + "，再按「進入副屏全螢幕」。";
    }
    private void OnWindowed(object? sender, RoutedEventArgs e) => WindowState = WindowState.Normal;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.Escape) return;
        if (ConfirmOpen) { CloseConfirm(false); e.Handled = true; }
        else if (ShortcutEditorOpen) { CloseShortcutEditor(null); e.Handled = true; }
        else if (AppPickerOpen) { CloseAppPicker(false); e.Handled = true; }
        else if (CancelWidgetInteraction()) e.Handled = true;
        else if (HomeCustomizeOpen) { CloseHomeCustomize(); e.Handled = true; }
        else if (settingsOpen) { CloseSettings(); e.Handled = true; }
        else if (WindowState == WindowState.FullScreen) { WindowState = WindowState.Normal; e.Handled = true; }
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (AllowClose) { DisposeGameTelemetry(); DisposeHome(); Launchpad.Dispose(); return; }
        // Without a tray there is no way back to a hidden window, so minimize instead.
        e.Cancel = true;
        if (TrayAvailable) Hide(); else WindowState = WindowState.Minimized;
    }
    public ConnectionPanel? ConnectionCard { get; private set; }
    // Connection mode sits after the hotkey card; tests attach a loopback controller.
    public void AttachConnection(Secondary.Shared.Connection.ConnectionController? controller, string? unavailable = null)
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
            // The main computer uses this device's hotkey while connected, and the game page follows the connection.
            controller.Hotkey = model.Settings.Hotkey;
            controller.Changed += OnConnectionChanged;
            controller.ToggleGameRequested += OnRemoteToggle;
        }
        ConnectionCard = new ConnectionPanel(controller, ConfirmAsync, unavailable);
        var hotkey = SettingsPanels.Children.FirstOrDefault(p => p is Visual visual && HotkeyInput.GetVisualAncestors().Contains(visual));
        SettingsPanels.Children.Insert(hotkey is null ? SettingsPanels.Children.Count : SettingsPanels.Children.IndexOf(hotkey) + 1, ConnectionCard);
    }
    private void OnConnectionChanged() => Dispatcher.UIThread.Post(ConfigureGameTelemetry);
    private void OnRemoteToggle() => Dispatcher.UIThread.Post(ToggleGame);
    public void ShowInterface()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
