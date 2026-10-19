using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;
using Secondary.Desktop;

internal static class LaunchpadOptimizationChecks
{
    public static void Run(MainWindow window, ShellViewModel model, Action<bool, string> check, string? outputDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LaunchpadTests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "shortcuts.json");
            var wait = new ShortcutEntry("wait", "Safe wait", "Work", Steps: [new("Wait", Seconds: 1)]);
            var executor = new ShortcutExecutor();
            wait = wait with { ApprovedFingerprint = ShortcutApproval.Fingerprint(wait, executor.Review(wait)) };
            var library = new ShortcutLibrary(path);
            check(library.Save([
                new("alpha", "Alpha", "Work", Steps: [new("Open", "https://example.invalid/alpha")]),
                new("beta", "Beta", "Play", Steps: [new("Open", "https://example.invalid/beta")]), wait
            ]), "Launchpad optimization fixtures save without executing targets");
            using var view = new LaunchpadView(path) { Resources = window.Resources };
            Layout(view);
            check(!Descendants(view).OfType<TextBox>().Any() && Tiles(view).Length == 3, "The launchpad has no search box and shows every tile");
            var groups = Descendants(view).OfType<ComboBox>().Single(); groups.SelectedItem = "Play";
            check(Tiles(view).Length == 1 && AutomationProperties.GetName(Tiles(view)[0]) == "Beta",
                "A group change immediately filters the tiles");
            groups.SelectedIndex = 0;

            Tile(view, "Safe wait").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var cancelled = view.ExecutionCompletion;
            check(view.IsExecuting && view.IsReviewing, "Launch review returns control while its work is pending");
            view.Cancel(); Wait(cancelled);
            check(!view.IsExecuting && !view.IsReviewing && view.ExecutionStatus.StartsWith("已取消"),
                "Cancellation during background review prevents confirmation and execution");
            Tile(view, "Safe wait").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(view.ExecutionCompletion);
            check(!view.IsExecuting && view.ExecutionStatus == "已完成：Safe wait",
                "A previously approved wait-only macro completes through worker execution and dispatcher progress");

