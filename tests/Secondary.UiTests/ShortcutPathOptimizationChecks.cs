using System;
using System.IO;
using Secondary.App;
using Secondary.Core;

internal static class ShortcutPathOptimizationChecks
{
    public static void Run(Action<bool, string> check, string? outputDirectory)
    {
        var directory = Path.Combine(outputDirectory ?? Path.Combine(Environment.CurrentDirectory, "artifacts"),
            ".shortcut-path-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var shortcuts = Path.Combine(directory, "shortcuts.json");
        var library = new ShortcutLibrary(shortcuts);
        check(library.Save([
            new("fixture-a", "Explicit shortcut A", Steps: [new("Open", "https://example.invalid/a")]),
            new("fixture-b", "Explicit shortcut B", Steps: [new("Open", "https://example.invalid/b")])
        ]), "Explicit shortcut profile fixture saves");
        using var displays = new DisplayService();
        using var model = new ShellViewModel(displays, new SettingsStore(Path.Combine(directory, "settings.json")));
        var window = new MainWindow(model, registerHotkey: false, shortcutsPath: shortcuts);
        try
        {
            check(window.Launchpad.EntryCount == 2, "MainWindow loads its explicitly supplied shortcut profile without showing a window");
            check(library.Load().Length == 2, "Loading the explicit shortcut profile preserves its stored entries");
        }
        finally
        {
            window.AllowClose = true; window.Close(); Directory.Delete(directory, true);
        }
    }
}
