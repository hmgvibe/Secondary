using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Secondary.Core;
using Secondary.Linux.Platform;
using Secondary.Shared;

namespace Secondary.Linux.Shortcuts;

// What the launchpad needs from its window: confirmation, the editor and the file picker.
public interface ILaunchpadHost
{
    Task<bool> ConfirmAsync(string title, string message, string accept);
    Task<ShortcutEntry?> EditShortcutAsync(ShortcutEntry entry, ShortcutRules rules);
    // Connection mode: mainName is the main computer's name, main says where the shortcut is stored, canChoose lets a new one pick.
    async Task<(ShortcutEntry Entry, bool Main)?> EditPlacedShortcutAsync(ShortcutEntry entry, string mainName, bool main, bool canChoose) =>
        await EditShortcutAsync(entry, main ? ShortcutRules.Windows : ShortcutRules.Linux) is { } edited ? (edited, main) : null;
    // Returns the .desktop files the user chose and the group they all go into, or null.
    Task<AppSelection?> PickAppsAsync(ISet<string> existing, IReadOnlyList<string> groups, string group);
    // Local paths for exporting or importing the launchpad configuration, or null when cancelled.
    Task<string?> PickExportPathAsync(string suggestedName) => Task.FromResult<string?>(null);
    Task<string?> PickImportPathAsync() => Task.FromResult<string?>(null);
}

public sealed record AppSelection(string[] Files, string Group);