            var scriptPath = Path.Combine(directory, "inert.ps1"); File.WriteAllText(scriptPath, "# inert fixture; not executed");
            var script = new ShortcutEntry("script", "Script fixture", Steps: [new("Script", scriptPath)]);
            script = script with { ApprovedFingerprint = ShortcutApproval.Fingerprint(script, executor.Review(script)) };
            var scriptLibrary = new ShortcutLibrary(Path.Combine(directory, "script.json"));
            check(scriptLibrary.Save([script]), "Approved script review fixture persists");
            using var scriptView = new LaunchpadView(Path.Combine(directory, "script.json")) { Resources = window.Resources };
            Layout(scriptView);
            var scriptTile = Tile(scriptView, "Script fixture");
            scriptTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var revoke = scriptTile.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "下次執行重新確認");
            revoke.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(scriptView.ExecutionCompletion);
            check(!scriptView.IsExecuting && scriptView.ExecutionStatus.Contains("已變更") && scriptLibrary.Load().Single().ApprovedFingerprint is null,
                "Revoking approval during asynchronous review blocks stale approval without overwriting the newer entry");

            using (var locked = new FileStream(scriptPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Tile(scriptView, "Script fixture").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(scriptView.ExecutionCompletion);
                check(!scriptView.IsExecuting && scriptView.ExecutionStatus.StartsWith("執行停止"),
                    "Worker review failures return a usable stopped state without reaching script execution");
            }
            Tile(scriptView, "Script fixture").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var unapproved = scriptView.ExecutionCompletion; scriptView.Cancel(); Wait(unapproved);
            check(scriptLibrary.Load().Single().ApprovedFingerprint is null,
                "Cancelling an unapproved review never stores approval or bypasses first-run confirmation");

            var guarded = new ShortcutEntry("guard", "Guarded wait", Steps: [new("Wait", Seconds: 1), new("Wait", Seconds: 1)]);
            guarded = guarded with { ApprovedFingerprint = ShortcutApproval.Fingerprint(guarded, executor.Review(guarded)) };
            var keys = new ShortcutEntry("keys", "No target keys", Steps: [
                new("Keys", "Ctrl+S", ProcessName: "Secondary_Missing_" + Guid.NewGuid().ToString("N"), WindowTitle: "Absent test target")
            ]);
            keys = keys with { ApprovedFingerprint = ShortcutApproval.Fingerprint(keys, executor.Review(keys)) };
            var guardPath = Path.Combine(directory, "guarded.json"); var guardLibrary = new ShortcutLibrary(guardPath);
            check(guardLibrary.Save([guarded, keys]), "Permission and key validation fixtures save");
            var allowRun = true;
            using var guardView = new LaunchpadView(guardPath) { Resources = window.Resources, CanRun = () => allowRun };
            Layout(guardView);
            Tile(guardView, "Guarded wait").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => guardView.ExecutionStatus.StartsWith("執行 Guarded wait"));
            allowRun = false; Wait(guardView.ExecutionCompletion);
            check(!guardView.IsExecuting && guardView.ExecutionStatus.StartsWith("已取消"),
                "Permission changes during a macro stop its remaining steps on the dispatcher");
            allowRun = true;
            Tile(guardView, "No target keys").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(guardView.ExecutionCompletion);
            check(guardView.ExecutionStatus.Contains("找不到唯一"),
                "Worker key validation rejects a nonexistent target before attempting focus or input");

            view.Dispose();
            check(!view.IsExecuting, "Disposing the launchpad ends its execution lifetime");

            // Export and import through the toolbar buttons, with the file pickers and confirmation substituted.
            var exported = Path.Combine(directory, "export.json"); string? confirmText = null; var accept = false;
            using var source = new LaunchpadView(path) { Resources = window.Resources, PickExportPath = () => exported };
            Layout(source);
            Click(source, "匯出設定");
            check(File.Exists(exported) && source.ExecutionStatus.StartsWith("已匯出 3 個") && !File.ReadAllText(exported).Contains(wait.ApprovedFingerprint!),
                "Export writes every shortcut without approvals and reports the count");
            var targetPath = Path.Combine(directory, "target.json");
            check(new ShortcutLibrary(targetPath).Save([new("other", "Other", "Misc", Steps: [new("Open", "https://example.invalid/other")])]), "Import target fixture saves");
            using var target = new LaunchpadView(targetPath) { Resources = window.Resources, PickImportPath = () => exported, ConfirmImport = text => { confirmText = text; return accept; } };
            Layout(target);
            Click(target, "匯入設定");
            check(target.EntryCount == 1 && confirmText!.Contains("3 個捷徑取代目前的 1 個") && target.ExecutionStatus.StartsWith("已取消匯入"),
                "Declining the import confirmation changes nothing");
            accept = true; Click(target, "匯入設定"); Layout(target);
            check(target.EntryCount == 3 && Tiles(target).Length == 3 && new ShortcutLibrary(targetPath).Load().All(e => e.ApprovedFingerprint is null),
                "Accepting replaces the launchpad and every imported shortcut needs confirmation again");
            check(File.Exists(Path.Combine(directory, "target.before-import.json")), "Import keeps a backup of the replaced configuration");
            File.WriteAllText(exported, "not a settings file");
            check(!target.ImportFrom(exported) && target.ExecutionStatus.StartsWith("匯入失敗") && target.EntryCount == 3, "A broken file is refused without changes");
        }
        finally { Directory.Delete(directory, true); }
    }

    // Call after the integration window has been shown. The ordinary suite keeps
    // its existing offscreen layout fixtures and exercises the hidden-view path.
    public static void RunLive(MainWindow window, Action<bool, string> check)
    {
        if (!window.IsVisible) throw new InvalidOperationException("Live launchpad check requires a visible window");
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LaunchpadLiveTests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "shortcuts.json");
        var library = new ShortcutLibrary(path);
        library.Save([
            new("alpha", "Alpha", "Work", Steps: [new("Open", "https://example.invalid/alpha")]),
            new("beta", "Beta", "Play", Steps: [new("Open", "https://example.invalid/beta")])
        ]);
        using var view = new LaunchpadView(path) { CanRun = () => false };
        var page = (StackPanel)window.FindName("ShortcutsPage"); var originalPage = window.CurrentPage;
        page.Children.Remove(window.Launchpad); page.Children.Add(view);
        try
        {
            window.Navigate("shortcuts"); window.UpdateLayout(); Pump(TimeSpan.FromMilliseconds(30));
            var groups = Descendants(view).OfType<ComboBox>().Single(); groups.SelectedItem = "Work";
            check(Tiles(view).Length == 1 && AutomationProperties.GetName(Tiles(view)[0]) == "Alpha",
                "Visible group filtering applies immediately");
            var grouped = Tiles(view)[0]; window.Navigate("home"); window.Navigate("shortcuts"); window.UpdateLayout();
            check(ReferenceEquals(grouped, Tiles(view)[0]), "Returning to shortcuts keeps the tiles without a rebuild");
        }
        finally
        {
            page.Children.Remove(view); page.Children.Add(window.Launchpad); window.Navigate(originalPage);
            Directory.Delete(directory, true);
        }
    }
    private static void Click(LaunchpadView view, string label) =>
        Descendants(view).OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Button[] Tiles(LaunchpadView view) => Descendants(view).OfType<Button>().Where(b => b.Content is StackPanel).ToArray();
    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(900, 700)); element.Arrange(new Rect(0, 0, 900, 700)); element.UpdateLayout();
    }
    private static Button Tile(LaunchpadView view, string name) => Tiles(view).Single(b => AutomationProperties.GetName(b) == name);
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static void Pump(TimeSpan duration)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }
    private static void Wait(Task task)
    {
        WaitUntil(() => task.IsCompleted); task.GetAwaiter().GetResult();
    }
    private static void WaitUntil(Func<bool> complete)
    {
        var deadline = Stopwatch.StartNew();
        while (!complete() && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
        if (!complete()) throw new TimeoutException("Launchpad optimization test did not finish");
    }
}
