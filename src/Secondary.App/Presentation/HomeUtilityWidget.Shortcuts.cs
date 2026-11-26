using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.App.Presentation;

// A home-page shortcut box: a small frame where shortcuts can be pinned by dragging files,
// URLs or existing launchpad tiles in, and launched with one click. Entries stay in
// shortcuts.json; this widget only stores their ids, so edits stay in sync everywhere.
public sealed partial class HomeUtilityWidget
{
    private const string ShortcutHintText = "把快捷方式、文件或网址拖进来；点击即可运行";
    private StackPanel? shortcutList;
    private TextBlock? shortcutHint;

    private void RenderShortcuts()
    {
        shortcutHint = Label(ShortcutHintText, 12);
        shortcutHint.TextWrapping = TextWrapping.Wrap;
        shortcutHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(shortcutHint);
        shortcutList = new StackPanel();
        body.Children.Add(shortcutList);
        var openLaunchpad = ActionButton("管理全部快捷方式", () => LaunchShortcutById?.Invoke(this, ManageShortcutsCommand));
        body.Children.Add(openLaunchpad);
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            var ids = e.Data.GetDataPresent("Secondary.ShortcutId");
            var files = e.Data.GetDataPresent(DataFormats.FileDrop);
            var web = !files && Uri.TryCreate(e.Data.GetData(DataFormats.UnicodeText) as string, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            e.Effects = (ids || files || web) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData("Secondary.ShortcutId") is string id) { PinShortcut(id); return; }
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { ImportShortcutFiles(files); return; }
            if (e.Data.GetData(DataFormats.UnicodeText) is string text) ImportShortcutUrl(text);
        };
        RefreshShortcuts();
    }

    public void ReloadShortcuts() { if (Settings.Kind == "Shortcuts") RefreshShortcuts(); }
    public string[] PinnedShortcutIds => Settings.ShortcutIds ?? [];
    public void PinShortcut(string id, bool remove = false) => Pin(id, remove);
    public void ImportShortcutFiles(string[] files) => Import(files);
    public void ImportShortcutUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        ImportEntries([new ShortcutEntry(Guid.NewGuid().ToString("N"), uri.Host, "日常", Steps: [new ShortcutStep("Open", uri.AbsoluteUri)])]);
    }
    private ShortcutLibrary OpenLibrary() => new(ShortcutsFilePath?.Invoke(this)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "shortcuts.json"));

    private void RefreshShortcuts()
    {
        if (shortcutList is null) return;
        shortcutList.Children.Clear();
        var library = OpenLibrary();
        var all = library.Load();
        var ids = (Settings.ShortcutIds ?? []).Where(id => all.Any(e => e.Id == id)).Take(12).ToArray();
        if (ids.Length == 0)
        {
            var empty = new TextBlock { Text = "（还没有固定的快捷方式）", FontSize = 13, TextWrapping = TextWrapping.Wrap };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            shortcutList.Children.Add(empty);
            return;
        }
        foreach (var id in ids)
        {
            var entry = all.First(e => e.Id == id);
            var button = new Button
            {
                Content = $"{entry.Icon}  {entry.Name}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 8, 10, 8),
                ToolTip = string.Join("\n", entry.Steps!.Select(s => $"{s.Kind}: {s.Target}"))
            };
            button.Click += (_, _) => LaunchShortcutById?.Invoke(this, id);
            button.ContextMenu = new ContextMenu();
            var unpin = new MenuItem { Header = "从此组件移除" };
            unpin.Click += (_, _) => Pin(id, remove: true);
            button.ContextMenu.Items.Add(unpin);
            shortcutList.Children.Add(button);
        }
    }

    private void Pin(string id, bool remove = false)
    {
        if (!remove && !OpenLibrary().Load().Any(e => e.Id == id))
        { if (shortcutHint is not null) shortcutHint.Text = "这个快捷方式已不存在。"; return; }
        var current = Settings.ShortcutIds ?? [];
        var next = remove ? current.Where(x => x != id).ToArray() : current.Append(id).Distinct().Take(12).ToArray();
        if (SaveRequested?.Invoke(Settings with { ShortcutIds = next }) == true)
        {
            Settings = Settings with { ShortcutIds = next };
            if (shortcutHint is not null) shortcutHint.Text = ShortcutHintText;
            RefreshShortcuts();
        }
        else if (shortcutHint is not null) shortcutHint.Text = "固定失败；请检查首页设置能否保存。";
    }

    private void Import(string[] files)
    {
        var candidates = new List<ShortcutEntry>();
        foreach (var path in files.Take(20))
            try { candidates.Add(ShortcutImport.FromPath(path, "日常")); } catch (Exception) { }
        ImportEntries(candidates.ToArray());
    }

    private void ImportEntries(ShortcutEntry[] entries)
    {
        if (entries.Length == 0) { if (shortcutHint is not null) shortcutHint.Text = "无法从拖入的内容建立快捷方式。"; return; }
        var library = OpenLibrary();
        var all = library.Load();
        if (library.ReadOnly) { if (shortcutHint is not null) shortcutHint.Text = library.Error ?? "快捷方式数据无法写入。"; return; }
        var added = new List<string>();
        foreach (var entry in entries)
        {
            if (all.Length + added.Count >= 200) break;
            var valid = entry with { Id = Guid.NewGuid().ToString("N") };
            all = all.Append(valid).ToArray();
            added.Add(valid.Id);
        }
        if (!library.Save(all)) { if (shortcutHint is not null) shortcutHint.Text = library.Error ?? "保存失败。"; return; }
        var ids = (Settings.ShortcutIds ?? []).Concat(added).Distinct().Take(12).ToArray();
        if (SaveRequested?.Invoke(Settings with { ShortcutIds = ids }) == true)
        {
            Settings = Settings with { ShortcutIds = ids };
            if (shortcutHint is not null) shortcutHint.Text = ShortcutHintText;
            RefreshShortcuts();
        }
    }
}
