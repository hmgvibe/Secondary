using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Platform;
using Secondary.Core;

namespace Secondary.Linux.Platform;

public static class DisplayCatalog
{
    public static DisplayInfo[] From(IReadOnlyList<Screen> screens) => From(screens.Select(s => (s.DisplayName, s.Bounds, s.IsPrimary)).ToArray());
    // Connector names are stable across restarts; position is the fallback when the backend gives none.
    public static DisplayInfo[] From(IReadOnlyList<(string? Name, PixelRect Bounds, bool Primary)> screens) => screens.Select((s, i) =>
    {
        var named = !string.IsNullOrWhiteSpace(s.Name) && screens.Count(o => o.Name == s.Name) == 1;
        var id = named ? "name:" + s.Name : $"rect:{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}";
        return new DisplayInfo(id, named ? s.Name! : $"螢幕 {i + 1}", s.Primary, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
    }).ToArray();
}

public static class ShortcutTrigger
{
    // Settings keep the Windows-style text; the portal takes the XDG shortcuts format.
    public static string ToXdg(HotkeyGesture gesture)
    {
        var parts = new List<string>();
        if ((gesture.Modifiers & 2) != 0) parts.Add("CTRL");
        if ((gesture.Modifiers & 1) != 0) parts.Add("ALT");
        if ((gesture.Modifiers & 4) != 0) parts.Add("SHIFT");
        parts.Add(gesture.KeyName.Length == 1 ? gesture.KeyName.ToLowerInvariant() : gesture.KeyName);
        return string.Join('+', parts);
    }
}

public static class LaunchCommand
{
    // Inside an AppImage the process path points into a temporary mount; APPIMAGE is the file to run.
    public static string Program(Func<string, string?> environment, string? processPath)
    {
        var image = environment("APPIMAGE");
        return image is { Length: > 1 } && image[0] == '/' ? image : processPath ?? "secondary";
    }
    public static string ToggleGame(Func<string, string?> environment, string? processPath) => Quote(Program(environment, processPath)) + " --toggle-game";
    private static string Quote(string path) => path.All(c => char.IsLetterOrDigit(c) || "/._-+".Contains(c)) ? path : "'" + path.Replace("'", "'\\''") + "'";
}

public enum LauncherState { Missing, Current, Elsewhere, Packaged, Shadowed }

// GNOME only grants portal features such as global shortcuts to apps whose ID has an installed
// .desktop file. Writing one is the user's choice and touches only their own XDG data directory.
// A .deb install already provides one in a system data directory, so there is nothing to add.
public sealed class DesktopLauncher(string dataHome, string program, IReadOnlyList<string>? systemDirectories = null)
{
    public string Program => program;
    public string DesktopFile => Path.Combine(dataHome, "applications", DesktopPortal.ApplicationId + ".desktop");
    public string IconFile => Path.Combine(dataHome, "icons", "hicolor", "256x256", "apps", DesktopPortal.ApplicationId + ".png");

    public static DesktopLauncher FromEnvironment(Func<string, string?> environment, string home, string? processPath)
    {
        var data = environment("XDG_DATA_HOME");
        var dirs = environment("XDG_DATA_DIRS") is { Length: > 0 } list ? list : "/usr/local/share:/usr/share";
        return new(!string.IsNullOrEmpty(data) && Path.IsPathFullyQualified(data) ? data : Path.Combine(home, ".local", "share"), LaunchCommand.Program(environment, processPath),
            dirs.Split(':', StringSplitOptions.RemoveEmptyEntries).Where(d => d.StartsWith('/')).ToArray());
    }

    public LauncherState State
    {
        get
        {
            try
            {
                var packaged = (systemDirectories ?? []).Any(d => File.Exists(Path.Combine(d, "applications", DesktopPortal.ApplicationId + ".desktop")));
                if (!File.Exists(DesktopFile)) return packaged ? LauncherState.Packaged : LauncherState.Missing;
                if (File.ReadAllText(DesktopFile) == Content(program)) return LauncherState.Current;
                // An entry added from an older AppImage hides the .deb's menu item and may point at a deleted file.
                return packaged ? LauncherState.Shadowed : LauncherState.Elsewhere;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return LauncherState.Missing; }
        }
    }

    public void Install(Stream icon)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
        WriteAtomically(IconFile, target => { using var file = File.Create(target); icon.CopyTo(file); });
        WriteAtomically(DesktopFile, target => File.WriteAllText(target, Content(program)));
    }

    public void Remove()
    {
        // An entry may have been written without its icon, so only delete what is there.
        foreach (var file in new[] { DesktopFile, IconFile }) if (File.Exists(file)) File.Delete(file);
    }

    private static void WriteAtomically(string path, Action<string> write)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { write(temporary); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Content(string program) => string.Join('\n',
        "[Desktop Entry]", "Type=Application", "Name=Secondary", "Comment=副屏首頁、快捷導航與遊戲模式",
        "Exec=" + Exec(program), "Icon=" + DesktopPortal.ApplicationId, "Categories=Utility;", "StartupWMClass=secondary", "Actions=ToggleGame;", "",
        "[Desktop Action ToggleGame]", "Name=進入／退出遊戲模式", "Exec=" + Exec(program) + " --toggle-game", "");

    // Desktop Entry Specification: quote arguments with reserved characters and double % codes. The
    // quoting backslash is itself escaped by the string rule, so it is written twice (a literal
    // backslash becomes four).
    public static string Exec(string program)
    {
        var text = program.Replace("%", "%%");
        if (text.All(c => char.IsLetterOrDigit(c) || "/._-+%".Contains(c))) return text;
        var escaped = new System.Text.StringBuilder("\"");
        foreach (var c in text)
        {
            if (c == '\\') escaped.Append(@"\\\\");
            else { if (c is '"' or '`' or '$') escaped.Append(@"\\"); escaped.Append(c); }
        }
        return escaped.Append('"').ToString();
    }
}
