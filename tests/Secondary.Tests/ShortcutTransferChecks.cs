using Secondary.Core;

// Exporting and importing the launchpad configuration (entries, groups, layout).
internal static class ShortcutTransferChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.Transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var site = new ShortcutEntry("a", "網站", "日常", "🌐", [new("Open", "https://example.com")], ApprovedFingerprint: "approved");
            var tool = new ShortcutEntry("b", "工具", "工作", Steps: [new("Open", @"C:\Tools\tool.exe", "--fast")]);
            var keys = new ShortcutEntry("c", "存檔", "工作", Steps: [new("Keys", "Ctrl+S", ProcessName: "notepad", WindowTitle: "Document")]);
            var file = Path.Combine(directory, "export.json");
            File.WriteAllBytes(file, ShortcutTransfer.Export([site, tool], "Horizontal"));
            check(!File.ReadAllText(file).Contains("approved"), "Exports leave out first-run approvals");

            var imported = ShortcutTransfer.Read(file, ShortcutRules.Windows);
            check(imported.Entries.Length == 2 && imported.Skipped == 0 && imported.GroupLayout == "Horizontal"
                && Same(imported.Entries[0], site with { ApprovedFingerprint = null }) && Same(imported.Entries[1], tool),
                "An export reads back with its names, groups, icons, steps, order and layout");

            var broken = new ShortcutEntry("x", "壞的", Steps: [new("Open", "javascript:alert(1)")]);
            File.WriteAllBytes(file, ShortcutTransfer.Export([site, keys, site with { Name = "同 id" }, broken], "Vertical"));
            var onLinux = ShortcutTransfer.Read(file, ShortcutRules.Linux);
            check(onLinux.Entries.Length == 2 && onLinux.Pending.Single().Name == "存檔" && onLinux.Skipped == 1,
                "Shortcuts only another system can run become pending; ones no system can run are skipped");
            check(onLinux.Entries.Select(e => e.Id).Distinct().Count() == 2 && onLinux.Entries.Single(e => e.Name == "同 id").Id != "a",
                "Duplicate ids are renumbered instead of lost");

            File.WriteAllBytes(file, ShortcutTransfer.Export([], "Vertical", [keys]));
            var back = ShortcutTransfer.Read(file, ShortcutRules.Windows);
            check(back.Entries.Single().Name == "存檔" && back.Pending.Length == 0, "A pending shortcut this system can run is imported as a normal one");
            check(ShortcutTransfer.Read(file, ShortcutRules.Linux).Pending.Single().Name == "存檔", "Pending shortcuts travel in exports");
            var files = new ShortcutEntry("l", "檔案", Steps: [new("Open", "/usr/bin/nautilus")]);
            check(ShortcutPolicy.Validate(files, ShortcutRules.Windows) is not null && ShortcutPolicy.Validate(files, ShortcutRules.Linux) is null,
                "Windows rules refuse Linux paths");
            File.WriteAllBytes(file, ShortcutTransfer.Export([site, files], "Vertical"));
            var onWindows = ShortcutTransfer.Read(file, ShortcutRules.Windows);
            check(onWindows.Entries.Single().Id == "a" && onWindows.Pending.Single().Name == "檔案", "A Linux program imported on Windows becomes pending");
            File.WriteAllBytes(file, ShortcutTransfer.Export([broken], "Vertical"));
            check(Throws(() => ShortcutTransfer.Read(file, ShortcutRules.Linux)), "A file with nothing usable is refused");
            File.WriteAllText(file, "{ not json");
            check(Throws(() => ShortcutTransfer.Read(file, ShortcutRules.Windows)), "A file that is not JSON is refused");
            File.WriteAllText(file, """{ "Version": 2, "Entries": [] }""");
            check(Throws(() => ShortcutTransfer.Read(file, ShortcutRules.Windows)), "An unknown format version is refused");
            File.WriteAllText(file, """{ "Version": 1, "Entries": [] }""");
            check(ShortcutTransfer.Read(file, ShortcutRules.Windows).Entries.Length == 0, "An exported empty launchpad imports as empty");

            var path = Path.Combine(directory, "shortcuts.json");
            var library = new ShortcutLibrary(path);
            check(library.Save([tool]), "Library fixture saves");
            File.WriteAllBytes(file, ShortcutTransfer.Export([site], "Horizontal"));
            check(library.Replace(ShortcutTransfer.Read(file, ShortcutRules.Windows)), "Import replaces the library");
            var reloaded = new ShortcutLibrary(path);
            check(reloaded.Load().Single().Id == "a" && reloaded.GroupLayout == "Horizontal" && reloaded.Load().Single().ApprovedFingerprint is null,
                "The replaced library persists the imported entries and layout without approvals");
            check(new ShortcutLibrary(library.BackupPath).Load().Single().Id == "b", "The previous configuration is kept as a backup");
            var linuxPath = Path.Combine(directory, "linux.json");
            var linuxLibrary = new ShortcutLibrary(linuxPath, ShortcutRules.Linux);
            File.WriteAllBytes(file, ShortcutTransfer.Export([site, keys], "Vertical"));
            check(linuxLibrary.Replace(ShortcutTransfer.Read(file, ShortcutRules.Linux)), "An import with pending shortcuts saves");
            var linuxReloaded = new ShortcutLibrary(linuxPath, ShortcutRules.Linux);
            check(linuxReloaded.Load().Single().Id == "a" && linuxReloaded.Pending.Single().Id == "c", "Pending shortcuts persist apart from runnable ones");
            check(linuxReloaded.Save(linuxReloaded.Load(), []) && new ShortcutLibrary(linuxPath, ShortcutRules.Linux) is var cleared && cleared.Load().Length == 1 && cleared.Pending.Length == 0,
                "Clearing pending shortcuts keeps the runnable ones");
            check(!linuxReloaded.Save([site], [broken]), "A pending shortcut no system can run is not saved");
            // A file from an earlier version that saved Linux-only shortcuts as normal ones (the reverse case on a
            // Linux host) still loads: they become pending instead of making the launchpad read-only.
            var older = Path.Combine(directory, "older.json");
            File.WriteAllText(older, System.Text.Json.JsonSerializer.Serialize(new ShortcutDocument(Entries: [site, files])));
            var olderLibrary = new ShortcutLibrary(older);
            check(olderLibrary.Load().Single().Id == "a" && olderLibrary.Pending.Single().Name == "檔案" && !olderLibrary.ReadOnly,
                "Saved shortcuts this system cannot run load as pending instead of failing");
            check(olderLibrary.Save(olderLibrary.Load()) && new ShortcutLibrary(older) is var resaved && resaved.Load().Length == 1 && resaved.Pending.Length == 1,
                "The next save stores them as pending");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Same(ShortcutEntry a, ShortcutEntry b) => a with { Steps = null } == b with { Steps = null } && a.Steps!.SequenceEqual(b.Steps!);
    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
