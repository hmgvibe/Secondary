using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Secondary.Core;
using Secondary.Shared.Connection;

namespace Secondary.App.Presentation;

// Pending shortcuts: imported from another system and not runnable here. They are never run on this device;
// the user moves them to the main computer, edits them into something this device can run, or deletes them.
public sealed partial class LaunchpadView
{
    public ShortcutEntry[] PendingEntries => library.Pending;
    private IEnumerable<Button> AllTiles => groupSections.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<WrapPanel>()).SelectMany(p => p.Children.OfType<Button>());
    public IEnumerable<Button> PendingTiles => AllTiles.Where(b => b.Tag is PendingTile);
    public IEnumerable<Button> LocalTiles => AllTiles.Where(b => b.Tag is null);
    private static bool MainCanRun(ShortcutEntry entry) => ShortcutPolicy.Validate(entry, ShortcutRules.Windows) is null;

    private Button CreatePendingTile(ShortcutEntry entry, bool horizontal)
    {
        var body = new StackPanel { Opacity = .65 };
        var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0,0,0,12) };
        icon.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步 · 待处理", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0,6,0,0) });
        var tile = new Button { Content = body, Width = 180, Height = 144, Margin = horizontal ? new Thickness(0,0,12,10) : new Thickness(0,0,12,12), Tag = new PendingTile(entry) };
        tile.ToolTip = "这个快捷方式来自另一个系统，无法在这台设备执行。" + (MainCanRun(entry) ? "可以移到主机执行，或编辑、删除。" : "请编辑成这台设备可用的内容，或删除。");
        System.Windows.Automation.AutomationProperties.SetName(tile, entry.Name + "（待处理）");
        tile.Click += (_, _) => ResolvePending(entry);
        var menu = new ContextMenu();
        var toMain = new MenuItem { Header = "移到主机执行", IsEnabled = MainName is not null && MainCanRun(entry) };
        toMain.Click += (_, _) => _ = MovePendingToMainAsync(entry);
        menu.Items.Add(toMain);
        foreach (var (label, action) in new (string, Action)[] {
            ("编辑", () => EditPending(entry)),
            ("删除", () => { if (ShortcutDialogs.Confirm(Owner,"删除待处理的快捷方式",$"删除「{entry.Name}」？","删除")) SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray()); }) })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        tile.ContextMenu = menu;
        return tile;
    }

    // Clicking never runs it: it offers the main computer when that can, otherwise explains the choices.
    private void ResolvePending(ShortcutEntry entry)
    {
        if (MainName is { } mainName && MainCanRun(entry))
        {
            if (ConfirmMove($"「{entry.Name}」无法在这台设备执行。要移到主机「{mainName}」，改在主机执行吗？\n\n第一次在主机执行时，需要在主机上确认。"))
                _ = MovePendingToMainAsync(entry);
            return;
        }
        status.Text = MainCanRun(entry)
            ? $"「{entry.Name}」无法在这台设备执行；连接到主机后可以移过去执行，也可以右键编辑或删除。"
            : $"「{entry.Name}」无法在这台设备执行；请右键编辑成这台设备可用的内容，或删除。";
    }
    public Func<string, bool> ConfirmMove { get; set; } = _ => false;

    public Task<bool> MovePendingToMainAsync(ShortcutEntry entry) =>
        MoveToMainAsync(entry, () => SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray()));

    // The editor saves to this device or, while connected, to the main computer.
    private void EditPending(ShortcutEntry entry)
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        var mainName = MainName;
        if (EditDialog(entry, mainName, mainName is not null && MainCanRun(entry), mainName is not null) is not { } changed) return;
        var rest = library.Pending.Where(e => e.Id != entry.Id).ToArray();
        if (changed.Main) { _ = MoveToMainAsync(changed.Entry, () => SavePending(rest)); return; }
        if (library.Save(entries.Append(changed.Entry).ToArray(), rest)) { entries = entries.Append(changed.Entry).ToArray(); Refresh(); status.Text = "已改为这台设备的快捷方式；第一次执行时需要确认。"; }
        else status.Text = "保存失败：" + library.Error;
    }

    private bool SavePending(ShortcutEntry[] next)
    {
        if (!library.Save(entries, next)) { status.Text = "保存失败：" + library.Error; return false; }
        Refresh(); return true;
    }
}

public sealed record PendingTile(ShortcutEntry Entry);
