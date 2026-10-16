using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Secondary.Core;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    private TextBox? todoEntry;
    private Button? todoAdd, counterMinus, counterPlus;
    private StackPanel? todoList;
    private ScrollViewer? todoScroll;
    private TextBlock? progressDate;
    private readonly TextBlock[] progressLabels = new TextBlock[3];
    private readonly Grid[] progressTracks = new Grid[3];

    private bool Commit(HomeWidgetSettings next)
    {
        next = next.Normalize();
        if (SaveRequested?.Invoke(next) != true) { status.Text = "儲存失敗，請重試。"; return false; }
        Settings = next; return true;
    }
    private void RenderCounter()
    {
        body.Children.Add(new TextBlock { Text = "記錄次數 · 自動儲存", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,6,0,0) });
        readout.FontSize = 48; body.Children.Add(numberHost);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        counterMinus = ActionButton("− 1", () => ChangeCount(-1));
        counterPlus = ActionButton("＋ 1", () => ChangeCount(1));
        row.Children.Add(counterMinus); row.Children.Add(counterPlus); body.Children.Add(row);
        var reset = ActionButton("歸零", () => { if (Commit(Settings with { Count = 0 })) UpdateCounter(); });
        reset.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(reset);
        body.Children.Add(status); UpdateCounter();
    }
    private void ChangeCount(int delta)
    {
        if (Commit(Settings with { Count = Settings.Count + delta })) UpdateCounter();
    }
    private void UpdateCounter()
    {
        readout.Text = Settings.Count.ToString();
        counterMinus!.IsEnabled = Settings.Count > -999999;
        counterPlus!.IsEnabled = Settings.Count < 999999;
        status.Text = "關閉程式後仍保留數字";
    }
    private void RenderTodo()
    {
        contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        todoEntry = new TextBox { MaxLength = 120, MinHeight = 36, Padding = new Thickness(8,5,8,5) };
        todoEntry.SetResourceReference(StyleProperty, "WidgetInput");
        System.Windows.Automation.AutomationProperties.SetName(todoEntry, "新增待辦事項；Enter 新增");
        var row = new DockPanel();
        todoAdd = ActionButton("新增", AddTodo);
        todoAdd.Margin = new Thickness(6,0,0,0); todoAdd.Padding = new Thickness(10,4,10,4);
        todoEntry.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { AddTodo(); e.Handled = true; } };
        DockPanel.SetDock(todoAdd, Dock.Right); row.Children.Add(todoAdd); row.Children.Add(todoEntry); body.Children.Add(row);
        todoList = new StackPanel();
        todoScroll = new ScrollViewer { Content = todoList, Height = 146, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(todoScroll); body.Children.Add(status); UpdateTodoList();
    }
    private void AddTodo()
    {
        var tasks = Settings.Tasks ?? [];
        if (tasks.Length >= 20) { status.Text = "最多 20 項；請先移除已完成項目。"; return; }
        if (string.IsNullOrWhiteSpace(todoEntry!.Text)) { status.Text = "請先輸入待辦內容"; return; }
        if (!Commit(Settings with { Tasks = tasks.Append(new TodoItem(todoEntry.Text)).ToArray() })) return;
        todoEntry.Clear(); UpdateTodoList(); todoEntry.Focus();
    }
    private void UpdateTodoList(int? focusIndex = null)
    {
        if (todoList is null) return;
        var tasks = Settings.Tasks ?? [];
        var offset = todoScroll!.VerticalOffset;
        todoList.Children.Clear();
        for (var i = 0; i < tasks.Length; i++)
        {
            var index = i; var task = tasks[i];
            var line = new DockPanel { Margin = new Thickness(0,4,0,0) };
            var remove = ActionButton("×", () =>
            {
                if (Commit(Settings with { Tasks = (Settings.Tasks ?? []).Where((_, n) => n != index).ToArray() })) UpdateTodoList(Math.Min(index, (Settings.Tasks?.Length ?? 0) - 1));
            });
            remove.Margin = new Thickness(4,0,0,0); remove.Padding = new Thickness(6,0,6,0); remove.MinWidth = 28;
            System.Windows.Automation.AutomationProperties.SetName(remove, "移除待辦：" + task.Text);
            DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
            var text = new TextBlock { Text = (task.Done ? "✓  " : "○  ") + task.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = task.Done ? .55 : 1 };
            var check = ActionButton("", () =>
            {
                var next = (Settings.Tasks ?? []).Select((t,n) => n == index ? t with { Done = !t.Done } : t).ToArray();
                if (Commit(Settings with { Tasks = next })) UpdateTodoList(index);
            });
            check.Content = text; check.Margin = new Thickness(0); check.Padding = new Thickness(6,4,6,4);
            check.Height = double.NaN; check.HorizontalContentAlignment = HorizontalAlignment.Left;
            System.Windows.Automation.AutomationProperties.SetName(check, (task.Done ? "已完成：" : "待完成：") + task.Text);
            line.Children.Add(check); todoList.Children.Add(line);
            if (focusIndex == index) check.Focus();
        }
        if (tasks.Length == 0) todoList.Children.Add(new TextBlock { Text = "把今天想完成的事寫下來", FontSize = 13, Opacity = .6, Margin = new Thickness(4,16,0,0) });
        todoAdd!.IsEnabled = tasks.Length < 20;
        status.Text = $"已完成 {tasks.Count(t => t.Done)} / {tasks.Length} · 最多 20 項 · 自動儲存";
        todoScroll.ScrollToVerticalOffset(offset);
        if (focusIndex is < 0) todoEntry!.Focus();
        UpdateResponsiveLayout();
    }
    private void RenderProgress()
    {
        progressDate = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, Margin = new Thickness(0,4,0,12) };
        body.Children.Add(progressDate);
        for (var i = 0; i < 3; i++)
        {
            progressLabels[i] = new TextBlock { FontSize = 13, Margin = new Thickness(0,0,0,5) };
            body.Children.Add(progressLabels[i]);
            var fill = new Border { CornerRadius = new CornerRadius(3) }; fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            var track = progressTracks[i] = new Grid { Height = 6, Margin = new Thickness(0,0,0,14) };
            track.SetResourceReference(Panel.BackgroundProperty, "InsetBrush");
            track.ColumnDefinitions.Add(new ColumnDefinition()); track.ColumnDefinitions.Add(new ColumnDefinition());
            track.Children.Add(fill); body.Children.Add(track);
        }
        status.Text = "本機時間 · 週一開始 · 每分鐘更新"; body.Children.Add(status); UpdateProgress();
    }
    private void UpdateProgress()
    {
        if (progressDate is null) return;
        var now = DateTime.Now;
        progressDate.Text = now.ToString("MM 月 dd 日 · dddd");
        var weekStart = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
        var yearStart = new DateTime(now.Year, 1, 1);
        var values = new[] { ("今日", now.TimeOfDay.TotalDays), ("本週", (now - weekStart).TotalDays / 7), ("今年", (now - yearStart).TotalDays / (DateTime.IsLeapYear(now.Year) ? 366 : 365)) };
        for (var i = 0; i < values.Length; i++)
        {
            var fraction = Math.Clamp(values[i].Item2, 0, 1);
            progressLabels[i].Text = values[i].Item1 + "  " + fraction.ToString("P0");
            progressTracks[i].ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
            progressTracks[i].ColumnDefinitions[1].Width = new GridLength(1 - fraction, GridUnitType.Star);
        }
        UpdateResponsiveLayout();
    }
}
