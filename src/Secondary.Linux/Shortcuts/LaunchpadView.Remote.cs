using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Secondary.Core;
using Secondary.Shared.Connection;

namespace Secondary.Linux.Shortcuts;

// A main-computer tile; kept apart from this device's tiles so local reordering and tests never mix them.
public sealed record RemoteTile(ShortcutEntry Entry, bool Approved);

// Connection mode: the main computer's shortcuts appear in the same groups as this device's, marked "主電腦".
// They are stored and run on the Windows main computer, under its rules; the first run of each is confirmed on its screen.
public sealed partial class LaunchpadView
{
    private ConnectionController? remote;
    private RemoteShortcutList? remoteList;
    private bool remoteConnected;
    public RemoteShortcutList? RemoteShortcutList => remoteList;
    public IEnumerable<Button> RemoteTiles => groupSections.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is RemoteTile);
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
    private void OnRemoteChanged() => Dispatcher.UIThread.Post(() =>
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
            if (remoteList.Error is { } error) status.Text = "主電腦捷徑：" + error;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException)
        { remoteList = null; status.Text = "無法取得主電腦的捷徑：" + error.Message; }
        if (!disposed) Refresh();
    }

    private Button CreateRemoteTile(RemoteTile item, bool horizontal)
    {
        var entry = item.Entry;
        var body = new StackPanel();
        var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        icon.Bind(TextBlock.ForegroundProperty, icon.GetResourceObservable("AccentBrush"));
        body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步 · 主電腦" + (item.Approved ? "" : " · 需確認"), TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Opacity = .7 });
        var tile = new Button
        {
            Content = body, Width = 180, Height = 144, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = horizontal ? new Thickness(0, 0, 12, 10) : new Thickness(0, 0, 12, 12)
        };
        tile.Classes.Add("tile");
        ToolTip.SetTip(tile, item.Approved ? "在主電腦「" + remote?.ConnectedName + "」執行" : "在主電腦「" + remote?.ConnectedName + "」執行；第一次執行需要在主電腦上確認");
        AutomationProperties.SetName(tile, entry.Name + "（主電腦）");
        tile.Click += async (_, _) =>
        {
            var alreadyRunning = execution is not null;
            var launch = LaunchRemote(entry);
            if (!alreadyRunning) ExecutionCompletion = launch;
            await launch;
        };
        var menu = new ContextMenu();
        foreach (var (label, action) in new (string, Action)[]
        {
            ("編輯", () => _ = EditRemote(entry)),
            ("下次執行重新確認", () => _ = ChangeRemoteAsync(link => RemoteShortcuts.ResetApprovalAsync(link, entry.Id), "已設定：下次在主電腦執行前重新確認。")),
            ("刪除", () => _ = ConfirmDeleteRemote(entry))
        })
        {
            var menuItem = new MenuItem { Header = label };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }
        tile.ContextMenu = menu;
        return tile;
    }

    // The Linux editor has no window-key step; such shortcuts stay as they are and are edited on a Windows device.
    public async Task EditRemote(ShortcutEntry entry)
    {
        if (entry.Steps!.Any(s => s.Kind == "Keys")) { status.Text = "這個主電腦捷徑含有「視窗快捷鍵」步驟，Linux 版無法編輯；請在 Windows 設備編輯，仍可在這裡執行。"; return; }
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主電腦未連線，無法編輯主電腦的捷徑。"; return; }
        if (await host.EditPlacedShortcutAsync(entry, link.ConnectedName ?? "主電腦", true, false) is { } changed) await SaveRemoteAsync(changed.Entry);
    }
    private async Task ConfirmDeleteRemote(ShortcutEntry entry)
    {
        if (await host.ConfirmAsync("刪除主電腦捷徑", $"刪除主電腦上的「{entry.Name}」？不會刪除原始檔案。", "刪除")) await DeleteRemoteAsync(entry.Id);
    }
    public Task<bool> SaveRemoteAsync(ShortcutEntry entry) => ChangeRemoteAsync(link => RemoteShortcuts.SaveAsync(link, entry), "已儲存到主電腦；沒有執行任何操作。");
    public Task<bool> DeleteRemoteAsync(string id) => ChangeRemoteAsync(link => RemoteShortcuts.DeleteAsync(link, id), "已從主電腦刪除。");
    private async Task<bool> ChangeRemoteAsync(Func<ConnectionController, Task<string?>> change, string done)
    {
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主電腦未連線，這次沒有變更。"; return false; }
        try
        {
            var problem = await change(link);
            await RefreshRemoteAsync();
            status.Text = problem is null ? done : "主電腦拒絕：" + problem;
            return problem is null;
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException)
        { status.Text = "無法連絡主電腦：" + error.Message; return false; }
    }

    // The device only asks; the main computer reviews, asks its own user when needed, and runs the steps.
    public async Task LaunchRemote(ShortcutEntry entry)
    {
        if (disposed || execution is not null || !CanRun()) { status.Text = "目前不能執行；請先取消正在進行的巨集或退出遊戲模式。"; return; }
        if (remote is not { State: LinkState.Connected } link) { status.Text = "主電腦未連線，這次未執行。"; return; }
        using var cancellation = new CancellationTokenSource();
        execution = cancellation; cancel.IsEnabled = true;
        status.Text = $"正在請主電腦執行「{entry.Name}」…";
        string text;
        try
        {
            (_, text) = await RemoteShortcuts.RunAsync(link, entry.Id, progress => Dispatcher.UIThread.Post(() =>
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
