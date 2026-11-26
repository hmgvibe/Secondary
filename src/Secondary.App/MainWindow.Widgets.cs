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
            var description = new TextBlock { Text = kind switch { "Calendar" => ("查看日期与月份"), "Note" => ("随手记下想法 · 本机保存"), "Countdown" => ("设置专注时间 · 1–1440 分钟"), "Todo" => ("添加、勾选完成 · 最多 20 项"), "Counter" => ("加减计数 · 自动保存"), "TimeProgress" => ("今日、本周、今年的时间进度"), "Media" => ("目前播放内容与控制"), "Weather" => ("输入城市查看即时天气"), "EventCountdown" => ("自订纪念日或目标日期"), "AiUsage" => ("Claude 与 Codex 的五小时、每周额度"), _ => ("记录经过时间 · 随时暂停") }, FontSize = 12, Margin = new Thickness(0,4,0,0) };
            description.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); header.Children.Add(description);
            var item = new MenuItem { Header = header, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = utilityWidgets.Count < 12 };
            System.Windows.Automation.AutomationProperties.SetName(item,HomeUtilityWidget.Title(kind));
            item.Click += (_, _) => AddHomeWidget(kind); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        if (utilityWidgets.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = ("选取现有组件"), Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = false });
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
        menu.Items.Add(new MenuItem { Header = ($"已加入 {utilityWidgets.Count} / 12  ·  可重复添加"), FontSize = 12, Style = (Style)FindResource("WidgetMenuItem"), IsEnabled = false });
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
        // With connection mode set up, AI usage is usually wanted from the main computer.
        if (kind == "AiUsage" && Secondary.Shared.Connection.RemoteUsageClient.Current is { Configured: true }) widget = widget with { UsageSource = "Main" };
        if (!model.SaveHome(home with { Widgets = (home.Widgets ?? []).Append(widget).ToArray() })) { HomeMediaStatus.Text = model.Status; return false; }
        AttachUtilityWidget(widget);
        utilityWidgets[widget.Id].Frame.SelectForEditing();
        if (crowded) HomeMediaStatus.Text = ("首页空间已满；新组件已放在最上层，可拖动调整或从组件清单选取。");
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
        var remove = new MenuItem { Header = ("移除") + HomeUtilityWidget.Title(settings.Kind), Style = (Style)FindResource("WidgetMenuItem") };
        remove.Click += (_, _) =>
        {
            if (ShortcutDialogs.Confirm(this, ("移除组件"), ("移除此组件？其中的数据及未保存编辑也会一并移除。"), ("移除")))
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
            foreach (var (providers, header) in new[] { ("Both", ("显示 Claude 和 Codex")), ("Claude", ("只显示 Claude")), ("Codex", ("只显示 Codex")) })
            {
                var choice = new MenuItem { Header = header, IsCheckable = true, Style = (Style)FindResource("WidgetMenuItem") };
                choice.Click += (_, _) => { content.SetUsageProviders(providers); Refresh(); };
                choices.Add((choice, providers)); menu.Items.Add(choice);
            }
            menu.Items.Add(new Separator());
            online = new MenuItem { Header = ("Claude 线上用量（非官方）"), IsCheckable = true, IsChecked = content.Settings.ClaudeOnline, Style = (Style)FindResource("WidgetMenuItem") };
            online.Click += (_, _) =>
            {
                var enable = !content.Settings.ClaudeOnline;
                if (enable && !ShortcutDialogs.Confirm(this, ("启用 Claude 线上用量"), (content.Settings.UsageSource == "Main" ? OnlineUsageMainWarning : OnlineUsageWarning), ("启用")))
                { online.IsChecked = false; return; }
                content.SetClaudeOnline(enable);
                online.IsChecked = content.Settings.ClaudeOnline;
            };
            menu.Items.Add(online);
            Refresh();
        }
        menu.Items.Add(remove); frame.ContextMenu = menu;
    }
    private const string OnlineUsageWarning =
        "Secondary 会读取 ~/.claude/.credentials.json 里 Claude Code CLI 的登录 token，最多每 5 分钟传送一次到 api.anthropic.com 的未公开用量端点，以取得五小时与每周百分比。\n\n" +
        "这个端点没有公开文件，可能随时失效或改变。token 只送往该端点，不会保存或显示。Claude 桌面版的登录不在这个文件，只用桌面版时会显示「无法取得」。";
    private const string OnlineUsageMainWarning =
        "这个组件显示主机的用量：主机的连接助手会用主机上 Claude Code CLI 的登录 token，最多每 5 分钟呼叫一次 api.anthropic.com 的未公开用量端点。token 不会传到这台设备，设备只收到百分比与重置时间。\n\n" +
        "主机也需要在连接助手菜单勾选「允许设备使用 Claude 线上用量」。这个端点没有公开文件，可能随时失效或改变。";
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
