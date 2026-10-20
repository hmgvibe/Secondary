using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Secondary.Core;

namespace Secondary.Linux.Home;

public sealed partial class UtilityWidget
{
    private TextBox? todoEntry;
    private Button? todoAdd, counterMinus, counterPlus;
    private StackPanel? todoList;
    private ScrollViewer? todoScroll;
    private TextBlock? progressDate;
    private readonly TextBlock[] progressLabels = new TextBlock[3];
    private readonly Grid[] progressTracks = new Grid[3];
    public TextBox? TodoEntry => todoEntry;
    public TextBox? NoteEditor => note;

    private bool Commit(HomeWidgetSettings next)
    {
        next = next.Normalize();
        if (SaveRequested?.Invoke(next) != true) { status.Text = "儲存失敗，請重試。"; return false; }
        Settings = next; return true;
    }

    private void RenderCounter()
    {
        var eyebrow = new TextBlock { Text = "記錄次數 · 自動儲存", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        Muted(eyebrow); body.Children.Add(eyebrow);
        readout.FontSize = 48; body.Children.Add(numberHost);
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        counterMinus = ActionButton("− 1", () => ChangeCount(-1));
        counterPlus = ActionButton("＋ 1", () => ChangeCount(1));
        AutomationProperties.SetName(counterMinus, "減 1");
        AutomationProperties.SetName(counterPlus, "加 1");
        row.Children.Add(counterMinus); row.Children.Add(counterPlus); body.Children.Add(row);
        var reset = ActionButton("歸零", () => { if (Commit(Settings with { Count = 0 })) UpdateCounter(); });
        reset.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(reset);
        body.Children.Add(status); UpdateCounter();
    }
    public void ChangeCount(int delta) { if (Commit(Settings with { Count = Settings.Count + delta })) UpdateCounter(); }
    private void UpdateCounter()
    {
        readout.Text = Settings.Count.ToString(CultureInfo.InvariantCulture);
        counterMinus!.IsEnabled = Settings.Count > -999999;
        counterPlus!.IsEnabled = Settings.Count < 999999;
        status.Text = "關閉程式後仍保留數字";
    }

    private void RenderTodo()
    {
        contentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        todoEntry = new TextBox { MaxLength = 120, MinHeight = 36, Padding = new Thickness(8, 5) };
        AutomationProperties.SetName(todoEntry, "新增待辦事項；Enter 新增");
        var row = new DockPanel();
        todoAdd = ActionButton("新增", AddTodo);
        todoAdd.Margin = new Thickness(6, 0, 0, 0); todoAdd.Padding = new Thickness(10, 4);
        todoEntry.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { AddTodo(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        DockPanel.SetDock(todoAdd, Dock.Right); row.Children.Add(todoAdd); row.Children.Add(todoEntry); body.Children.Add(row);
        todoList = new StackPanel();
        todoScroll = new ScrollViewer { Content = todoList, Height = 146, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(todoScroll); body.Children.Add(status); UpdateTodoList();
    }
    public void AddTodo()
    {
        var tasks = Settings.Tasks ?? [];
        if (tasks.Length >= 20) { status.Text = "最多 20 項；請先移除已完成項目。"; return; }
        if (string.IsNullOrWhiteSpace(todoEntry!.Text)) { status.Text = "請先輸入待辦內容"; return; }
        if (!Commit(Settings with { Tasks = tasks.Append(new TodoItem(todoEntry.Text)).ToArray() })) return;
        todoEntry.Text = ""; UpdateTodoList(); todoEntry.Focus();
    }
    public void ToggleTodo(int index)
    {
        var next = (Settings.Tasks ?? []).Select((t, n) => n == index ? t with { Done = !t.Done } : t).ToArray();
        if (Commit(Settings with { Tasks = next })) UpdateTodoList(index);
    }
    public void RemoveTodo(int index)
    {
        if (Commit(Settings with { Tasks = (Settings.Tasks ?? []).Where((_, n) => n != index).ToArray() })) UpdateTodoList(Math.Min(index, (Settings.Tasks?.Length ?? 0) - 1));
    }
    private void UpdateTodoList(int? focusIndex = null)
    {
        if (todoList is null) return;
        var tasks = Settings.Tasks ?? [];
        var offset = todoScroll!.Offset;
        todoList.Children.Clear();
        for (var i = 0; i < tasks.Length; i++)
        {
            var index = i; var task = tasks[i];
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var remove = ActionButton("×", () => RemoveTodo(index));
            remove.Margin = new Thickness(4, 0, 0, 0); remove.Padding = new Thickness(6, 0); remove.MinWidth = 28;
            AutomationProperties.SetName(remove, "移除待辦：" + task.Text);
            DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
            var text = new TextBlock { Text = (task.Done ? "✓  " : "○  ") + task.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = task.Done ? .55 : 1 };
            var check = ActionButton("", () => ToggleTodo(index));
            check.Content = text; check.Margin = new Thickness(0); check.Padding = new Thickness(6, 4);
            check.Height = double.NaN; check.HorizontalAlignment = HorizontalAlignment.Stretch; check.HorizontalContentAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(check, (task.Done ? "已完成：" : "待完成：") + task.Text);
            line.Children.Add(check); todoList.Children.Add(line);
            if (focusIndex == index) check.Focus();
        }
        if (tasks.Length == 0) todoList.Children.Add(new TextBlock { Text = "把今天想完成的事寫下來", FontSize = 13, Opacity = .6, Margin = new Thickness(4, 16, 0, 0) });
        todoAdd!.IsEnabled = tasks.Length < 20;
        status.Text = $"已完成 {tasks.Count(t => t.Done)} / {tasks.Length} · 最多 20 項 · 自動儲存";
        todoScroll.Offset = offset;
        if (focusIndex is < 0) todoEntry!.Focus();
        UpdateResponsiveLayout();
    }

    private void RenderProgress()
    {
        progressDate = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, Margin = new Thickness(0, 4, 0, 12) };
        body.Children.Add(progressDate);
        for (var i = 0; i < 3; i++)
        {
            progressLabels[i] = new TextBlock { FontSize = 13, Margin = new Thickness(0, 0, 0, 5) };
            body.Children.Add(progressLabels[i]);
            var fill = new Border { CornerRadius = new CornerRadius(3) };
            fill.Bind(BackgroundProperty, fill.GetResourceObservable("AccentBrush"));
            var track = progressTracks[i] = new Grid { Height = 6, Margin = new Thickness(0, 0, 0, 14), ColumnDefinitions = new ColumnDefinitions("*,*") };
            track.Bind(Panel.BackgroundProperty, track.GetResourceObservable("InsetBrush"));
            track.Children.Add(fill); body.Children.Add(track);
        }
        status.Text = "本機時間 · 週一開始 · 每分鐘更新"; body.Children.Add(status); UpdateProgress();
    }
    public string[] ProgressTexts => progressLabels.Select(l => l?.Text ?? "").ToArray();
    private void UpdateProgress()
    {
        if (progressDate is null) return;
        var now = DateTime.Now;
        progressDate.Text = now.ToString("MM 月 dd 日 · dddd", Chinese);
        var weekStart = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        var yearStart = new DateTime(now.Year, 1, 1);
        var values = new[] { ("今日", now.TimeOfDay.TotalDays), ("本週", (now - weekStart).TotalDays / 7), ("今年", (now - yearStart).TotalDays / (DateTime.IsLeapYear(now.Year) ? 366 : 365)) };
        for (var i = 0; i < values.Length; i++)
        {
            var fraction = Math.Clamp(values[i].Item2, 0, 1);
            progressLabels[i].Text = values[i].Item1 + "  " + fraction.ToString("P0", CultureInfo.InvariantCulture);
            progressTracks[i].ColumnDefinitions[0].Width = new GridLength(fraction, GridUnitType.Star);
            progressTracks[i].ColumnDefinitions[1].Width = new GridLength(1 - fraction, GridUnitType.Star);
        }
        UpdateResponsiveLayout();
    }

    // Dimensions come from the viewport, never ScrollViewer's unbounded measure.
    // Updating existing elements preserves editor drafts, focus and running timers.
    private void UpdateResponsiveLayout()
    {
        if (contentViewport.Bounds.Width <= 0 || contentViewport.Bounds.Height <= 0) return;
        var width = Math.Max(1, contentViewport.Bounds.Width - 24);
        var height = Math.Max(1, contentViewport.Bounds.Height - 16);
        var scale = Math.Clamp(Math.Min(width / 272, height / 232), .65, 2.4);
        status.FontSize = Math.Clamp(11 * scale, 11, 15);
        if (note is not null)
        {
            note.Height = Math.Max(40, height - 82);
            note.FontSize = Math.Clamp(13 * scale, 12, 20);
        }
        if (todoScroll is not null)
        {
            todoScroll.Height = Math.Max(36, height - 82);
            foreach (var line in todoList!.Children)
                if (line is DockPanel panel)
                    foreach (var child in panel.Children)
                        if (child is Button { Content: TextBlock text }) text.FontSize = Math.Clamp(13 * scale, 12, 20);
        }
        if (calendarGrid is not null)
        {
            calendarGrid.Height = Math.Max(96, height - 92);
            var font = Math.Clamp(Math.Min(width / 7 * .45, calendarGrid.Height / 7 * .55), 11, 30);
            foreach (var label in calendarLabels) label.FontSize = font;
            calendarHeading!.FontSize = Math.Clamp(17 * scale, 13, 25);
        }
        if (Settings.Kind is "Countdown" or "Stopwatch" or "Counter")
        {
            var reserved = Settings.Kind == "Stopwatch" ? 106 : 150;
            numberHost.Height = Math.Clamp(height - reserved, 40, 180);
            numberHost.MaxHeight = 180;
            readout.Margin = new Thickness(0);
            body.Margin = new Thickness(0, Math.Max(0, (height - reserved - numberHost.Height) / 2), 0, 0);
        }
        if (progressDate is not null)
        {
            progressDate.FontSize = Math.Clamp(17 * scale, 13, 30);
            var gap = Math.Clamp((height - 170 * scale) / 4, 4, 44);
            for (var i = 0; i < progressLabels.Length; i++)
            {
                progressLabels[i].FontSize = Math.Clamp(13 * scale, 12, 26);
                progressTracks[i].Height = Math.Clamp(6 * scale, 5, 14);
                progressTracks[i].Margin = new Thickness(0, 0, 0, gap);
            }
        }
        if (mediaHero is not null)
        {
            var cover = Math.Clamp(Math.Min(width * .30, height - 145), 44, 168);
            mediaArtwork!.Width = mediaArtwork.Height = cover;
            mediaHero.ColumnDefinitions[0].Width = new GridLength(cover + 10);
            mediaHero.MinHeight = cover;
            mediaArtworkIcon!.Width = mediaArtworkIcon.Height = Math.Clamp(cover * .45, 22, 60);
            if (mediaArtwork.Child is Image image) image.Width = image.Height = cover;
            mediaTitle!.FontSize = Math.Clamp(19 * scale, 14, 32);
            mediaArtist!.FontSize = Math.Clamp(12 * scale, 11, 19);
            mediaTitle.MaxHeight = Math.Max(42, cover * .65);
        }
        if (weatherHero is not null)
        {
            var heroHeight = Math.Clamp(height - 162, 52, 200);
            weatherHero.MinHeight = heroHeight;
            weatherHero.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(width * .26, 44, 130));
            weatherReading!.FontSize = Math.Clamp(Math.Min(width * .68 / 4.3, heroHeight * .72), 24, 92);
            weatherIcon!.FontSize = Math.Clamp(heroHeight * .70, 30, 88);
            weatherDetails!.FontSize = Math.Clamp(15 * scale, 13, 25);
            weatherMeta!.FontSize = Math.Clamp(12 * scale, 11, 18);
            weatherCity!.FontSize = Math.Clamp(15 * scale, 13, 22);
        }
        UpdateUsageLayout(scale);
        if (eventReading is not null)
        {
            eventReading.FontSize = Math.Clamp(Math.Min(width / Math.Max(3, (eventReading.Text ?? "").Length) * 1.5, (height - 66) * .45), 24, 110);
            eventReading.Margin = new Thickness(0, Math.Clamp((height - eventReading.FontSize - 60) / 3, 32, 100), 0, 6);
            eventDescription!.FontSize = Math.Clamp(13 * scale, 12, 22);
        }
    }
}
