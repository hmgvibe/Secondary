using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Platform;
using static Program;

// Installed-app catalog (XDG directories, GNOME filtering, icon themes, SVG) and the app picker.
internal static class AppChecks
{
    // The catalog reads POSIX XDG directory lists, so it runs where those exist: on Linux.
    public static void Run(string? output)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "Secondary.Apps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = Fixture(root);
            TestCatalog(root, catalog);
            TestPicker(root, catalog, output);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Desktop(string directory, string name, string body)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "[Desktop Entry]\n" + body + "\n[Desktop Action Extra]\nName=ignored\n");
        return path;
    }
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><rect width=\"24\" height=\"24\" rx=\"5\" fill=\"#3b82f6\"/></svg>";

    private static AppCatalog Fixture(string root)
    {
        var home = Path.Combine(root, "home");
        var user = Path.Combine(home, ".local", "share");
        var system = Path.Combine(root, "usr", "share");
        Desktop(Path.Combine(system, "applications"), "org.gnome.TextEditor.desktop", "Type=Application\nName=Text Editor\nName[zh_TW]=文字編輯器\nComment=Edit text files\nComment[zh_TW]=編輯文字檔案\nExec=gnome-text-editor %U\nIcon=org.gnome.TextEditor");
        Desktop(Path.Combine(system, "applications"), "firefox.desktop", "Type=Application\nName=Firefox\nExec=firefox %u\nIcon=firefox");
        Desktop(Path.Combine(system, "applications"), "shadowed.desktop", "Type=Application\nName=Shadowed\nExec=shadowed");
        Desktop(Path.Combine(user, "applications"), "shadowed.desktop", "Type=Application\nName=Shadowed\nExec=shadowed\nNoDisplay=true");
        Desktop(Path.Combine(system, "applications"), "hidden.desktop", "Type=Application\nName=Hidden\nExec=x\nHidden=true");
        Desktop(Path.Combine(system, "applications"), "kde-only.desktop", "Type=Application\nName=KDE Only\nExec=x\nOnlyShowIn=KDE;");
        Desktop(Path.Combine(system, "applications"), "not-gnome.desktop", "Type=Application\nName=Not GNOME\nExec=x\nNotShowIn=GNOME;");
        Desktop(Path.Combine(system, "applications"), "removed.desktop", "Type=Application\nName=Removed\nExec=gone\nTryExec=gone");
        Desktop(Path.Combine(system, "applications"), "link.desktop", "Type=Link\nName=Link\nURL=https://example.com");
        Desktop(Path.Combine(system, "applications", "games"), "chess.desktop", $"Type=Application\nName=Chess\nExec=chess\nIcon={Path.Combine(root, "chess.png")}");
        Desktop(Path.Combine(root, "flatpak", "applications"), "org.example.Notes.desktop", "Type=Application\nName=Notes\nExec=flatpak run org.example.Notes\nIcon=org.example.Notes");
        // Icons: an SVG in hicolor/scalable, PNGs at two sizes, an absolute path and a broken SVG.
        Directory.CreateDirectory(Path.Combine(system, "icons", "hicolor", "scalable", "apps"));
        File.WriteAllText(Path.Combine(system, "icons", "hicolor", "scalable", "apps", "org.gnome.TextEditor.svg"), Svg);
        foreach (var size in new[] { "48x48", "256x256" })
        {
            var folder = Directory.CreateDirectory(Path.Combine(system, "icons", "hicolor", size, "apps")).FullName;
            using var bitmap = new RenderTargetBitmap(new PixelSize(size == "48x48" ? 48 : 256, size == "48x48" ? 48 : 256));
            bitmap.Save(Path.Combine(folder, "firefox.png"), PngBitmapEncoderOptions.Default);
        }
        using (var chess = new RenderTargetBitmap(new PixelSize(64, 64))) chess.Save(Path.Combine(root, "chess.png"), PngBitmapEncoderOptions.Default);
        Directory.CreateDirectory(Path.Combine(root, "flatpak", "icons", "hicolor", "scalable", "apps"));
        File.WriteAllText(Path.Combine(root, "flatpak", "icons", "hicolor", "scalable", "apps", "org.example.Notes.svg"), "<svg broken");
        var environment = new Dictionary<string, string>
        {
            ["XDG_DATA_HOME"] = user, ["XDG_DATA_DIRS"] = system + ":" + Path.Combine(root, "flatpak") + ":relative/ignored",
            ["XDG_CURRENT_DESKTOP"] = "ubuntu:GNOME"
        };
        // No extra directories: the fixture must not see the machine's real Flatpak or Snap apps.
        return new AppCatalog(k => environment.GetValueOrDefault(k), home, program => program != "gone", []);
    }

    private static void TestCatalog(string root, AppCatalog catalog)
    {
        var apps = catalog.Load();
        var names = apps.Select(a => a.Name).ToArray();
        Check(names.Contains("文字編輯器") && names.Contains("Firefox") && names.Contains("Chess") && names.Contains("Notes"), "Installed apps are read from user, system and Flatpak directories: " + string.Join(", ", names));
        Check(!names.Intersect(["Shadowed", "Hidden", "KDE Only", "Not GNOME", "Removed", "Link"]).Any(),
            "A user entry shadows the system one, and hidden, other-desktop, removed and non-application entries are left out");
        Check(apps.Single(a => a.Name == "Chess").Id == "games-chess.desktop", "Desktop IDs of nested launchers join folders with '-'");
        Check(apps.Single(a => a.Name == "文字編輯器").Comment == "編輯文字檔案", "Names and descriptions prefer Traditional Chinese");
        Check(catalog.DataDirectories().All(d => d.StartsWith('/') || Path.IsPathRooted(d)), "Relative XDG data directories are ignored");
        var svg = catalog.IconPath("org.gnome.TextEditor");
        var png = catalog.IconPath("firefox");
        Check(svg?.EndsWith(".svg", StringComparison.Ordinal) == true && png?.Contains("256x256", StringComparison.Ordinal) == true, "Icon lookup prefers scalable SVG, then the largest PNG");
        Check(catalog.IconPath(Path.Combine(root, "chess.png")) == Path.Combine(root, "chess.png") && catalog.IconPath("no-such-icon") is null, "Absolute icon paths are used directly; missing icons resolve to nothing");
        var rendered = AppCatalog.Render(svg!, 64);
        Check(rendered is { PixelSize.Width: 64, PixelSize.Height: 64 }, "SVG icons render to bitmaps at the requested size");
        Check(AppCatalog.Render(catalog.IconPath("org.example.Notes")!, 64) is null, "A broken SVG falls back instead of failing");
    }

    private static void TestPicker(string root, AppCatalog catalog, string? output)
    {
        var model = new ShellModel(new SettingsStore(Path.Combine(root, "settings.json")));
        var window = new MainWindow(model, new DesktopLauncher(Path.Combine(root, "data"), "/apps/s"), Path.Combine(root, "shortcuts.json")) { Width = 1200, Height = 820, AppCatalog = catalog };
        window.Show(); Pump();
        window.Navigate("shortcuts"); Pump();
        var pick = window.Launchpad.PickApps();
        for (var i = 0; i < 300 && window.OpenAppPicker is null; i++) { Thread.Sleep(10); Pump(); }
        var picker = window.OpenAppPicker!;
        Check(window.AppPickerOpen && picker.Count == 4, "The picker lists the installed apps");
        for (var i = 0; i < 100; i++) { Thread.Sleep(10); Pump(); }
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-app-picker.png"), PngBitmapEncoderOptions.Default);
        picker.Filter("編輯");
        Check(picker.Visible.Select(a => a.Name).SequenceEqual(["文字編輯器"]), "Search matches Chinese names and descriptions");
        picker.Filter("");
        Check(picker.Group == "日常", "The group defaults to the launchpad's current group");
        picker.SetGroup("  工具  ");
        picker.Toggle("org.gnome.TextEditor.desktop", true); picker.Toggle("firefox.desktop", true); Pump();
        Check(window.FindControl<Button>("AddAppsButton")!.IsEnabled && (window.FindControl<TextBlock>("AppPickerStatus")!.Text ?? "").Contains("已選 2 個", StringComparison.Ordinal), "Selecting apps enables adding them");
        window.CloseAppPicker(true); Pump();
        for (var i = 0; i < 200 && !pick.IsCompleted; i++) { Thread.Sleep(10); Pump(); }
        var addedNames = window.Launchpad.Entries.Select(e => e.Name).ToHashSet();
        Check(window.Launchpad.Entries.All(e => e.Group == "工具"), "Every app chosen together goes into the group set in the picker");
        Check(addedNames.SetEquals(["Firefox", "文字編輯器"]) && window.Launchpad.Entries.All(e => e.Steps![0].Target.EndsWith(".desktop", StringComparison.Ordinal)),
            "Chosen apps become launcher shortcuts without running them (" + string.Join(", ", addedNames) + "; " + window.Launchpad.ExecutionStatus + ")");
        for (var i = 0; i < 200 && !window.Launchpad.Tiles.All(t => t.GetVisualDescendantsOfType<Image>().Any()); i++) { Thread.Sleep(10); Pump(); }
        Check(window.Launchpad.Tiles.All(t => t.GetVisualDescendantsOfType<Image>().Any()), "Launcher tiles show the app's own icon");
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-app-tiles.png"), PngBitmapEncoderOptions.Default);
        var again = window.Launchpad.PickApps();
        for (var i = 0; i < 300 && window.OpenAppPicker is null; i++) { Thread.Sleep(10); Pump(); }
        Check(window.OpenAppPicker!.Group == "日常" && window.OpenAppPicker.GetVisualDescendantsOfType<AutoCompleteBox>().Single().ItemsSource is IEnumerable<string> offered && offered.Contains("工具"),
            "Existing groups are offered in the picker");
        var boxes = window.OpenAppPicker!.GetVisualDescendantsOfType<CheckBox>().ToArray();
        Check(boxes.Count(b => !b.IsEnabled) == 2, "Apps already in the launchpad cannot be added twice");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
        for (var i = 0; i < 100 && !again.IsCompleted; i++) { Thread.Sleep(10); Pump(); }
        Check(again.IsCompleted && !window.AppPickerOpen && window.Launchpad.EntryCount == 2, "Esc closes the picker without adding anything");
        window.AllowClose = true; window.Close(); Pump();
        model.Dispose();
    }
}
