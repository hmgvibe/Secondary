using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Secondary.Core;
using Secondary.App.Presentation;

namespace Secondary.App;

public partial class MainWindow
{
    private readonly Dictionary<string, (HomeWidget Frame, HomeUtilityWidget Content)> utilityWidgets = new();
    public int UtilityWidgetCount => utilityWidgets.Count;
    private void OnAddWidget(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("WidgetMenu") };
        foreach (var kind in HomeWidgetSettings.Kinds)
        {
            var header = new StackPanel { MinWidth = 240 };
            header.Children.Add(new TextBlock { Text = HomeUtilityWidget.Title(kind), FontSize = 15, FontWeight = FontWeights.SemiBold });
            var description = new TextBlock { Text = kind switch { "Calendar" => "查看日期與月份", "Note" => "隨手記下想法 · 本機儲存", "Countdown" => "設定專注時間 · 1–1440 分鐘", "Todo" => "新增、勾選完成 · 最多 20 項", "Counter" => "加減計數 · 自動儲存", "TimeProgress" => "今日、本週、今年的時間進度", "Media" => "目前播放內容與控制", "Weather" => "輸入城市查看即時天氣", "EventCountdown" => "自訂紀念日或目標日期", "AiUsage" => "Claude 與 Codex 的五小時、每週額度", _ => "記錄經過時間 · 隨時暫停" }, FontSize = 12, Margin = new Thickness(0,4,0,0) };
            description.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); header.Children.Add(description);
            var item = new MenuItem { Header = header, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = utilityWidgets.Count < 12 };
            System.Windows.Automation.AutomationProperties.SetName(item,HomeUtilityWidget.Title(kind));
            item.Click += (_, _) => AddHomeWidget(kind); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        if (utilityWidgets.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "選取現有組件", Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = false });
            var ordinal = 0;
            foreach (var widget in utilityWidgets.Values)
            {
                var item = new MenuItem { Header = $"{++ordinal}. {HomeUtilityWidget.Title(widget.Content.Settings.Kind)}", Style = (Style)FindResource("WidgetMenuItem") };
                item.Click += (_, _) =>
                {
                    foreach (var other in utilityWidgets.Values) Panel.SetZIndex(other.Frame, 0);
                    Panel.SetZIndex(widget.Frame, 1); widget.Frame.SelectForEditing();
                };
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuItem { Header = $"已加入 {utilityWidgets.Count} / 12  ·  可重複新增", FontSize = 12, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    public bool AddHomeWidget(string kind)
    {
        if (!HomeWidgetSettings.Supported(kind) || utilityWidgets.Count >= 12) return false;
        var home = model.Settings.Home ?? new();
        WidgetCanvas.UpdateLayout();
        var occupied = WidgetCanvas.Children.OfType<HomeWidget>().Select(frame => new Rect(
            Math.Max(0, Canvas.GetLeft(frame)), Math.Max(0, Canvas.GetTop(frame)),
            Math.Max(0, frame.ActualWidth), Math.Max(0, frame.ActualHeight))).ToArray();
        var placement = HomeWidgetLayout.FindNewPlacement(new Size(WidgetCanvas.ActualWidth, WidgetCanvas.ActualHeight), occupied, utilityWidgets.Count, out var crowded);
        var widget = new HomeWidgetSettings(Guid.NewGuid().ToString("N"), kind, placement);
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Append(widget).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        AttachUtilityWidget(widget);
        utilityWidgets[widget.Id].Frame.SelectForEditing();
        if (crowded) HomeMediaStatus.Text = "首頁空間已滿；新組件已放在最上層，可拖動調整或從組件清單選取。";
        else HomeMediaStatus.Text = "";
        UpdateHomeActivity(); return true;
    }
    private void AttachUtilityWidget(HomeWidgetSettings settings)
    {
        var frame = new HomeWidget(); var content = new HomeUtilityWidget(settings);
        frame.Attach(WidgetCanvas,content,HomeUtilityWidget.Title(settings.Kind));
        frame.Configure(settings.Placement ?? new(),new Size(320,300));
        utilityWidgets.Add(settings.Id,(frame,content));
        Panel.SetZIndex(frame, 1);
        frame.PreviewMouseDown += (_, _) =>
        {
            foreach (var other in utilityWidgets.Values) Panel.SetZIndex(other.Frame,0);
            Panel.SetZIndex(frame,1);
        };
        frame.PlacementCommitted += placement =>
        {
            var current = (model.Settings.Home?.Widgets ?? []).FirstOrDefault(w => w.Id == settings.Id);
            if (current is not null && !SaveUtilityWidget(current with { Placement = placement }))
                frame.Configure(current.Placement ?? new(),new Size(320,300));
        };
        content.SaveRequested += next =>
        {
            var current = (model.Settings.Home?.Widgets ?? []).FirstOrDefault(w => w.Id == settings.Id);
            return current is not null && SaveUtilityWidget(next with { Placement = current.Placement });
        };
        var menu = new ContextMenu { Style = (Style)FindResource("WidgetMenu") };
        var remove = new MenuItem { Header = "移除" + HomeUtilityWidget.Title(settings.Kind), Style = (Style)FindResource("WidgetMenuItem") };
        remove.Click += (_, _) =>
        {
            if (ShortcutDialogs.Confirm(this, "移除組件", "移除此組件？其中的資料及未儲存編輯也會一併移除。", "移除"))
                RemoveHomeWidget(settings.Id);
        };
        if (settings.Kind == "AiUsage")
        {
            // Which services to show, as three exclusive choices; the online source needs Claude shown.
            var choices = new List<(MenuItem Item, string Providers)>();
            MenuItem? online = null;
            void Refresh()
            {
                foreach (var (item, providers) in choices) item.IsChecked = content.Settings.UsageProviders == providers;
                online!.IsEnabled = content.Settings.ShowsClaude();
            }
            foreach (var (providers, header) in new[] { ("Both", "顯示 Claude 和 Codex"), ("Claude", "只顯示 Claude"), ("Codex", "只顯示 Codex") })
            {
                var choice = new MenuItem { Header = header, IsCheckable = true, Style = (Style)FindResource("WidgetMenuItem") };
                choice.Click += (_, _) => { content.SetUsageProviders(providers); Refresh(); };
                choices.Add((choice, providers)); menu.Items.Add(choice);
            }
            menu.Items.Add(new Separator());
            online = new MenuItem { Header = "Claude 線上用量（非官方）", IsCheckable = true, IsChecked = content.Settings.ClaudeOnline, Style = (Style)FindResource("WidgetMenuItem") };
            online.Click += (_, _) =>
            {
                var enable = !content.Settings.ClaudeOnline;
                if (enable && !ShortcutDialogs.Confirm(this, "啟用 Claude 線上用量",
                    "Secondary 會讀取 ~/.claude/.credentials.json 裡 Claude Code CLI 的登入 token，最多每 5 分鐘傳送一次到 api.anthropic.com 的未公開用量端點，以取得五小時與每週百分比。\n\n" +
                    "這個端點沒有公開文件，可能隨時失效或改變。token 只送往該端點，不會儲存或顯示。Claude 桌面版的登入不在這個檔案，只用桌面版時會顯示「無法取得」。", "啟用"))
                { online.IsChecked = false; return; }
                content.SetClaudeOnline(enable);
                online.IsChecked = content.Settings.ClaudeOnline;
            };
            menu.Items.Add(online);
            Refresh();
        }
        menu.Items.Add(remove); frame.ContextMenu = menu;
    }
    private bool SaveUtilityWidget(HomeWidgetSettings next)
    {
        var home = model.Settings.Home ?? new();
        var saved = model.SaveHome(home with { Widgets = (home.Widgets ?? []).Select(w => w.Id == next.Id ? next : w).ToArray() });
        if (!saved) HomeMediaStatus.Text = model.Status;
        return saved;
    }
    public bool RemoveHomeWidget(string id)
    {
        if (!utilityWidgets.TryGetValue(id,out var widget)) return false;
        var home = model.Settings.Home ?? new();
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Where(w => w.Id != id).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        widget.Content.Dispose(); widget.Frame.Detach(); utilityWidgets.Remove(id); return true;
    }
    private bool CancelWidgetInteraction()
    {
        var frames = utilityWidgets.Values.Select(w => w.Frame).Append(ClockWidget).Where(w => w.IsInteracting).ToArray();
        foreach (var frame in frames) frame.CancelInteraction();
        return frames.Length > 0;
    }
}
