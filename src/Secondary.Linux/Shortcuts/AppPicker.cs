using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Secondary.Linux.Platform;

namespace Secondary.Linux.Shortcuts;

// Lists installed applications with their icons; the user ticks any number to add as shortcuts.
public sealed class AppPicker : DockPanel
{
    private readonly TextBox search = new() { MinHeight = 44, PlaceholderText = "搜尋 App 名稱或說明", Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel list = new();
    private readonly AutoCompleteBox group = new() { MinHeight = 44, MaxLength = 40, MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.Contains, Margin = new Thickness(0, 6, 0, 12) };
    // Every chosen app goes into this group; an empty name means 未分組, as in the editor.
    public string Group => string.IsNullOrWhiteSpace(group.Text) ? "未分組" : group.Text.Trim();
    public void SetGroup(string name) => group.Text = name;
    private readonly List<(InstalledApp App, CheckBox Box, Control Row)> rows = new();
    public event Action? SelectionChanged;
    public IReadOnlyList<InstalledApp> Selected => rows.Where(r => r.Box.IsChecked == true).Select(r => r.App).ToArray();
    public IEnumerable<InstalledApp> Visible => rows.Where(r => r.Row.IsVisible).Select(r => r.App);
    public int Count => rows.Count;

    public AppPicker(IReadOnlyList<InstalledApp> apps, AppCatalog catalog, ISet<string> existing, IReadOnlyList<string> groups, string defaultGroup)
    {
        var groupLabel = new TextBlock { Text = "加入到分組 · 可選現有分組或輸入新名稱", Classes = { "label" }, FontSize = 13 };
        group.ItemsSource = groups; group.Text = defaultGroup;
        AutomationProperties.SetName(group, "加入到分組");
        // Opening the list on focus lets a finger pick an existing group without typing.
        group.GotFocus += (_, _) => { if (groups.Count > 0) group.IsDropDownOpen = true; };
        foreach (var control in new Control[] { groupLabel, group }) DockPanel.SetDock(control, Dock.Top);
        Children.Add(groupLabel); Children.Add(group);
        AutomationProperties.SetName(search, "搜尋 App");
        DockPanel.SetDock(search, Dock.Top);
        Children.Add(search);
        Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        foreach (var app in apps)
        {
            var icon = new Panel { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var fallback = new TextBlock { Text = "↗", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            fallback.Bind(TextBlock.ForegroundProperty, fallback.GetResourceObservable("AccentBrush"));
            icon.Children.Add(fallback);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = app.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
            var added = existing.Contains(app.DesktopFile);
            var detail = added ? "已在快捷導航" : app.Comment;
            if (!string.IsNullOrWhiteSpace(detail)) text.Children.Add(new TextBlock { Text = detail, Classes = { "label" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon); content.Children.Add(text);
            // The whole row is the touch target; already-added apps cannot be added twice.
            var box = new CheckBox { Content = content, MinHeight = 52, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = !added, Padding = new Thickness(8, 4) };
            AutomationProperties.SetName(box, app.Name);
            box.IsCheckedChanged += (_, _) => SelectionChanged?.Invoke();
            list.Children.Add(box);
            rows.Add((app, box, box));
            _ = LoadIcon(catalog, app, icon, fallback);
        }
        if (apps.Count == 0) list.Children.Add(new TextBlock { Text = "找不到已安裝的 App。", Classes = { "label" }, Margin = new Thickness(4, 16) });
        search.TextChanged += (_, _) => Filter(search.Text ?? "");
    }
    private static async System.Threading.Tasks.Task LoadIcon(AppCatalog catalog, InstalledApp app, Panel host, TextBlock fallback)
    {
        var bitmap = await catalog.LoadIconAsync(catalog.IconPath(app.Icon), 72);
        if (bitmap is null) return;
        Dispatcher.UIThread.Post(() => { host.Children.Remove(fallback); host.Children.Add(new Image { Source = bitmap, Width = 36, Height = 36 }); });
    }
    public void Filter(string text)
    {
        var query = text.Trim();
        foreach (var (app, _, row) in rows)
            row.IsVisible = query.Length == 0 || app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || (app.Comment?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) || app.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
    public void Toggle(string id, bool value) { foreach (var row in rows.Where(r => r.App.Id == id && r.Box.IsEnabled)) row.Box.IsChecked = value; }
    public void FocusSearch() => search.Focus();
}