// Behaviour and wording follow the WPF LaunchpadView; Linux rules decide what can run.
public sealed partial class LaunchpadView : StackPanel, IDisposable
{
    private static readonly DataFormat<string> ShortcutId = DataFormat.CreateInProcessFormat<string>("Secondary.ShortcutId");
    private readonly ShortcutLibrary library;
    private readonly IReviewingExecutor executor;
    private readonly ShortcutRunner runner;
    private readonly ILaunchpadHost host;
    private readonly AppCatalog catalog;
    private ShortcutEntry[] entries;
    private readonly ComboBox groups = new() { MinWidth = 140, MinHeight = 44, Margin = new Thickness(0, 0, 8, 8) };
    private readonly ScrollViewer groupViewport = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly StackPanel groupSections = new();
    private readonly List<(WrapPanel Tiles, int Count)> horizontalTilePanels = [];
    private readonly Button layoutToggle, cancel;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 13 };
    private CancellationTokenSource? execution;
    private bool filtering, reviewing, disposed;
    public Func<bool> CanRun { get; set; } = () => true;
    public Func<double> ViewportHeight { get; set; } = () => 0;
    public int EntryCount => entries.Length;
    public IReadOnlyList<ShortcutEntry> Entries => entries;
    public string GroupLayout => library.GroupLayout;
    public bool IsExecuting => execution is not null;
    public bool IsReviewing => reviewing;
    public string ExecutionStatus => status.Text ?? "";
    public Task ExecutionCompletion { get; private set; } = Task.CompletedTask;
    public IEnumerable<Button> Tiles => groupSections.GetVisualDescendants().OfType<Button>().Where(b => b.Tag is ShortcutEntry);

    public LaunchpadView(string path, ILaunchpadHost host, IReviewingExecutor? executor = null, AppCatalog? catalog = null)
    {
        this.host = host;
        this.catalog = catalog ?? AppCatalog.Current;
        library = new(path, ShortcutRules.Linux); entries = library.Load();
        this.executor = executor ?? new LinuxShortcutExecutor();
        runner = new(this.executor) { Rules = ShortcutRules.Linux };
        Children.Add(new TextBlock { Text = "YOUR LAUNCHPAD", Classes = { "eyebrow" } });
        Children.Add(new TextBlock { Text = "快捷導航", FontSize = 34, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 8) });
        Children.Add(new TextBlock { Text = "拖入檔案、資料夾、應用程式啟動器（.desktop）或網址即可新增；用滑鼠拖動現有捷徑可排序，觸控請長按捷徑使用選單。", Classes = { "label" }, Margin = new Thickness(0, 0, 0, 20) });
        var toolbar = new WrapPanel();
        AutomationProperties.SetName(groups, "快捷方式分組");
        toolbar.Children.Add(groups);
        layoutToggle = ToolButton("", ToggleGroupLayout);
        AutomationProperties.SetName(layoutToggle, "切換分組排列方向");
        toolbar.Children.Add(layoutToggle);
        toolbar.Children.Add(ToolButton("＋ 新增", () => _ = Add()));
        toolbar.Children.Add(ToolButton("＋ 選擇 App", () => _ = PickApps()));
        toolbar.Children.Add(ToolButton("名稱排序", () => Save(entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray())));
        toolbar.Children.Add(ToolButton("匯出設定", () => _ = Export()));
        toolbar.Children.Add(ToolButton("匯入設定", () => _ = Import()));
        cancel = ToolButton("取消執行", Cancel); cancel.IsEnabled = false;
        toolbar.Children.Add(cancel);
        Children.Add(toolbar);
        groupViewport.Content = groupSections;
        Children.Add(groupViewport); Children.Add(status);
        status.Bind(TextBlock.ForegroundProperty, status.GetResourceObservable("MutedBrush"));
        groups.SelectionChanged += (_, _) => { if (!filtering) RenderTiles(); };
        Background = Brushes.Transparent;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => HandleExternalDrag(e, false));
        AddHandler(DragDrop.DropEvent, (_, e) => HandleExternalDrag(e, true));
        Refresh();
        status.Text = library.Error ?? "尚未執行任何捷徑。首次使用請按「＋ 新增」，也可拖入檔案、啟動器或網址。";
    }

    private static Button ToolButton(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (_, _) => click();
        return button;
    }
    private bool Save(ShortcutEntry[] next)
    {
        if (!library.Save(next)) { status.Text = "儲存失敗：" + library.Error; return false; }
        entries = next; Refresh(); status.Text = "已儲存；沒有執行任何操作。"; return true;
    }
    private void ToggleGroupLayout()
    {
        var next = library.GroupLayout == "Vertical" ? "Horizontal" : "Vertical";
        if (!library.SetGroupLayout(entries, next)) { status.Text = "排列方式儲存失敗：" + library.Error; return; }
        RenderTiles(); status.Text = "分組排列已切換並儲存。";
    }
    private void Refresh()
    {
        filtering = true;
        var selected = groups.SelectedIndex > 0 ? groups.SelectedItem as string : null;
        var names = new List<string> { "全部" };
        names.AddRange(entries.Concat(library.Pending).Select(e => e.Group).Concat(RemoteGroups).Distinct().Order(StringComparer.CurrentCultureIgnoreCase));
        groups.ItemsSource = names;
        groups.SelectedIndex = selected is not null && names.Contains(selected) ? names.IndexOf(selected) : 0;
        filtering = false; layoutToggle.IsEnabled = !library.ReadOnly; RenderTiles();
    }
    public void SelectGroup(string? group) { groups.SelectedIndex = group is null ? 0 : Math.Max(0, ((List<string>)groups.ItemsSource!).IndexOf(group)); RenderTiles(); }

    private void RenderTiles()
    {
        if (disposed) return;
        groupSections.Children.Clear(); horizontalTilePanels.Clear();
        var horizontal = library.GroupLayout == "Horizontal";
        layoutToggle.Content = horizontal ? "分組：橫向  ↔" : "分組：縱向  ↕";
        groupSections.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        groupViewport.HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        // Main-computer and pending shortcuts share the groups, marked on their tiles.
        var selectedGroup = groups.SelectedIndex > 0 ? groups.SelectedItem as string : null;
        var visible = ShortcutPolicy.Filter(entries, "", selectedGroup).Select(e => (Entry: e, Remote: (RemoteTile?)null, Pending: false))
            .Concat(VisibleRemote(selectedGroup).Select(r => (r.Entry, Remote: (RemoteTile?)r, Pending: false)))
            .Concat(ShortcutPolicy.Filter(library.Pending, "", selectedGroup).Select(e => (Entry: e, Remote: (RemoteTile?)null, Pending: true))).ToArray();
        foreach (var group in visible.GroupBy(e => e.Entry.Group).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var section = new StackPanel { MinWidth = horizontal ? 200 : 0, Margin = horizontal ? new Thickness(0, 0, 20, 0) : new Thickness(0, 4, 0, 18), Background = Brushes.Transparent };
            DragDrop.SetAllowDrop(section, true);
            var heading = new DockPanel { Margin = new Thickness(0, 0, horizontal ? 0 : 12, 12) };
            var count = new TextBlock { Text = group.Count() + " 個", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "label" } };
            DockPanel.SetDock(count, Dock.Right); heading.Children.Add(count);
            heading.Children.Add(new TextBlock { Text = group.Key, FontSize = 18, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var divider = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, horizontal ? 0 : 12, 10), Child = heading };
            divider.Bind(Border.BorderBrushProperty, divider.GetResourceObservable("LineBrush"));
            section.Children.Add(divider);
            var tilePanel = new WrapPanel { Orientation = horizontal ? Orientation.Vertical : Orientation.Horizontal };
            foreach (var item in group) tilePanel.Children.Add(item.Remote is { } remoteTile ? CreateRemoteTile(remoteTile, horizontal)
                : item.Pending ? CreatePendingTile(item.Entry, horizontal) : CreateTile(item.Entry, horizontal));
            if (horizontal) horizontalTilePanels.Add((tilePanel, group.Count()));
            section.Children.Add(tilePanel);
            var key = group.Key;
            section.AddHandler(DragDrop.DragOverEvent, (_, e) => { if (e.DataTransfer.Contains(ShortcutId)) { e.DragEffects = DragDropEffects.Move; e.Handled = true; } });
            section.AddHandler(DragDrop.DropEvent, (_, e) => { if (e.DataTransfer.TryGetValue(ShortcutId) is { } id) { Save(ShortcutPolicy.MoveToGroup(entries, id, key)); e.Handled = true; } });
            groupSections.Children.Add(section);
        }
        if (groupSections.Children.Count == 0)
            groupSections.Children.Add(new TextBlock
            {
                Text = entries.Length == 0 && library.Pending.Length == 0 && RemoteGroups.Length == 0 ? "還沒有快捷方式\n新增常用程式、資料夾、網站，或建立自己的步驟式巨集。" : "沒有符合條件的快捷方式",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(8, 24)
            });
        UpdateHorizontalExtent();
    }
    // Horizontal groups fill the available height with as many tile rows as fit.
    public void UpdateHorizontalExtent()
    {
        if (library.GroupLayout != "Horizontal") return;
        var available = Math.Max(216, ViewportHeight() - 260);
        var rows = Math.Max(1, (int)Math.Floor((available - 54) / 154));
        foreach (var (tiles, count) in horizontalTilePanels) tiles.Height = Math.Min(count, rows) * 154;
    }

    private Button CreateTile(ShortcutEntry entry, bool horizontal)
    {
        var body = new StackPanel();
        var icon = new TextBlock { Text = entry.Icon, FontSize = 30, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        icon.Bind(TextBlock.ForegroundProperty, icon.GetResourceObservable("AccentBrush"));
        body.Children.Add(icon);
        // An application launcher shows its own icon in place of the text symbol.
        if (entry.Steps!.Length == 1 && entry.Steps[0].Kind == "Open" && entry.Steps[0].Target.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase)) _ = ShowAppIcon(entry.Steps[0].Target, body, icon);
        body.Children.Add(new TextBlock { Text = entry.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = $"{entry.Steps!.Length} 步", TextAlignment = TextAlignment.Center, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Opacity = .7 });
        var tile = new Button
        {
            Content = body, Width = 180, Height = 144, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = horizontal ? new Thickness(0, 0, 12, 10) : new Thickness(0, 0, 12, 12)
        };
        tile.Classes.Add("tile");
        DragDrop.SetAllowDrop(tile, true);
        AutomationProperties.SetName(tile, entry.Name);
        var suppressClick = false;
        tile.Click += async (_, _) =>
        {
            if (suppressClick) { suppressClick = false; return; }
            var alreadyRunning = execution is not null;
            var launch = Launch(entry);
            if (!alreadyRunning) ExecutionCompletion = launch;
            await launch;
        };
        // Mouse drags reorder; a held finger opens the menu instead, so scrolling stays a pan.
        PointerPressedEventArgs? pressed = null; Point pressPoint = default;
        var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        hold.Tick += (_, _) => { hold.Stop(); suppressClick = true; tile.ContextMenu?.Open(tile); };
        tile.AddHandler(PointerPressedEvent, (_, e) =>
        {
            pressPoint = e.GetPosition(tile);
            if (e.Pointer.Type is PointerType.Touch or PointerType.Pen) { suppressClick = false; hold.Start(); }
            else if (e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) pressed = e;
        }, RoutingStrategies.Tunnel);
        tile.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            var moved = e.GetPosition(tile) - pressPoint;
            if (Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) > 10) hold.Stop();
            if (pressed is null || Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) <= 8) return;
            var start = pressed; pressed = null;
            var data = new DataTransfer(); data.Add(DataTransferItem.Create(ShortcutId, entry.Id));
            await DragDrop.DoDragDropAsync(start, data, DragDropEffects.Move);
        }, RoutingStrategies.Tunnel);
        tile.AddHandler(PointerReleasedEvent, (_, _) => { pressed = null; hold.Stop(); }, RoutingStrategies.Tunnel);
        tile.AddHandler(DragDrop.DragOverEvent, (_, e) => { if (e.DataTransfer.Contains(ShortcutId)) { e.DragEffects = DragDropEffects.Move; e.Handled = true; } });
        tile.AddHandler(DragDrop.DropEvent, (_, e) => { if (e.DataTransfer.TryGetValue(ShortcutId) is { } id) { Save(ShortcutPolicy.MoveToGroup(entries, id, entry.Group, entry.Id)); e.Handled = true; } });
        var menu = new ContextMenu();
        foreach (var (label, action) in new (string, Action)[]
        {
            ("編輯", () => _ = Edit(entry)),
            ("複製", () => _ = Edit(entry with { Id = Guid.NewGuid().ToString("N"), Name = entry.Name + " 副本" })),
            ("往前移", () => MoveBy(entry, -1)), ("往後移", () => MoveBy(entry, 1)),
            ("下次執行重新確認", () => Save(entries.Select(e => e.Id == entry.Id ? e with { ApprovedFingerprint = null } : e).ToArray())),
            ("刪除", () => _ = Delete(entry))
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        tile.ContextMenu = menu;
        return tile;
    }
    public async Task PickApps()
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        var existing = entries.Where(e => e.Steps is { Length: 1 }).Select(e => e.Steps![0].Target).ToHashSet(StringComparer.Ordinal);
        var groupNames = entries.Select(e => e.Group).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (await host.PickAppsAsync(existing, groupNames, SelectedGroup) is { Files.Length: > 0 } chosen) ImportFiles(chosen.Files, chosen.Group);
    }
    private async Task ShowAppIcon(string desktopFile, StackPanel body, TextBlock fallback)
    {
        string? name;
        try { name = AppCatalog.DesktopEntry(File.ReadLines(desktopFile).Take(400)).GetValueOrDefault("Icon"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        var bitmap = await catalog.LoadIconAsync(catalog.IconPath(name), 96);
        if (bitmap is null || disposed) return;
        var index = body.Children.IndexOf(fallback);
        if (index < 0) return;
        body.Children[index] = new Image { Source = bitmap, Width = 44, Height = 44, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
    }
    private async Task Delete(ShortcutEntry entry)
    {
        if (await host.ConfirmAsync("刪除快捷方式", $"刪除「{entry.Name}」？不會刪除原始檔案。", "刪除")) Save(entries.Where(e => e.Id != entry.Id).ToArray());
    }
    public void MoveBy(ShortcutEntry entry, int delta)
    {
        var list = entries.ToList();
        var positions = list.Select((item, index) => (item, index)).Where(p => p.item.Group == entry.Group).Select(p => p.index).ToArray();
        var position = Array.FindIndex(positions, index => list[index].Id == entry.Id);
        if (position < 0 || position + delta < 0 || position + delta >= positions.Length) return;
        var i = positions[position]; var j = positions[position + delta];
        (list[i], list[j]) = (list[j], list[i]); Save(list.ToArray());
    }
    // While connected, editing can also move the shortcut to the main computer.
    public async Task Edit(ShortcutEntry entry)
    {
        if (library.ReadOnly) { status.Text = library.Error; return; }
        if (MainName is not { } mainName || !entries.Any(e => e.Id == entry.Id))
        {
            if (await host.EditShortcutAsync(entry, ShortcutRules.Linux) is { } changed) SaveLocal(changed);
            return;
        }
        if (await host.EditPlacedShortcutAsync(entry, mainName, false, true) is not { } result) return;
        if (!result.Main) SaveLocal(result.Entry);
        else await MoveToMainAsync(result.Entry, () => Save(entries.Where(e => e.Id != result.Entry.Id).ToArray()));
    }
    private string? MainName => remote is { State: Secondary.Shared.Connection.LinkState.Connected } link ? link.ConnectedName ?? "主電腦" : null;
    private void SaveLocal(ShortcutEntry changed) =>
        Save(entries.Any(e => e.Id == changed.Id) ? entries.Select(e => e.Id == changed.Id ? changed : e).ToArray() : entries.Append(changed).ToArray());
    // While connected, a new shortcut is stored and run either here or on the main computer, chosen in the editor.
    public async Task Add()
    {
        var entry = new ShortcutEntry(Guid.NewGuid().ToString("N"), "新快捷方式", SelectedGroup, Steps: [new()]);
        if (remote is not { State: Secondary.Shared.Connection.LinkState.Connected } link) { await Edit(entry); return; }
        if (await host.EditPlacedShortcutAsync(entry, link.ConnectedName ?? "主電腦", false, true) is not { } result) return;
        if (result.Main) await SaveRemoteAsync(result.Entry);
        else if (library.ReadOnly) status.Text = library.Error;
        else SaveLocal(result.Entry);
    }

    public void HandleExternalDrag(DragEventArgs e, bool drop)
    {
        // Internal reordering is handled by the target tile or section, not by import.
        if (e.DataTransfer.Contains(ShortcutId)) return;
        var files = e.DataTransfer.Contains(DataFormat.File);
        var text = !files ? e.DataTransfer.TryGetText()?.Trim() : null;
        var web = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        e.Handled = true;
        e.DragEffects = !library.ReadOnly && (files || web) ? DragDropEffects.Copy : DragDropEffects.None;
        if (!drop || e.DragEffects == DragDropEffects.None) return;
        if (files) ImportFiles(e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray() ?? []);
        else if (web) ImportEntries([new(Guid.NewGuid().ToString("N"), uri!.Host, SelectedGroup, Steps: [new("Open", uri.AbsoluteUri)])], 0);
    }
    private string SelectedGroup => groups.SelectedIndex > 0 ? (string)groups.SelectedItem! : "日常";
    public int ImportFiles(string[] files, string? group = null)
    {
        group = string.IsNullOrWhiteSpace(group) ? SelectedGroup : group.Trim();
        if (library.ReadOnly) { status.Text = library.Error; return 0; }
        if (files.Length > 20) { status.Text = "一次最多拖入 20 個項目，這次未新增。"; return 0; }
        var candidates = new List<ShortcutEntry>(); var skipped = 0;
        foreach (var path in files)
            try { candidates.Add(ShortcutImport.FromPath(path, group, ShortcutRules.Linux)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { skipped++; }
        return ImportEntries(candidates.ToArray(), skipped);
    }
    private int ImportEntries(ShortcutEntry[] candidates, int skipped)
    {
        var next = entries.ToList(); var added = 0;
        foreach (var entry in candidates)
        {
            var step = entry.Steps![0];
            // Linux paths are case-sensitive, so duplicates compare exactly.
            if (next.Count >= 200 || next.Any(e => e.Steps is { Length: 1 } && e.Steps[0].Kind == step.Kind && e.Steps[0].Arguments.Length == 0 && e.Steps[0].WorkingDirectory.Length == 0 && e.Steps[0].Target == step.Target)) { skipped++; continue; }
            next.Add(entry); added++;
        }
        if (added > 0 && !Save(next.ToArray())) return 0;
                status.Text = $"已新增 {added} 個；略過 {skipped} 個（重複、不存在、不支援或已達上限）。沒有啟動任何目標；可右鍵或長按編輯。";
        return added;
    }

    private bool MatchesCurrentExecution(ShortcutEntry snapshot, out ShortcutEntry current)
    {
        current = entries.FirstOrDefault(e => e.Id == snapshot.Id)!;
        if (current is not null && current.ApprovedFingerprint == snapshot.ApprovedFingerprint && current.Steps is not null && snapshot.Steps is not null && current.Steps.SequenceEqual(snapshot.Steps)) return true;
        status.Text = "快捷方式已變更或移除，這次未執行；請重新點選並確認。";
        return false;
    }
    private void SetExecutionStatus(CancellationTokenSource owner, string text) => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed && ReferenceEquals(execution, owner)) status.Text = text;
    });
    public async Task Launch(ShortcutEntry entry)
    {
        if (disposed || execution is not null || !CanRun()) { status.Text = "目前不能執行；請先取消正在進行的巨集或退出遊戲模式。"; return; }
        // A tile can outlive an edit made elsewhere: resolve the current entry and copy its steps.
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
                var fingerprint = ShortcutApproval.Fingerprint(entry, details);
                return (Details: details, Fingerprint: fingerprint, Approved: entry.ApprovedFingerprint == fingerprint);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); reviewing = false;
            if (disposed || !CanRun() || !MatchesCurrentExecution(entry, out var current)) return;
            if (!review.Approved)
            {
                if (!await host.ConfirmAsync("首次執行 · " + current.Name, "請確認以下目標與步驟，只執行你信任的內容。確認後會記住選擇；執行步驟或腳本內容變更時才會再次詢問。\n\n" + review.Details, "確認並執行")) return;
                cancellation.Token.ThrowIfCancellationRequested();
                if (disposed || !CanRun() || !MatchesCurrentExecution(entry, out current)) return;
                var approved = current with { ApprovedFingerprint = review.Fingerprint };
                if (!Save(entries.Select(e => e.Id == current.Id ? approved : e).ToArray())) return;
                current = approved;
            }
            var run = current with { Steps = current.Steps?.ToArray() };
            await Task.Run(() => runner.Run(run, i =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() =>
                {
                    if (disposed || !CanRun()) { cancellation.Cancel(); return; }
                    if (ReferenceEquals(execution, cancellation)) status.Text = $"執行 {run.Name} · 步驟 {i}/{run.Steps!.Length}";
                });
            }, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetExecutionStatus(cancellation, "已完成：" + run.Name);
        }
        catch (OperationCanceledException) { SetExecutionStatus(cancellation, "已取消；不會關閉已開啟的程式，也不能撤銷已完成的操作。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException or TimeoutException)
        {
            AppDiagnostics.Write("shortcut-execution", e);
            SetExecutionStatus(cancellation, "執行停止：" + e.Message);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(execution, cancellation)) return;
                execution = null; cancel.IsEnabled = false; reviewing = false;
            });
        }
    }
    public void Cancel()
    {
        try { execution?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { disposed = true; Cancel(); DetachRemote(); }
}
