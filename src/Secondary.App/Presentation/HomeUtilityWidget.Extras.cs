using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Secondary.Core;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    private bool Commit(HomeWidgetSettings next)
    {
        next = next.Normalize();
        if (SaveRequested?.Invoke(next) != true) { status.Text = "儲存失敗，請重試。"; return false; }
        Settings = next; return true;
    }
    private void RenderCounter()
    {
        body.Children.Clear();
        body.Children.Add(new TextBlock { Text = "記錄次數 · 自動儲存", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,12,0,0) });
        readout.FontSize = 48; readout.Text = Settings.Count.ToString(); body.Children.Add(numberHost);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var change in new[] { -1, 1 })
        {
            var button = ActionButton(change > 0 ? "＋ 1" : "− 1", () => { if (Commit(Settings with { Count = Settings.Count + change })) RenderCounter(); });
            button.IsEnabled = change > 0 ? Settings.Count < 999999 : Settings.Count > -999999;
            row.Children.Add(button);
        }
        body.Children.Add(row);
        var reset = ActionButton("歸零", () => { if (Commit(Settings with { Count = 0 })) RenderCounter(); });
        reset.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(reset);
        status.Text = "關閉程式後仍保留數字"; body.Children.Add(status);
    }
    private void RenderTodo()
    {
        body.Children.Clear();
        var tasks = Settings.Tasks ?? [];
        var entry = new TextBox { MaxLength = 120, MinHeight = 36, Padding = new Thickness(8,5,8,5) };
        entry.SetResourceReference(StyleProperty, "WidgetInput");
        System.Windows.Automation.AutomationProperties.SetName(entry, "新增待辦事項");
        var row = new DockPanel();
        var add = ActionButton("新增", () =>
        {
            if (string.IsNullOrWhiteSpace(entry.Text)) { status.Text = "請先輸入待辦內容"; return; }
            if (Commit(Settings with { Tasks = tasks.Append(new TodoItem(entry.Text)).ToArray() })) RenderTodo();
        });
        add.Margin = new Thickness(6,0,0,0); add.Padding = new Thickness(10,4,10,4); add.IsEnabled = tasks.Length < 20;
        DockPanel.SetDock(add, Dock.Right); row.Children.Add(add); row.Children.Add(entry); body.Children.Add(row);
        var list = new StackPanel();
        for (var i = 0; i < tasks.Length; i++)
        {
            var index = i; var task = tasks[i];
            var line = new DockPanel { Margin = new Thickness(0,4,0,0) };
            var remove = ActionButton("×", () => { if (Commit(Settings with { Tasks = tasks.Where((_, n) => n != index).ToArray() })) RenderTodo(); });
            remove.Margin = new Thickness(4,0,0,0); remove.Padding = new Thickness(6,0,6,0); remove.MinWidth = 28;
            System.Windows.Automation.AutomationProperties.SetName(remove, "移除待辦：" + task.Text);
            DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
            var text = new TextBlock { Text = (task.Done ? "✓  " : "○  ") + task.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = task.Done ? .55 : 1 };
            var check = ActionButton("", () =>
            {
                var next = tasks.Select((t,n) => n == index ? t with { Done = !t.Done } : t).ToArray();
                if (Commit(Settings with { Tasks = next })) RenderTodo();
            });
            check.Content = text; check.Margin = new Thickness(0); check.Padding = new Thickness(6,4,6,4);
            check.Height = double.NaN; check.HorizontalContentAlignment = HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetName(check, (task.Done ? "已完成：" : "待完成：") + task.Text);
            line.Children.Add(check); list.Children.Add(line);
        }
        if (tasks.Length == 0) list.Children.Add(new TextBlock { Text = "把今天想完成的事寫下來", FontSize = 13, Opacity = .6, Margin = new Thickness(4,16,0,0) });
        body.Children.Add(new ScrollViewer { Content = list, Height = 146, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        status.Text = $"已完成 {tasks.Count(t => t.Done)} / {tasks.Length} · 最多 20 項 · 自動儲存"; body.Children.Add(status);
    }
    private void RenderProgress()
    {
        body.Children.Clear();
        var now = DateTime.Now;
        body.Children.Add(new TextBlock { Text = now.ToString("MM 月 dd 日 · dddd"), TextWrapping = TextWrapping.Wrap, FontSize = 17, Margin = new Thickness(0,4,0,12) });
        var weekStart = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
        var yearStart = new DateTime(now.Year, 1, 1);
        foreach (var value in new[] { ("今日", now.TimeOfDay.TotalDays), ("本週", (now - weekStart).TotalDays / 7), ("今年", (now - yearStart).TotalDays / (DateTime.IsLeapYear(now.Year) ? 366 : 365)) })
        {
            var fraction = Math.Clamp(value.Item2, 0, 1);
            body.Children.Add(new TextBlock { Text = value.Item1 + "  " + fraction.ToString("P0"), FontSize = 13, Margin = new Thickness(0,0,0,5) });
            var fill = new Border { CornerRadius = new CornerRadius(3) }; fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            var track = new Grid { Height = 6, Margin = new Thickness(0,0,0,14) };
            track.SetResourceReference(Panel.BackgroundProperty, "InsetBrush");
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
            track.Children.Add(fill); body.Children.Add(track);
        }
        status.Text = "本機時間 · 週一開始 · 每分鐘更新"; body.Children.Add(status);
    }
}
