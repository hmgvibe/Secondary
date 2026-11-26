using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Secondary.Core;

namespace Secondary.Linux.Shortcuts;

public sealed record PendingTile(ShortcutEntry Entry);

// Pending shortcuts: imported from another system and not runnable here. They are never run on this device;
// the user moves them to the main computer, edits them into something this device can run, or deletes them.
public sealed partial class LaunchpadView
{
    public ShortcutEntry[] PendingEntries => library.Pending;
    public System.Collections.Generic.IEnumerable<Button> PendingTiles => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(groupSections).OfType<Button>().Where(b => b.Tag is PendingTile);
    private static bool MainCanRun(ShortcutEntry entry) => ShortcutPolicy.Validate(entry, ShortcutRules.Windows) is null;
    // The Linux editor has no window-key step, so such shortcuts can only be moved or deleted here.
    private static bool Editable(ShortcutEntry entry) => entry.Steps!.All(s => s.Kind != "Keys");

    private Button CreatePendingTile(ShortcutEntry entry, bool horizontal)
    {
        var body = new StackPanel { Opacity = .65 };
        var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        icon.Bind(TextBlock.ForegroundProperty, icon.GetResourceObservable("MutedBrush"));
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步 · 待处理", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        var tile = new Button
        {
            Content = body, Width = 180, Height = 144, Tag = new PendingTile(entry), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = horizontal ? new Thickness(0, 0, 12, 10) : new Thickness(0, 0, 12, 12)
        };
        tile.Classes.Add("tile");
        ToolTip.SetTip(tile, "这个快捷方式来自另一个系统，无法在这台设备执行。" + (MainCanRun(entry) ? "可以移到主机执行，或编辑、删除。" : "请编辑成这台设备可用的内容，或删除。"));
        AutomationProperties.SetName(tile, entry.Name + "（待处理）");
        tile.Click += async (_, _) => await ResolvePending(entry);
        var menu = new ContextMenu();
        var toMain = new MenuItem { Header = "移到主机执行", IsEnabled = MainName is not null && MainCanRun(entry) };
        toMain.Click += (_, _) => _ = MovePendingToMainAsync(entry);
        menu.Items.Add(toMain);
        var change = new MenuItem { Header = "编辑", IsEnabled = Editable(entry) };
        change.Click += (_, _) => _ = EditPending(entry);
        menu.Items.Add(change);
        var remove = new MenuItem { Header = "删除" };
        remove.Click += async (_, _) =>
        {
            if (await host.ConfirmAsync("删除待处理的快捷方式", $"删除「{entry.Name}」？", "删除")) SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray());
        };
        menu.Items.Add(remove);
        tile.ContextMenu = menu;
        return tile;
    }

    // Clicking never runs it: it offers the main computer when that can, otherwise explains the choices.
    public async Task ResolvePending(ShortcutEntry entry)
    {
        if (MainName is { } mainName && MainCanRun(entry))
        {
            if (await host.ConfirmAsync("移到主机", $"「{entry.Name}」无法在这台设备执行。要移到主机「{mainName}」，改在主机执行吗？\n\n第一次在主机执行时，需要在主机上确认。", "移到主机"))
                await MovePendingToMainAsync(entry);
            return;
        }
        status.Text = MainCanRun(entry)
            ? $"「{entry.Name}」无法在这台设备执行；连接到主机后可以移过去执行，也可以长按或右键编辑、删除。"
            : $"「{entry.Name}」无法在这台设备执行；请长按或右键编辑成这台设备可用的内容，或删除。";
    }

    public Task<bool> MovePendingToMainAsync(ShortcutEntry entry) =>
        MoveToMainAsync(entry, () => SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray()));

    // The editor saves to this device or, while connected, to the main computer.
    private async Task EditPending(ShortcutEntry entry)
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        if (!Editable(entry)) { status.Text = "这个快捷方式含有「窗口快捷键」步骤，Linux 版无法编辑；可以移到主机执行或删除。"; return; }
        var mainName = MainName;
        (ShortcutEntry Entry, bool Main)? result = mainName is not null
            ? await host.EditPlacedShortcutAsync(entry, mainName, MainCanRun(entry), true)
            : await host.EditShortcutAsync(entry, ShortcutRules.Linux) is { } local ? (local, false) : null;
        if (result is not { } changed) return;
        var rest = library.Pending.Where(e => e.Id != entry.Id).ToArray();
        if (changed.Main) { await MoveToMainAsync(changed.Entry, () => SavePending(rest)); return; }
        if (library.Save(entries.Append(changed.Entry).ToArray(), rest)) { entries = entries.Append(changed.Entry).ToArray(); Refresh(); status.Text = "已改为这台设备的快捷方式；第一次执行时需要确认。"; }
        else status.Text = "保存失败：" + library.Error;
    }

    private bool SavePending(ShortcutEntry[] next)
    {
        if (!library.Save(entries, next)) { status.Text = "保存失败：" + library.Error; return false; }
        Refresh(); return true;
    }
}
