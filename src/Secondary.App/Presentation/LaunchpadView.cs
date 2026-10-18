using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Secondary.Core;
using Secondary.Desktop;
using Secondary.Shared;

namespace Secondary.App.Presentation;

public sealed partial class LaunchpadView : StackPanel, IDisposable
{
    private readonly ShortcutLibrary library;
    private readonly ShortcutExecutor executor = new();
    private readonly ShortcutRunner runner;
    private ShortcutEntry[] entries;
    private readonly ComboBox groups = new() { MinWidth = 140, Margin = new Thickness(0,0,8,8) };
    private readonly ScrollViewer groupViewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.HorizontalOnly };
    private readonly StackPanel groupSections = new();
    private readonly List<(WrapPanel Tiles, int Count)> horizontalTilePanels = [];
    private ScrollViewer? contentScroll;
    private readonly Button layoutToggle;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) };
    private CancellationTokenSource? execution;
    private readonly Button cancel;
    private bool filtering, reviewing, disposed;
    private Point dragStart;
    public Func<bool> CanRun { get; set; } = () => true;
    public int EntryCount => entries.Length;
    public string GroupLayout => library.GroupLayout;
    public bool IsExecuting => execution is not null;
    public bool IsReviewing => reviewing;
    public string ExecutionStatus => status.Text;
    public Task ExecutionCompletion { get; private set; } = Task.CompletedTask;
    internal void PrepareMeasurementEntries() => Save(Enumerable.Range(1, 200).Select(i =>
        new ShortcutEntry("measure-" + i, "量測入口 " + i, "量測分組 " + i % 8, Steps: [new("Open", "https://example.invalid/" + i)])).ToArray());
    public LaunchpadView(string path)
    {
        library = new(path); entries = library.Load(); runner = new(executor);
        EditDialog = (entry,mainName,main,canChoose) => ShortcutDialogs.Edit(Owner,entry,mainName,main,canChoose);
        Children.Add(new TextBlock { Text = "YOUR LAUNCHPAD", FontSize = 11 });
        Children.Add(new TextBlock { Text = "快捷導航", FontSize = 34, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,6,0,8) });
        Children.Add(new TextBlock { Text = "拖入桌面快捷方式、檔案或資料夾即可新增；拖動現有捷徑可排序。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,20) });
        var toolbar = new WrapPanel(); toolbar.Children.Add(groups);
        System.Windows.Automation.AutomationProperties.SetName(groups,"快捷方式分組");
        layoutToggle = ShortcutDialogs.Button("",ToggleGroupLayout);
        layoutToggle.Margin = new Thickness(0,0,8,8);
        System.Windows.Automation.AutomationProperties.SetName(layoutToggle,"切換分組排列方向");
        toolbar.Children.Add(layoutToggle);
        toolbar.Children.Add(ShortcutDialogs.Button("＋ 新增",Add));
        toolbar.Children.Add(ShortcutDialogs.Button("名稱排序",() => Save(entries.OrderBy(e => e.Name,StringComparer.CurrentCultureIgnoreCase).ToArray())));
        cancel = ShortcutDialogs.Button("取消執行",Cancel); cancel.IsEnabled = false;
        toolbar.Children.Add(cancel); Children.Add(toolbar);
        groupViewport.Content = groupSections; Children.Add(groupViewport); Children.Add(status);
        Loaded += (_, _) => AttachViewport();
        Unloaded += (_, _) => { if (reviewing) Cancel(); DetachViewport(); };
        IsVisibleChanged += OnVisibilityChanged;
        groupViewport.SizeChanged += (_, _) => UpdateHorizontalExtent();
        status.FontSize = 13; status.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        groups.SelectionChanged += (_, _) => { if (!filtering) RenderTiles(); };
        Background = Brushes.Transparent;
        AllowDrop = true; Drop += OnDrop; DragOver += (_, e) => HandleExternalDrag(e,false);
        Refresh(); status.Text = library.Error ?? "尚未執行任何捷徑。首次使用請按「＋ 新增」，也可拖入檔案或網址。";
    }
    private Window Owner => Window.GetWindow(this);
    private bool Save(ShortcutEntry[] next)
    {
        if (!library.Save(next)) { status.Text = "儲存失敗：" + library.Error; return false; }
        entries = next; Refresh(); status.Text = "已儲存；沒有執行任何操作。"; return true;
    }
    private void ToggleGroupLayout()
    {
        var next = library.GroupLayout == "Vertical" ? "Horizontal" : "Vertical";
        if (!library.SetGroupLayout(entries,next)) { status.Text = "排列方式儲存失敗：" + library.Error; return; }
        RenderTiles(); status.Text = "分組排列已切換並儲存。";
    }
    private void Refresh()
    {
        filtering = true; var selected = groups.SelectedIndex > 0 ? groups.SelectedItem as string : null;
        groups.Items.Clear(); groups.Items.Add("全部"); foreach (var group in entries.Select(e => e.Group).Concat(RemoteGroups).Distinct().Order()) groups.Items.Add(group);
        groups.SelectedIndex = selected is not null && groups.Items.Contains(selected) ? groups.Items.IndexOf(selected) : 0;
        filtering = false; layoutToggle.IsEnabled = !library.ReadOnly; RenderTiles();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (disposed) return;
        if (!IsVisible && reviewing) Cancel();
        if (IsVisible && remoteConnected) _ = RefreshRemoteAsync();
    }
    private void RenderTiles()
    {
        if (disposed) return;
        groupSections.Children.Clear();
        horizontalTilePanels.Clear();
        var horizontal = library.GroupLayout == "Horizontal";
        layoutToggle.Content = horizontal ? "分組：橫向  ↔" : "分組：縱向  ↕";
        groupSections.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        groupViewport.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Main-computer shortcuts share the groups, marked on their tiles.
        var selectedGroup = groups.SelectedIndex > 0 ? groups.SelectedItem as string : null;
        var visible = ShortcutPolicy.Filter(entries,"",selectedGroup).Select(e => (Entry: e, Remote: (RemoteTile?)null))
            .Concat(VisibleRemote(selectedGroup).Select(r => (r.Entry, Remote: (RemoteTile?)r))).ToArray();
        foreach (var group in visible.GroupBy(e => e.Entry.Group).OrderBy(g => g.Key,StringComparer.CurrentCultureIgnoreCase))
        {
            var section = new StackPanel { MinWidth = horizontal ? 200 : 0,
                Margin = horizontal ? new Thickness(0,0,20,0) : new Thickness(0,4,0,18), AllowDrop = true };
            var heading = new DockPanel { Margin = new Thickness(0,0,horizontal ? 0 : 12,12) };
            var count = new TextBlock { Text = group.Count() + " 個", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            DockPanel.SetDock(count,Dock.Right); heading.Children.Add(count);
            var name = new TextBlock { Text = group.Key, FontSize = 18, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis };
            heading.Children.Add(name);
            var divider = new Border { BorderThickness = new Thickness(0,0,0,1), Margin = new Thickness(0,0,horizontal ? 0 : 12,10), Child = heading };
            divider.SetResourceReference(Border.BorderBrushProperty,"LineBrush");
            section.Children.Add(divider);
            var tilePanel = new WrapPanel { Orientation = horizontal ? Orientation.Vertical : Orientation.Horizontal };
            foreach (var item in group) tilePanel.Children.Add(item.Remote is { } remoteTile ? CreateRemoteTile(remoteTile,horizontal) : CreateTile(item.Entry,horizontal));
            if (horizontal) horizontalTilePanels.Add((tilePanel,group.Count()));
            section.Children.Add(tilePanel);
            section.DragOver += (_, e) => { if (e.Data.GetDataPresent("Secondary.ShortcutId")) { e.Effects = DragDropEffects.Move; e.Handled = true; } };
            section.Drop += (_, e) =>
            {
                if (e.Data.GetData("Secondary.ShortcutId") is string id)
                { Save(ShortcutPolicy.MoveToGroup(entries,id,group.Key)); e.Handled = true; }
            };
            groupSections.Children.Add(section);
        }
        if (groupSections.Children.Count == 0) groupSections.Children.Add(new TextBlock { Text = entries.Length == 0 && RemoteGroups.Length == 0 ? "還沒有快捷方式\n新增常用程式、資料夾、網站，或建立自己的步驟式巨集。" : "沒有符合條件的快捷方式", TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(8,24,8,24) });
        Dispatcher.BeginInvoke(UpdateHorizontalExtent,DispatcherPriority.Loaded);
    }
    private void AttachViewport()
    {
        if (disposed || contentScroll is not null) return;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) { contentScroll = scroll; break; }
        if (contentScroll is not null)
        {
            contentScroll.SizeChanged += OnViewportSizeChanged;
            contentScroll.ScrollChanged += OnViewportScrollChanged;
        }
        if (contentScroll is not null) UpdateHorizontalExtent();
    }
    private void DetachViewport()
    {
        if (contentScroll is not null)
        {
            contentScroll.SizeChanged -= OnViewportSizeChanged;
            contentScroll.ScrollChanged -= OnViewportScrollChanged;
        }
        contentScroll = null;
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateHorizontalExtent();
    private void OnViewportScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0) UpdateHorizontalExtent();
    }
    private void UpdateHorizontalExtent()
    {
        if (library.GroupLayout != "Horizontal") return;
        if (contentScroll is null) AttachViewport();
        if (contentScroll is null) return;
        DependencyObject? ancestor = groupViewport;
        while (ancestor is not null && ancestor != contentScroll) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is null) { DetachViewport(); return; }
        var top = groupViewport.TransformToAncestor(contentScroll).Transform(new Point()).Y + contentScroll.VerticalOffset;
        var available = Math.Max(216,contentScroll.ViewportHeight - top - 16);
        if (double.IsNaN(available) || double.IsInfinity(available)) return;
        var rows = Math.Max(1,(int)Math.Floor((available - 54)/154));
        foreach (var (tiles,count) in horizontalTilePanels)
        {
            var height = Math.Min(count,rows)*154;
            if (double.IsNaN(tiles.Height) || Math.Abs(tiles.Height-height) > .5) tiles.Height = height;
        }
    }
    private Button CreateTile(ShortcutEntry entry, bool horizontal)
    {
        var body = new StackPanel(); var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0,0,0,12) }; icon.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush"); body.Children.Add(icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0,6,0,0), Opacity = .7 });
        var tile = new Button { Content = body, Width = 180, Height = 144,
            Margin = horizontal ? new Thickness(0,0,12,10) : new Thickness(0,0,12,12), AllowDrop = true };
        System.Windows.Automation.AutomationProperties.SetName(tile,entry.Name);
        tile.Click += async (_, _) =>
        {
            var alreadyRunning = execution is not null;
            var launch = Launch(entry);
            if (!alreadyRunning) ExecutionCompletion = launch;
            await launch;
        };
        tile.PreviewMouseLeftButtonDown += (_, e) => dragStart = e.GetPosition(tile);
        tile.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(tile)-dragStart).Length > 8) DragDrop.DoDragDrop(tile,new DataObject("Secondary.ShortcutId",entry.Id),DragDropEffects.Move); };
        tile.DragOver += (_, e) => { if (e.Data.GetDataPresent("Secondary.ShortcutId")) { e.Effects = DragDropEffects.Move; e.Handled = true; } };
        tile.Drop += (_, e) => { if (e.Data.GetData("Secondary.ShortcutId") is string id) { Save(ShortcutPolicy.MoveToGroup(entries,id,entry.Group,entry.Id)); e.Handled = true; } };
        var menu = new ContextMenu();
        foreach (var (label,action) in new (string,Action)[] {
            ("編輯",() => Edit(entry)),
            ("複製",() => Edit(entry with { Id = Guid.NewGuid().ToString("N"), Name = entry.Name + " 副本" })),
            ("往前移",() => MoveBy(entry,-1)),("往後移",() => MoveBy(entry,1)),
            ("下次執行重新確認",() => Save(entries.Select(e => e.Id == entry.Id ? e with { ApprovedFingerprint = null } : e).ToArray())),
            ("刪除",() => { if (ShortcutDialogs.Confirm(Owner,"刪除快捷方式",$"刪除「{entry.Name}」？不會刪除原始檔案。","刪除")) Save(entries.Where(e => e.Id != entry.Id).ToArray()); }) })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        tile.ContextMenu = menu; return tile;
    }
    private void MoveBy(ShortcutEntry entry, int delta)
    {
        var list = entries.ToList();
        var groupPositions = list.Select((item,index) => (item,index)).Where(pair => pair.item.Group == entry.Group)
            .Select(pair => pair.index).ToArray();
        var position = Array.FindIndex(groupPositions,index => list[index].Id == entry.Id);
        if (position < 0 || position + delta < 0 || position + delta >= groupPositions.Length) return;
        var i = groupPositions[position]; var j = groupPositions[position + delta];
        (list[i],list[j]) = (list[j],list[i]); Save(list.ToArray());
    }
    private void Edit(ShortcutEntry entry)
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        if (EditDialog(entry,null,false,false) is not { } changed) return;
        SaveLocal(changed.Entry);
    }
    private void SaveLocal(ShortcutEntry changed) =>
        Save(entries.Any(e => e.Id == changed.Id) ? entries.Select(e => e.Id == changed.Id ? changed : e).ToArray() : entries.Append(changed).ToArray());
    // While connected, a new shortcut is stored and run either here or on the main computer, chosen in the editor.
    public void Add()
    {
        var mainName = remote is { State: Secondary.Shared.Connection.LinkState.Connected } link ? link.ConnectedName ?? "主電腦" : null;
        if (mainName is null && library.ReadOnly) { status.Text = library.Error; return; }
        if (EditDialog(new(Guid.NewGuid().ToString("N"),"新快捷方式",SelectedGroup,Steps:[new()]),mainName,false,mainName is not null) is not { } result) return;
        if (result.Main) _ = SaveRemoteAsync(result.Entry);
        else if (library.ReadOnly) status.Text = library.Error;
        else SaveLocal(result.Entry);
    }
    // The editor: entry, main computer's name (null when not connected), stored on the main computer, location can be chosen.
    public Func<ShortcutEntry,string?,bool,bool,(ShortcutEntry Entry, bool Main)?> EditDialog { get; set; }
    private void OnDrop(object sender, DragEventArgs e)
    {
        HandleExternalDrag(e,true);
    }
    public void HandleExternalDrag(DragEventArgs e, bool drop)
    {
        // Internal reordering is handled by the target tile, not by file import.
        if (e.Data.GetDataPresent("Secondary.ShortcutId")) return;
        var fileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        var url = !fileDrop ? e.Data.GetData(DataFormats.UnicodeText) as string : null;
        var web = Uri.TryCreate(url?.Trim(),UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
        e.Handled = true;
        e.Effects = !library.ReadOnly && (fileDrop || web) && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        if (!drop || e.Effects == DragDropEffects.None) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            ImportFiles(files);
        }
        else if (web) ImportEntries([new(Guid.NewGuid().ToString("N"),uri!.Host,SelectedGroup,Steps:[new("Open",uri.AbsoluteUri)])],0);
    }
    private string SelectedGroup => groups.SelectedIndex > 0 ? (string)groups.SelectedItem : "日常";
    public int ImportFiles(string[] files)
    {
        if (library.ReadOnly) { status.Text = library.Error; return 0; }
        if (files.Length > 20) { status.Text = "一次最多拖入 20 個項目，這次未新增。"; return 0; }
        var candidates = new System.Collections.Generic.List<ShortcutEntry>(); var skipped = 0;
        foreach (var path in files)
            try { candidates.Add(ShortcutImport.FromPath(path,SelectedGroup)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { skipped++; }
        return ImportEntries(candidates.ToArray(),skipped);
    }
    private int ImportEntries(ShortcutEntry[] candidates, int skipped)
    {
        var next = entries.ToList(); var added = 0;
        foreach (var entry in candidates)
        {
            var step = entry.Steps![0];
            var local = HomeSettings.IsLocalPath(step.Target);
            if (next.Count >= 200 || next.Any(e => e.Steps is { Length: 1 } && e.Steps[0].Kind == step.Kind && e.Steps[0].Arguments.Length == 0 && e.Steps[0].WorkingDirectory.Length == 0 && string.Equals(e.Steps[0].Target,step.Target,local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) { skipped++; continue; }
            next.Add(entry); added++;
        }
        if (added > 0 && !Save(next.ToArray())) return 0;
                status.Text = $"已新增 {added} 個；略過 {skipped} 個（重複、不存在、不支援或已達上限）。沒有啟動任何目標；可右鍵編輯。";
        return added;
    }
    private bool MatchesCurrentExecution(ShortcutEntry snapshot, out ShortcutEntry current)
    {
        current = entries.FirstOrDefault(e => e.Id == snapshot.Id)!;
        if (current is not null && current.ApprovedFingerprint == snapshot.ApprovedFingerprint &&
            current.Steps is not null && snapshot.Steps is not null && current.Steps.SequenceEqual(snapshot.Steps)) return true;
        status.Text = "快捷方式已變更或移除，這次未執行；請重新點選並確認。";
        return false;
    }
    private async Task SetExecutionStatus(CancellationTokenSource owner, string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!disposed && ReferenceEquals(execution, owner)) status.Text = text;
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
    private async Task Launch(ShortcutEntry entry)
    {
        if (disposed || execution is not null || !CanRun()) { status.Text = "目前不能執行；請先取消正在進行的巨集或退出遊戲模式。"; return; }
        // A tile may outlive an edit made elsewhere. Resolve its current entry
        // before review and copy the immutable steps for this launch attempt.
        var currentEntry = entries.FirstOrDefault(e => e.Id == entry.Id);
        if (currentEntry is null) { status.Text = "快捷方式已移除，這次未執行。"; return; }
        entry = currentEntry with { Steps = currentEntry.Steps?.ToArray() };
        using var cancellation = new CancellationTokenSource(); execution = cancellation; cancel.IsEnabled = true; reviewing = true;
        status.Text = "正在讀取執行內容；可按「取消執行」停止。";
        try
        {
            var review = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var details = executor.Review(entry);
                cancellation.Token.ThrowIfCancellationRequested();
                var fingerprint = ShortcutApproval.Fingerprint(entry, details);
                return (Details: details, Fingerprint: fingerprint, Approved: entry.ApprovedFingerprint == fingerprint);
            }, cancellation.Token).ConfigureAwait(false);
            // Always marshal confirmation and persistence explicitly. This also
            // works for offscreen tests without a dispatcher synchronization context.
            var approvedEntry = await Dispatcher.InvokeAsync<ShortcutEntry?>(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested(); reviewing = false;
                if (disposed || !CanRun() || !MatchesCurrentExecution(entry, out var current)) return null;
                if (!review.Approved)
                {
                    if (!ShortcutDialogs.Confirm(Owner,"首次執行 · " + current.Name,"請確認以下目標與步驟，只執行你信任的內容。確認後會記住選擇；執行步驟或腳本內容變更時才會再次詢問。\n\n" + review.Details,"確認並執行")) return null;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (disposed || !CanRun() || !MatchesCurrentExecution(entry, out current)) return null;
                    var approved = current with { ApprovedFingerprint = review.Fingerprint };
                    if (!Save(entries.Select(e => e.Id == current.Id ? approved : e).ToArray())) return null;
                    current = approved;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                return current with { Steps = current.Steps?.ToArray() };
            }, DispatcherPriority.Normal, cancellation.Token).Task.ConfigureAwait(false);
            if (approvedEntry is null) return;
            // Execute also verifies the confirmed script hash while holding its
            // existing write-protected handle. That validation stays off the UI too.
            await Task.Run(() => runner.Run(approvedEntry, i =>
            {
                Dispatcher.Invoke(() =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (disposed || !CanRun()) throw new OperationCanceledException(cancellation.Token);
                    if (!disposed && ReferenceEquals(execution, cancellation))
                        status.Text = $"執行 {approvedEntry.Name} · 步驟 {i}/{approvedEntry.Steps!.Length}";
                }, DispatcherPriority.Normal, cancellation.Token);
            }, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await SetExecutionStatus(cancellation, "已完成：" + approvedEntry.Name).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await SetExecutionStatus(cancellation, "已取消；不會關閉已開啟的程式，也不能撤銷已完成的操作。").ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            AppDiagnostics.Write("shortcut-execution", e);
            await SetExecutionStatus(cancellation, "執行停止：" + e.Message).ConfigureAwait(false);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(execution, cancellation)) return;
                        execution = null; cancel.IsEnabled = false; reviewing = false;
                    }).Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
        }
    }
    public void Cancel()
    {
        try { execution?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        disposed = true; Cancel();
        IsVisibleChanged -= OnVisibilityChanged; DetachViewport(); DetachRemote();
    }
}
