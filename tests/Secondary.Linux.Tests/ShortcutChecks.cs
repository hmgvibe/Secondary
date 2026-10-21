using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Platform;
using Secondary.Linux.Shortcuts;
using static Program;

// L3: launchpad, editor, import, ordering, confirmation and the Linux executor.
internal static class ShortcutChecks
{
    public static void Run(string? output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LinuxShortcuts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestLaunchpad(directory, output);
            TestEditorInWindow(directory, output);
            TestTransfer(directory);
            if (OperatingSystem.IsLinux()) TestLinuxExecutor(directory);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestLaunchpad(string directory, string? output)
    {
        var host = new FakeHost();
        var executor = new FakeExecutor();
        var path = Path.Combine(directory, "shortcuts.json");
        var launchpad = new LaunchpadView(path, host, executor);
        var window = new Window { Width = 1200, Height = 820, Content = new ScrollViewer { Content = launchpad } };
        Palette.Apply(window.Resources, false, false, false);
        window.Show(); Pump();
        Check(launchpad.EntryCount == 0 && launchpad.ExecutionStatus.Contains("＋ 新增", StringComparison.Ordinal), "An empty launchpad explains how to add shortcuts and runs nothing");

        // Import: folders, documents, .desktop launchers, .sh scripts; duplicates and missing paths are skipped.
        var folder = Directory.CreateDirectory(Path.Combine(directory, "Projects")).FullName;
        var document = Path.Combine(directory, "notes.txt"); File.WriteAllText(document, "x");
        var desktop = Path.Combine(directory, "org.example.Player.desktop");
        File.WriteAllText(desktop, "[Desktop Entry]\nType=Application\nName=Player\nName[zh_TW]=播放器\nExec=player %U\n");
        var script = Path.Combine(directory, "backup.sh"); File.WriteAllText(script, "exit 0\n");
        var added = launchpad.ImportFiles([folder, document, desktop, script, document, Path.Combine(directory, "missing.txt")]);
        Check(added == 4 && launchpad.EntryCount == 4 && launchpad.ExecutionStatus.Contains("略過 2 個", StringComparison.Ordinal), "Dropped files import once each and report what was skipped");
        Check(launchpad.Entries.Any(e => e.Name == "播放器") && launchpad.Entries.Single(e => e.Name == "backup").Steps![0].Kind == "Script", "Launchers keep their own names; scripts become script steps");
        Check(launchpad.ImportFiles(Enumerable.Repeat(document, 21).ToArray()) == 0 && launchpad.ExecutionStatus.Contains("最多拖入 20", StringComparison.Ordinal), "More than 20 dropped items are refused together");
        Check(new ShortcutLibrary(path, ShortcutRules.Linux).Load().Length == 4, "Imported shortcuts are saved");

        // Groups, layout and order; there is no search box.
        Check(!launchpad.GetVisualDescendantsOfType<TextBox>().Any(t => t.TemplatedParent is not ComboBox) && launchpad.Tiles.Count() == 4, "The launchpad has no search box and shows every tile");
        var first = launchpad.Entries[0]; var second = launchpad.Entries[1];
        launchpad.MoveBy(first, 1);
        Check(launchpad.Entries[0].Id == second.Id && launchpad.Entries[1].Id == first.Id, "Move later swaps within the group and saves");
        var dragged = launchpad.Entries[0];
        var targetTile = launchpad.Tiles.Last();
        var data = new DataTransfer(); data.Add(DataTransferItem.Create(DataFormat.CreateInProcessFormat<string>("Secondary.ShortcutId"), dragged.Id));
        targetTile.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, targetTile, new Point(10, 10), KeyModifiers.None));
        Pump();
        Check(launchpad.Entries[^2].Id == dragged.Id, "Dropping a tile on another moves it before that tile");
        var layoutBefore = launchpad.GroupLayout;
        var menu = launchpad.Tiles.First().ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header as string).ToArray();
        Check(menu.SequenceEqual(["編輯", "複製", "往前移", "往後移", "下次執行重新確認", "刪除"]), "Each tile offers edit, copy, move, re-confirm and delete");
        var toggle = launchpad.GetVisualDescendantsOfType<Button>().First(b => (b.Content as string)?.StartsWith("分組：", StringComparison.Ordinal) == true);
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(launchpad.GroupLayout != layoutBefore && new ShortcutLibrary(path, ShortcutRules.Linux) is var reloaded && reloaded.Load().Length == 4 && reloaded.GroupLayout == launchpad.GroupLayout, "The group layout toggles and is saved");
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-launchpad.png"), PngBitmapEncoderOptions.Default);

        // First run asks; declining runs nothing; approval is remembered until the steps change.
        var target = launchpad.Entries.Single(e => e.Name == "notes");
        host.Answers.Enqueue(false);
        launchpad.Launch(target).Wait(TimeSpan.FromSeconds(5)); PumpUntil(() => !launchpad.IsExecuting);
        Check(host.Confirmations.Count == 1 && host.Confirmations[0].Contains(document, StringComparison.Ordinal) && executor.Calls.Count == 0, "First run shows the target and runs nothing when declined");
        host.Answers.Enqueue(true);
        Wait(launchpad.Launch(target)); PumpUntil(() => !launchpad.IsExecuting);
        Check(executor.Calls.Count == 1 && launchpad.ExecutionStatus.StartsWith("已完成", StringComparison.Ordinal), "Confirmed shortcuts run");
        Check(new ShortcutLibrary(path, ShortcutRules.Linux).Load().Single(e => e.Id == target.Id).ApprovedFingerprint is not null, "Approval is saved");
        Wait(launchpad.Launch(target)); PumpUntil(() => !launchpad.IsExecuting);
        Check(host.Confirmations.Count == 2 && executor.Calls.Count == 2, "An approved shortcut runs again without asking");
        executor.ReviewSuffix = "changed";
        host.Answers.Enqueue(false);
        Wait(launchpad.Launch(target)); PumpUntil(() => !launchpad.IsExecuting);
        Check(host.Confirmations.Count == 3 && executor.Calls.Count == 2, "A changed review (for example a modified script) asks again");
        executor.ReviewSuffix = "";

        // Cancellation and refusal while the app cannot run macros.
        executor.Block = true;
        var running = launchpad.Launch(target);
        PumpUntil(() => executor.Started);
        Check(launchpad.IsExecuting, "A running macro is tracked");
        launchpad.Cancel();
        Wait(running); PumpUntil(() => !launchpad.IsExecuting);
        Check(launchpad.ExecutionStatus.StartsWith("已取消", StringComparison.Ordinal), "Cancelling stops the macro and says nothing is undone");
        executor.Block = false;
        launchpad.CanRun = () => false;
        Wait(launchpad.Launch(target));
        Check(launchpad.ExecutionStatus.StartsWith("目前不能執行", StringComparison.Ordinal), "Macros do not start while the app cannot run them");
        launchpad.CanRun = () => true;
        executor.Fail = true;
        Wait(launchpad.Launch(target)); PumpUntil(() => !launchpad.IsExecuting);
        Check(launchpad.ExecutionStatus.StartsWith("執行停止", StringComparison.Ordinal), "A failing step stops the macro with its reason");
        executor.Fail = false;

        // Edit and delete go through the host; delete asks first.
        host.Edits.Enqueue(target with { Name = "筆記" });
        Wait(launchpad.Edit(target));
        Check(launchpad.Entries.Any(e => e.Name == "筆記"), "Edits are saved");
        window.Close(); Pump();
        launchpad.Dispose();
    }

    private static void TestEditorInWindow(string directory, string? output)
    {
        var shortcuts = Path.Combine(directory, "window-shortcuts.json");
        var model = new ShellModel(new SettingsStore(Path.Combine(directory, "settings.json")));
        var window = new MainWindow(model, new DesktopLauncher(Path.Combine(directory, "data"), "/apps/s"), shortcuts) { Width = 1200, Height = 820 };
        window.Show(); Pump();
        window.Navigate("shortcuts"); Pump();
        Check(window.FindControl<StackPanel>("ShortcutsPage")!.Children.Contains(window.Launchpad), "The shortcuts page hosts the launchpad");
        var sample = Directory.CreateDirectory(Path.Combine(directory, "Music")).FullName;
        window.Launchpad.ImportFiles([sample, Path.Combine(directory, "org.example.Player.desktop"), Path.Combine(directory, "backup.sh"), Path.Combine(directory, "notes.txt")]); Pump();
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-shortcuts-page.png"), PngBitmapEncoderOptions.Default);
        var edit = window.EditShortcutAsync(new ShortcutEntry("e1", "新快捷方式", Steps: [new()]), ShortcutRules.Linux); Pump();
        var editor = window.OpenShortcutEditor!;
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-shortcut-editor.png"), PngBitmapEncoderOptions.Default);
        Check(window.ShortcutEditorOpen && editor.Steps.Count == 1, "The editor opens in the window's dialog layer");
        var addButtons = editor.GetVisualDescendantsOfType<Button>().Select(b => b.Content as string).Where(t => t?.StartsWith("＋", StringComparison.Ordinal) == true).ToArray();
        Check(addButtons.SequenceEqual(["＋ 開啟", "＋ 等待", "＋ 腳本"]), "Linux offers open, wait and script steps but no key step");
        // A Unix path, so the path check passes on any host and the .sh rule is what refuses it.
        editor.Steps[0].Target!.Text = "/opt/tools/run.sh";
        Check(!window.SaveShortcutEditor() && window.ShortcutEditorOpen && editor.Error.IsVisible && (editor.Error.Text ?? "").Contains(".sh", StringComparison.Ordinal), "An .sh file must use the script step, with the Linux rule shown");
        editor.Steps[0].Target!.Text = "https://example.com/";
        editor.AddStep("Wait"); editor.Steps[1].Seconds!.Text = "2";
        editor.MoveStep(1, -1);
        Check(editor.Steps[0].Value.Kind == "Wait", "Steps can be reordered");
        Check(window.SaveShortcutEditor() && !window.ShortcutEditorOpen && edit.IsCompleted && edit.Result!.Steps!.Select(s => s.Kind).SequenceEqual(["Wait", "Open"]), "Saving returns the edited shortcut without running it");
        var cancelled = window.EditShortcutAsync(new ShortcutEntry("e2", "x", Steps: [new()]), ShortcutRules.Linux); Pump();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
        Check(cancelled.IsCompleted && cancelled.Result is null && !window.ShortcutEditorOpen, "Esc closes the editor without saving");
        window.Navigate("game"); Pump();
        Check(!window.Launchpad.CanRun(), "Macros cannot run in game mode");
        window.Navigate("exit");
        window.AllowClose = true; window.Close(); Pump();
        model.Dispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void TestLinuxExecutor(string directory)
    {
        var started = new List<ProcessStartInfo>();
        var recording = new LinuxShortcutExecutor(directory)
        { Start = info => { started.Add(info); return Process.Start(new ProcessStartInfo("true")); } };
        Wait(recording.Execute(new ShortcutStep("Open", "https://example.com/a"), CancellationToken.None));
        Wait(recording.Execute(new ShortcutStep("Open", directory), CancellationToken.None));
        var launcher = Path.Combine(directory, "tool.desktop"); File.WriteAllText(launcher, "[Desktop Entry]\nType=Application\nName=Tool\nExec=tool --flag\n");
        Wait(recording.Execute(new ShortcutStep("Open", launcher), CancellationToken.None));
        Check(started.Select(s => s.FileName + " " + string.Join(' ', s.ArgumentList)).SequenceEqual(["gio open https://example.com/a", "gio open " + directory, "gio launch " + launcher]),
            "Websites and folders open through the desktop; launchers start through gio launch");
        Check(recording.Review(new ShortcutEntry("r", "r", Steps: [new("Open", launcher)])).Contains("tool --flag", StringComparison.Ordinal), "Review shows the command a launcher will run");
        var program = Path.Combine(directory, "program"); File.WriteAllText(program, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        started.Clear();
        Wait(recording.Execute(new ShortcutStep("Open", program, "--windowed"), CancellationToken.None));
        Check(started.Single().FileName == program && started.Single().Arguments == "--windowed" && !started.Single().UseShellExecute, "Executables start directly with their arguments");
        var keys = Throws(() => recording.Execute(new ShortcutStep("Keys", "Ctrl+S", ProcessName: "x", WindowTitle: "y"), CancellationToken.None));
        Check(keys?.Message.Contains("Wayland", StringComparison.Ordinal) == true, "Key steps stop with the Wayland explanation");

        // Scripts run through /bin/sh from a private copy of the confirmed bytes.
        var real = new LinuxShortcutExecutor(directory);
        var marker = Path.Combine(directory, "ran.txt");
        var script = Path.Combine(directory, "write.sh"); File.WriteAllText(script, $"echo ok > '{marker}'\n");
        var entry = new ShortcutEntry("s", "s", Steps: [new("Script", script)]);
        real.Review(entry);
        Wait(real.Execute(entry.Steps![0], CancellationToken.None));
        Check(File.Exists(marker) && !Directory.EnumerateFiles(Path.Combine(directory, "secondary", "scripts")).Any(), "A confirmed script runs and its private copy is removed");
        File.AppendAllText(script, "echo changed\n");
        Check(Throws(() => real.Execute(entry.Steps[0], CancellationToken.None))?.Message.Contains("重新確認", StringComparison.Ordinal) == true, "A script changed after confirmation does not run");
        File.WriteAllText(script, "exit 3\n"); real.Review(entry);
        Check(Throws(() => real.Execute(entry.Steps[0], CancellationToken.None))?.Message.Contains("結束碼 3", StringComparison.Ordinal) == true, "A failing script stops the macro with its exit code");
        File.WriteAllText(script, "sleep 30\n"); real.Review(entry);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        var cancelled = Throws(() => real.Execute(entry.Steps[0], stop.Token));
        Check(cancelled is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(5), "Cancelling stops a running script promptly");
    }

    private static Exception? Throws(Func<Task> action)
    {
        // Run off the UI thread: blocking it would deadlock anything that resumes there.
        try { Task.Run(action).GetAwaiter().GetResult(); return null; }
        catch (Exception error) { return error; }
    }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> done) { for (var i = 0; i < 500 && !done(); i++) { Pump(); Thread.Sleep(10); } Pump(); }

    // Export and import through the toolbar buttons; a Windows export keeps only what Linux can run.
    private static void TestTransfer(string directory)
    {
        var path = Path.Combine(directory, "transfer.json");
        var web = new ShortcutEntry("web", "網站", "日常", Steps: [new("Open", "https://example.com")]);
        var keys = new ShortcutEntry("keys", "存檔", "工作", Steps: [new("Keys", "Ctrl+S", ProcessName: "notepad", WindowTitle: "Document")]);
        var windowsFile = Path.Combine(directory, "from-windows.json");
        File.WriteAllBytes(windowsFile, ShortcutTransfer.Export([web, keys], "Horizontal"));
        Check(new ShortcutLibrary(path, ShortcutRules.Linux).Save([web with { Id = "old", Name = "舊的" }]), "Transfer fixture saves");
        var host = new FakeHost { ImportPath = windowsFile, ExportPath = Path.Combine(directory, "exported.json") };
        var executor = new FakeExecutor();
        var launchpad = new LaunchpadView(path, host, executor);
        var window = new Window { Width = 1200, Height = 820, Content = new ScrollViewer { Content = launchpad } };
        Palette.Apply(window.Resources, false, false, false);
        window.Show(); Pump();
        Button Tool(string label) => launchpad.GetVisualDescendantsOfType<Button>().Single(b => b.Content as string == label);
        host.Answers.Enqueue(false);
        Tool("匯入設定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => launchpad.ExecutionStatus.StartsWith("已取消匯入", StringComparison.Ordinal));
        Check(launchpad.Entries.Single().Id == "old" && host.Confirmations[^1].Contains("1 個無法在這臺設備執行，會列為「待處理」", StringComparison.Ordinal), "Declining keeps the launchpad and the prompt names the pending shortcuts");
        host.Answers.Enqueue(true);
        Tool("匯入設定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => launchpad.ExecutionStatus.StartsWith("已匯入", StringComparison.Ordinal));
        Check(launchpad.Entries.Single().Id == "web" && launchpad.GroupLayout == "Horizontal" && launchpad.Tiles.Count() == 1, "Accepting replaces shortcuts and layout with what Linux can run");
        Check(launchpad.PendingEntries.Single().Id == "keys" && launchpad.PendingTiles.Count() == 1, "What Linux cannot run is kept as a pending tile");
        var pendingTile = launchpad.PendingTiles.Single();
        Check(((TextBlock)((StackPanel)pendingTile.Content!).Children[2]).Text!.Contains("待處理", StringComparison.Ordinal)
            && pendingTile.ContextMenu!.Items.OfType<MenuItem>().Select(i => (i.Header as string, i.IsEnabled)).SequenceEqual([("移到主電腦執行", false), ("編輯", false), ("刪除", true)]),
            "A pending tile is marked, and without a main computer or a Linux editor for window keys it can only be deleted");
        var calls = executor.Calls.Count;
        pendingTile.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
        Check(launchpad.ExecutionStatus.Contains("無法在這臺設備執行", StringComparison.Ordinal) && executor.Calls.Count == calls, "Pressing a pending tile runs nothing and explains the choices");
        Tool("匯出設定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); PumpUntil(() => File.Exists(host.ExportPath));
        Check(ShortcutTransfer.Read(host.ExportPath!, ShortcutRules.Linux) is { Entries: [{ Name: "網站" }], Pending: [{ Name: "存檔" }] }, "Export writes the current launchpad with its pending shortcuts");
        host.Answers.Enqueue(true);
        launchpad.PendingTiles.Single().ContextMenu!.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(() => launchpad.PendingEntries.Length == 0);
        Check(launchpad.PendingEntries.Length == 0 && !launchpad.PendingTiles.Any() && new ShortcutLibrary(path, ShortcutRules.Linux) is var saved && saved.Load().Length == 1 && saved.Pending.Length == 0,
            "Deleting a pending shortcut asks first and keeps the others");
        window.Close(); launchpad.Dispose();
    }

    private sealed class FakeHost : ILaunchpadHost
    {
        public string? ExportPath, ImportPath;
        public Task<string?> PickExportPathAsync(string suggestedName) => Task.FromResult(ExportPath);
        public Task<string?> PickImportPathAsync() => Task.FromResult(ImportPath);
        public Queue<bool> Answers { get; } = new();
        public Queue<ShortcutEntry?> Edits { get; } = new();
        public List<string> Confirmations { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message, string accept) { Confirmations.Add(message); return Task.FromResult(Answers.Count > 0 && Answers.Dequeue()); }
        public Task<ShortcutEntry?> EditShortcutAsync(ShortcutEntry entry, ShortcutRules rules) => Task.FromResult(Edits.Count > 0 ? Edits.Dequeue() : null);
        public Task<AppSelection?> PickAppsAsync(ISet<string> existing, IReadOnlyList<string> groups, string group) => Task.FromResult<AppSelection?>(null);
    }
    private sealed class FakeExecutor : IReviewingExecutor
    {
        public List<ShortcutStep> Calls { get; } = new();
        public string ReviewSuffix { get; set; } = "";
        public bool Block, Fail, Started;
        public string Review(ShortcutEntry entry) => string.Join("\n", entry.Steps!.Select(s => s.Kind + " " + s.Target)) + ReviewSuffix;
        public async Task Execute(ShortcutStep step, CancellationToken cancellation)
        {
            Started = true;
            if (Fail) throw new IOException("測試失敗");
            if (Block) await Task.Delay(Timeout.Infinite, cancellation);
            Calls.Add(step);
        }
    }
}
