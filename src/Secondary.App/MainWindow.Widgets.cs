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
            var description = new TextBlock { Text = kind switch { "Calendar" => "查看日期與月份", "Note" => "隨手記下想法 · 本機儲存", "Countdown" => "設定專注時間 · 1–1440 分鐘", "Todo" => "新增、勾選完成 · 最多 20 項", "Counter" => "加減計數 · 自動儲存", "TimeProgress" => "今日、本週、今年的時間進度", "Media" => "目前播放內容與控制", "Weather" => "輸入城市查看即時天氣", "EventCountdown" => "自訂紀念日或目標日期", _ => "記錄經過時間 · 隨時暫停" }, FontSize = 12, Margin = new Thickness(0,4,0,0) };
            description.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); header.Children.Add(description);
            var item = new MenuItem { Header = header, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = utilityWidgets.Count < 12 };
            System.Windows.Automation.AutomationProperties.SetName(item,HomeUtilityWidget.Title(kind));
            item.Click += (_, _) => AddHomeWidget(kind); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = $"已加入 {utilityWidgets.Count} / 12  ·  可重複新增", FontSize = 12, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = false });
        menu.PlacementTarget = (UIElement)sender; menu.IsOpen = true;
    }
    public bool AddHomeWidget(string kind)
    {
        if (!HomeWidgetSettings.Supported(kind) || utilityWidgets.Count >= 12) return false;
        var home = model.Settings.Home ?? new();
        var index = utilityWidgets.Count;
        var widget = new HomeWidgetSettings(Guid.NewGuid().ToString("N"), kind, new WidgetPlacement(.08 + index % 3 * .36,.08 + index / 3 % 3 * .36,1));
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Append(widget).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        AttachUtilityWidget(widget); UpdateHomeActivity(); return true;
    }
    private void AttachUtilityWidget(HomeWidgetSettings settings)
    {
        var frame = new HomeWidget(); var content = new HomeUtilityWidget(settings);
        frame.Attach(WidgetCanvas,content,HomeUtilityWidget.Title(settings.Kind));
        frame.Configure(settings.Placement ?? new(),new Size(320,300));
        utilityWidgets.Add(settings.Id,(frame,content));
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
