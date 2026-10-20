using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.App.Presentation;

// Content only: the shared HomeWidget owns movement and placement persistence.
public sealed partial class HomeUtilityWidget : Border, IDisposable
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = new();
    private readonly StackPanel body = new();
    private readonly Viewbox numberHost = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly TextBlock readout = new() { FontSize = 44, FontFamily = new FontFamily("Bahnschrift"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,8,0,12) };
    private readonly TextBlock status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,0), TextAlignment = TextAlignment.Center };
    private TextBox? note, minutes;
    private readonly Border contentViewport;
    private readonly ScrollViewer contentScroll;
    private readonly WeatherDataService weatherSource;
    private UniformGrid? calendarGrid;
    private TextBlock? calendarHeading;
    private readonly System.Collections.Generic.List<TextBlock> calendarLabels = new();
    private Button? toggle;
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime calendarDay;
    public HomeWidgetSettings Settings { get; private set; }
    public bool IsRefreshing => refresh.IsEnabled;
    public event Func<HomeWidgetSettings, bool>? SaveRequested;
    public static string Title(string kind) => kind switch { "Calendar" => "日曆", "Note" => "便箋", "Countdown" => "倒數計時", "Stopwatch" => "碼錶", "Todo" => "待辦清單", "Counter" => "計數器", "TimeProgress" => "時間進度", "Media" => "媒體", "Weather" => "天氣", "EventCountdown" => "倒數日", "AiUsage" => "AI 用量", _ => kind };
    public HomeUtilityWidget(HomeWidgetSettings settings) : this(settings, WeatherDataService.Shared) { }
    internal HomeUtilityWidget(HomeWidgetSettings settings, AiUsageService usageSource) : this(settings, WeatherDataService.Shared, usageSource) { }
    internal HomeUtilityWidget(HomeWidgetSettings settings, WeatherDataService weatherSource, AiUsageService? usageSource = null)
    {
        this.weatherSource = weatherSource;
        this.usageSource = usageSource ?? AiUsageService.Shared;
        Settings = settings.Normalize();
        numberHost.Child = readout;
        contentScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var panel = contentViewport = new Border { Padding = new Thickness(12,8,12,8), Child = contentScroll };
        status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        panel.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        Child = panel;
        if (settings.Kind == "Todo") RenderTodo();
        else if (settings.Kind == "Counter") RenderCounter();
        else if (settings.Kind == "TimeProgress") RenderProgress();
        else if (settings.Kind == "Media") RenderMedia();
        else if (settings.Kind == "Weather") RenderWeather();
        else if (settings.Kind == "EventCountdown") RenderEventCountdown();
        else if (settings.Kind == "AiUsage") RenderUsage();
        else if (settings.Kind == "Calendar") RenderCalendar();
        else if (settings.Kind == "Note")
        {
            note = new TextBox { Text = Settings.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 144, MaxLength = 4000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            note.SetResourceReference(StyleProperty,"WidgetInput");
            status.Text = "僅儲存於此電腦";
            System.Windows.Automation.AutomationProperties.SetName(note, "便箋內容");
            body.Children.Add(note);
            var save = ActionButton("儲存便箋", () => SaveNote()); body.Children.Add(save); body.Children.Add(status);
            note.TextChanged += (_, _) => status.Text = "尚未儲存";
            note.PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SaveNote(); e.Handled = true; } };
        }
        else
        {
            var eyebrow = new TextBlock { Text = settings.Kind == "Countdown" ? "專注當下" : "記錄每一刻", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,6,0,0) };
            eyebrow.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); body.Children.Add(eyebrow);
            body.Children.Add(numberHost);
            if (settings.Kind == "Countdown")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(new TextBlock { Text = "分鐘", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,8,0) });
                minutes = new TextBox { Text = Settings.Minutes.ToString(CultureInfo.InvariantCulture), Width = 80, MaxLength = 4 };
                minutes.SetResourceReference(StyleProperty,"WidgetInput"); minutes.TextAlignment = TextAlignment.Center; minutes.Padding = new Thickness(8,5,8,5);
                System.Windows.Automation.AutomationProperties.SetName(minutes, "倒數分鐘，1 至 1440");
                row.Children.Add(minutes); body.Children.Add(row);
            }
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            toggle = ActionButton("開始", Toggle); actions.Children.Add(toggle);
            toggle.SetResourceReference(Control.BackgroundProperty,"SelectedBrush");
            actions.Children.Add(ActionButton("重設", () => { elapsed.Reset(); toggle.Content = "開始"; if (minutes is not null) minutes.IsEnabled = true; UpdateTime(); }));
            body.Children.Add(actions); body.Children.Add(status);
            UpdateTime();
        }
        if (eventSettingsButton is not null)
        {
            var scroll = contentScroll;
            panel.Child = null;
            var layout = new Grid();
            layout.Children.Add(scroll);
            eventSettingsButton.HorizontalAlignment = HorizontalAlignment.Right;
            eventSettingsButton.VerticalAlignment = VerticalAlignment.Top;
            eventSettingsButton.Margin = new Thickness(0, 4, 4, 0);
            Panel.SetZIndex(eventSettingsButton, 1);
            layout.Children.Add(eventSettingsButton);
            panel.Child = layout;
        }
        contentViewport.SizeChanged += (_, _) => UpdateResponsiveLayout();
        refresh.Tick += OnTick;
    }
    private static Button ActionButton(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(4,8,4,0), Padding = new Thickness(14,6,14,6), MinHeight = 36, Height = 36, FontSize = 13 };
        button.Click += (_, _) => action(); return button;
    }
    public bool SaveNote()
    {
        if (note is null) return true;
        var next = Settings with { Text = note.Text };
        if (SaveRequested?.Invoke(next) != true) { status.Text = "儲存失敗，內容仍保留，請重試。"; return false; }
        Settings = next; status.Text = "已儲存"; return true;
    }
    private void Toggle()
    {
        if (elapsed.IsRunning) { elapsed.Stop(); toggle!.Content = "繼續"; UpdateTime(); return; }
        if (Settings.Kind == "Countdown" && elapsed.Elapsed == TimeSpan.Zero)
        {
            if (!int.TryParse(minutes!.Text, out var duration) || duration is < 1 or > 1440) { status.Text = "請輸入 1–1440 分鐘。"; return; }
            var next = Settings with { Minutes = duration };
            if (SaveRequested?.Invoke(next) != true) { status.Text = "無法儲存時間，請重試。"; return; }
            Settings = next; minutes.IsEnabled = false;
        }
        if (Settings.Kind == "Countdown" && elapsed.Elapsed.TotalMinutes >= Settings.Minutes) elapsed.Reset();
        elapsed.Start(); toggle!.Content = "暫停"; UpdateTime();
    }
    private void UpdateTime()
    {
        var remaining = Settings.Kind == "Countdown" ? TimeSpan.FromMinutes(Settings.Minutes) - elapsed.Elapsed : elapsed.Elapsed;
        if (remaining < TimeSpan.Zero) { remaining = TimeSpan.Zero; elapsed.Stop(); toggle!.Content = "重新開始"; }
        readout.Text = $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        status.Text = Settings.Kind == "Countdown" && remaining == TimeSpan.Zero ? "時間到！" : "切頁繼續計時；關閉程式後重設。";
    }
    private void OnTick(object? sender, EventArgs e)
    {
        if (Settings.Kind == "TimeProgress") UpdateProgress();
        else if (Settings.Kind == "Calendar") { if (calendarDay != DateTime.Today) UpdateCalendar(); }
        else if (Settings.Kind == "EventCountdown") UpdateEventCountdown();
        else if (Settings.Kind == "Media") _ = RefreshMediaAsync();
        else if (Settings.Kind == "Weather") _ = RefreshWeatherAsync();
        else if (Settings.Kind == "AiUsage") _ = RefreshUsageAsync();
        else UpdateTime();
    }
    public void SetActive(bool active)
    {
        refresh.Stop();
        if (disposed) return;
        widgetActive = active;
        if (!active) { PauseWeatherRequests(); PauseUsageRequests(); }
        SetMediaSubscription(active && Settings.Kind == "Media");
        if (Settings.Kind == "AiUsage") SetUsageSubscription(active && MainUsage && !UsagePaused);
        refresh.Interval = Settings.Kind switch { "Media" => TimeSpan.FromSeconds(5), "Weather" or "TimeProgress" or "Calendar" or "EventCountdown" or "AiUsage" => TimeSpan.FromMinutes(1), _ => TimeSpan.FromSeconds(1) };
        if (active && Settings.Kind is not ("Note" or "Todo" or "Counter") && !UsagePaused) { OnTick(null, EventArgs.Empty); refresh.Start(); }
    }
    private void RenderCalendar()
    {
        var header = new DockPanel();
        var previous = ActionButton("‹", () => { if (month.Year > 1 || month.Month > 1) { month = month.AddMonths(-1); UpdateCalendar(); } });
        var next = ActionButton("›", () => { if (month.Year < 9999 || month.Month < 12) { month = month.AddMonths(1); UpdateCalendar(); } });
        System.Windows.Automation.AutomationProperties.SetName(previous, "上一個月");
        System.Windows.Automation.AutomationProperties.SetName(next, "下一個月");
        foreach (var button in new[] { previous, next }) { button.Width = 36; button.MinWidth = 36; button.Padding = new Thickness(0); button.Margin = new Thickness(0); button.FontSize = 20; button.Background = Brushes.Transparent; }
        DockPanel.SetDock(next, Dock.Right); header.Children.Add(next); DockPanel.SetDock(previous, Dock.Left); header.Children.Add(previous);
        calendarHeading = new TextBlock { FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(calendarHeading);
        body.Children.Add(header);
        calendarGrid = new UniformGrid { Columns = 7, Rows = 7, Height = 144, Margin = new Thickness(0,8,0,0) };
        body.Children.Add(calendarGrid);
        body.Children.Add(ActionButton("回到今天", () => { month = new(DateTime.Today.Year,DateTime.Today.Month,1); UpdateCalendar(); }));
        UpdateCalendar();
    }
    private void UpdateCalendar()
    {
        if (calendarGrid is null || calendarHeading is null) return;
        calendarDay = DateTime.Today;
        calendarHeading.Text = month.ToString("yyyy 年 M 月");
        var days = calendarGrid;
        days.Children.Clear(); calendarLabels.Clear();
        foreach (var day in new[] { "日", "一", "二", "三", "四", "五", "六" })
        {
            var label = new TextBlock { Text = day, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = .55 };
            days.Children.Add(label); calendarLabels.Add(label);
        }
        for (var i = 0; i < (int)month.DayOfWeek; i++) days.Children.Add(new TextBlock());
        for (var day = 1; day <= DateTime.DaysInMonth(month.Year, month.Month); day++)
        {
            var label = new TextBlock { Text = day.ToString(), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cell = new Border { Child = label, CornerRadius = new CornerRadius(6), Margin = new Thickness(3,0,3,0) };
            if (month.Year == calendarDay.Year && month.Month == calendarDay.Month && day == calendarDay.Day) { label.FontWeight = FontWeights.Bold; cell.SetResourceReference(Border.BackgroundProperty,"SelectedBrush"); cell.SetResourceReference(Border.BorderBrushProperty,"AccentBrush"); cell.BorderThickness = new Thickness(1); }
            days.Children.Add(cell);
            calendarLabels.Add(label);
        }
        UpdateResponsiveLayout();
    }
    public void Dispose() { disposed = true; widgetActive = false; PauseWeatherRequests(); PauseUsageRequests(); if (Settings.Kind == "AiUsage") SetUsageSubscription(false); SetMediaSubscription(false); refresh.Stop(); refresh.Tick -= OnTick; elapsed.Stop(); }
}
