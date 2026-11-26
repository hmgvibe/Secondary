using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Secondary.Core;
using Secondary.Shared.Connection;

namespace Secondary.App.Presentation;

// A main-computer tile; kept apart from this device's tiles so local reordering never touches it.
public sealed record RemoteTile(ShortcutEntry Entry, bool Approved);

// Connection mode: the main computer's shortcuts appear in the same groups as this device's, marked "主机".
// They are stored and run on the main computer; the first run of each (or after a change) is confirmed on its screen.
public sealed partial class LaunchpadView
{
    private ConnectionController? remote;
    private RemoteShortcutList? remoteList;
    private bool remoteConnected;
    public RemoteShortcutList? RemoteShortcutList => remoteList;
    public IEnumerable<Button> RemoteTiles => groupSections.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<WrapPanel>())
        .SelectMany(p => p.Children.OfType<Button>()).Where(b => b.Tag is RemoteTile);
    private bool RemoteShown => remoteList is not null && remote?.State == LinkState.Connected;
    private string[] RemoteGroups => RemoteShown ? remoteList!.Entries.Select(e => e.Group).Distinct().ToArray() : [];
    private IEnumerable<RemoteTile> VisibleRemote(string? group) => RemoteShown
        ? ShortcutPolicy.Filter(remoteList!.Entries, "", group).Select(e => new RemoteTile(e, remoteList.Approved.Contains(e.Id))) : [];

    public void AttachRemote(ConnectionController? controller)
    {
        if (remote is not null) remote.Changed -= OnRemoteChanged;
        remote = controller;
        remoteList = null; remoteConnected = false;
        if (controller is not null) controller.Changed += OnRemoteChanged;
        OnRemoteChanged();
    }
    private void OnRemoteChanged() => Dispatcher.BeginInvoke(() =>
    {
        var connected = remote?.State == LinkState.Connected;
        if (connected == remoteConnected || disposed) return;
        remoteConnected = connected;
        if (connected) _ = RefreshRemoteAsync();
        else { remoteList = null; Refresh(); }
    });

    public async Task RefreshRemoteAsync()
    {
        if (disposed) return;
        if (remote is not { State: LinkState.Connected } link) { remoteList = null; Refresh(); return; }
        try
        {
            remoteList = await RemoteShortcuts.ListAsync(link);
            if (remoteList.Error is { } error) status.Text = "主机快捷方式：" + error;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException)
        { remoteList = null; status.Text = "无法取得主机的快捷方式：" + error.Message; }
        if (!disposed) Refresh();
    }

    private Button CreateRemoteTile(RemoteTile item, bool horizontal)
    {
        var entry = item.Entry;
        var body = new StackPanel();
        var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步 · 主机" + (item.Approved ? "" : " · 需确认"), TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Opacity = .7 });
        var tile = new Button { Content = body, Width = 180, Height = 144, Margin = horizontal ? new Thickness(0, 0, 12, 10) : new Thickness(0, 0, 12, 12), Tag = item };
        tile.ToolTip = item.Approved ? "在主机「" + remote?.ConnectedName + "」执行" : "在主机「" + remote?.ConnectedName + "」执行；第一次执行需要在主机上确认";
        System.Windows.Automation.AutomationProperties.SetName(tile, entry.Name + "（主机）");
        tile.Click += async (_, _) =>
        {
            var alreadyRunning = execution is not null;
            var launch = LaunchRemote(entry);
            if (!alreadyRunning) ExecutionCompletion = launch;
            await launch;
        };
        var menu = new ContextMenu();
        foreach (var (label, action) in new (string, Action)[] {
            ("编辑", () => EditRemote(entry)),
            ("下次执行重新确认", () => _ = ChangeRemoteAsync(link => RemoteShortcuts.ResetApprovalAsync(link, entry.Id), "已设置：下次在主机执行前重新确认。")),
            ("删除", () => { if (ShortcutDialogs.Confirm(Owner, "删除主机快捷方式", $"删除主机上的「{entry.Name}」？不会删除原始文件。", "删除")) _ = DeleteRemoteAsync(entry.Id); }) })
        { var menuItem = new MenuItem { Header = label }; menuItem.Click += (_, _) => action(); menu.Items.Add(menuItem); }
        tile.ContextMenu = menu;
        return tile;
    }

    // Choosing 本机 in the editor moves the shortcut here: saved locally first, then removed from the main computer.
    public void EditRemote(ShortcutEntry entry)
    {
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主机未连接，无法编辑主机的快捷方式。"; return; }
        if (EditDialog(entry, link.ConnectedName ?? "主机", true, true) is not { } changed) return;
        if (changed.Main) { _ = SaveRemoteAsync(changed.Entry); return; }
        if (library.ReadOnly) { status.Text = library.Error; return; }
        if (!Save(entries.Append(changed.Entry with { ApprovedFingerprint = null }).ToArray())) return;
        _ = MoveDoneAsync(DeleteRemoteAsync(changed.Entry.Id), "已移到本机；第一次执行时需要确认。");
    }
    private async Task MoveDoneAsync(Task<bool> step, string done) { if (await step && !disposed) status.Text = done; }
    // Saved on the main computer first; only then is it removed here.
    private async Task<bool> MoveToMainAsync(ShortcutEntry entry, Func<bool> removeHere)
    {
        if (!await SaveRemoteAsync(entry with { ApprovedFingerprint = null })) return false;
        if (!removeHere()) return false;
        status.Text = $"已移到主机「{remote?.ConnectedName}」；第一次在那里执行时需要在主机上确认。";
        return true;
    }
    public Task<bool> SaveRemoteAsync(ShortcutEntry entry) => ChangeRemoteAsync(link => RemoteShortcuts.SaveAsync(link, entry), "已保存到主机；没有执行任何操作。");
    public Task<bool> DeleteRemoteAsync(string id) => ChangeRemoteAsync(link => RemoteShortcuts.DeleteAsync(link, id), "已从主机删除。");
    private async Task<bool> ChangeRemoteAsync(Func<ConnectionController, Task<string?>> change, string done)
    {
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主机未连接，这次没有变更。"; return false; }
        try
        {
            var problem = await change(link);
            await RefreshRemoteAsync();
            status.Text = problem is null ? done : "主机拒绝：" + problem;
            return problem is null;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException)
        { status.Text = "无法连络主机：" + error.Message; return false; }
    }

    // The device only asks; the main computer reviews, asks its own user when needed, and runs the steps.
    public async Task LaunchRemote(ShortcutEntry entry)
    {
        if (disposed || execution is not null || !CanRun()) { status.Text = "目前不能执行；请先取消正在进行的巨集。"; return; }
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主机未连接，这次未执行。"; return; }
        using var cancellation = new CancellationTokenSource();
        execution = cancellation; cancel.IsEnabled = true;
        status.Text = $"正在请主机执行「{entry.Name}」…";
        string text;
        try
        {
            (_, text) = await RemoteShortcuts.RunAsync(link, entry.Id, progress => Dispatcher.BeginInvoke(() =>
            {
                // Entering game mode or hiding the window stops the main computer too, as with local macros.
                if (!disposed && !CanRun()) Cancel();
                if (!disposed && ReferenceEquals(execution, cancellation)) status.Text = progress;
            }), cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(execution, cancellation)) { execution = null; cancel.IsEnabled = false; }
        }
        await RefreshRemoteAsync();
        if (!disposed) status.Text = text;
    }

    private void DetachRemote() { if (remote is not null) remote.Changed -= OnRemoteChanged; remote = null; }
}
