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
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步 · 待處理", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        var tile = new Button
        {
            Content = body, Width = 180, Height = 144, Tag = new PendingTile(entry), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = horizontal ? new Thickness(0, 0, 12, 10) : new Thickness(0, 0, 12, 12)
        };
        tile.Classes.Add("tile");
        ToolTip.SetTip(tile, "這個捷徑來自另一個系統，無法在這臺設備執行。" + (MainCanRun(entry) ? "可以移到主電腦執行，或編輯、刪除。" : "請編輯成這臺設備可用的內容，或刪除。"));
        AutomationProperties.SetName(tile, entry.Name + "（待處理）");
        tile.Click += async (_, _) => await ResolvePending(entry);
        var menu = new ContextMenu();
        var toMain = new MenuItem { Header = "移到主電腦執行", IsEnabled = MainName is not null && MainCanRun(entry) };
        toMain.Click += (_, _) => _ = MovePendingToMainAsync(entry);
        menu.Items.Add(toMain);
        var change = new MenuItem { Header = "編輯", IsEnabled = Editable(entry) };
        change.Click += (_, _) => _ = EditPending(entry);
        menu.Items.Add(change);
        var remove = new MenuItem { Header = "刪除" };
        remove.Click += async (_, _) =>
        {
            if (await host.ConfirmAsync("刪除待處理的捷徑", $"刪除「{entry.Name}」？", "刪除")) SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray());
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
            if (await host.ConfirmAsync("移到主電腦", $"「{entry.Name}」無法在這臺設備執行。要移到主電腦「{mainName}」，改在主電腦執行嗎？\n\n第一次在主電腦執行時，需要在主電腦上確認。", "移到主電腦"))
                await MovePendingToMainAsync(entry);
            return;
        }
        status.Text = MainCanRun(entry)
            ? $"「{entry.Name}」無法在這臺設備執行；連線到主電腦後可以移過去執行，也可以長按或右鍵編輯、刪除。"
            : $"「{entry.Name}」無法在這臺設備執行；請長按或右鍵編輯成這臺設備可用的內容，或刪除。";
    }

    public Task<bool> MovePendingToMainAsync(ShortcutEntry entry) =>
        MoveToMainAsync(entry, () => SavePending(library.Pending.Where(e => e.Id != entry.Id).ToArray()));

    // The editor saves to this device or, while connected, to the main computer.
    private async Task EditPending(ShortcutEntry entry)
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        if (!Editable(entry)) { status.Text = "這個捷徑含有「視窗快捷鍵」步驟，Linux 版無法編輯；可以移到主電腦執行或刪除。"; return; }
        var mainName = MainName;
        (ShortcutEntry Entry, bool Main)? result = mainName is not null
            ? await host.EditPlacedShortcutAsync(entry, mainName, MainCanRun(entry), true)
            : await host.EditShortcutAsync(entry, ShortcutRules.Linux) is { } local ? (local, false) : null;
        if (result is not { } changed) return;
        var rest = library.Pending.Where(e => e.Id != entry.Id).ToArray();
        if (changed.Main) { await MoveToMainAsync(changed.Entry, () => SavePending(rest)); return; }
        if (library.Save(entries.Append(changed.Entry).ToArray(), rest)) { entries = entries.Append(changed.Entry).ToArray(); Refresh(); status.Text = "已改為這臺設備的捷徑；第一次執行時需要確認。"; }
        else status.Text = "儲存失敗：" + library.Error;
    }

    private bool SavePending(ShortcutEntry[] next)
    {
        if (!library.Save(entries, next)) { status.Text = "儲存失敗：" + library.Error; return false; }
        Refresh(); return true;
    }
}
