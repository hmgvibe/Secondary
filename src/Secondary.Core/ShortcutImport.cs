namespace Secondary.Core;

public static class ShortcutImport
{
    public static ShortcutEntry FromPath(string input, string group) => FromPath(input, group, ShortcutRules.Windows);
    public static ShortcutEntry FromPath(string input, string group, ShortcutRules rules)
    {
        if (!HomeSettings.IsLocalPath(input)) throw new InvalidDataException("只支援本機路徑。");
        var path = Path.GetFullPath(input);
        var folder = Directory.Exists(path);
        if (!folder && !File.Exists(path)) throw new FileNotFoundException("目標不存在。",path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var target = path;
        if (!folder && extension == ".url")
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("網際網路捷徑過大。");
            var section = false; string? address = null;
            foreach (var line in File.ReadAllLines(path))
            {
                var text = line.Trim();
                if (text.StartsWith('[')) section = text.Equals("[InternetShortcut]",StringComparison.OrdinalIgnoreCase);
                else if (section && text.StartsWith("URL=",StringComparison.OrdinalIgnoreCase)) { address = text[4..].Trim(); break; }
            }
            if (!Uri.TryCreate(address,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("網際網路捷徑只支援 HTTP／HTTPS。");
            target = uri.AbsoluteUri;
        }
        var name = folder ? Path.GetFileName(path.TrimEnd('\\','/')) : Path.GetFileNameWithoutExtension(path);
        // A Linux application launcher names itself; prefer that over its file name.
        if (!folder && extension == ".desktop") name = DesktopEntryName(path) ?? name;
        if (string.IsNullOrWhiteSpace(name)) name = path;
        var kind = !folder && rules.ScriptExtensions.Contains(extension) ? "Script" : "Open";
        var entry = new ShortcutEntry(Guid.NewGuid().ToString("N"),name[..Math.Min(name.Length,80)],group,folder ? "▣" : "↗",[new(kind,target)]);
        if (ShortcutPolicy.Validate(entry, rules) is { } error) throw new InvalidDataException(error);
        return entry;
    }

    // Reads Name (preferring Traditional Chinese) from the [Desktop Entry] group of a bounded .desktop file.
    public static string? DesktopEntryName(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("應用程式啟動器過大。");
        var section = false; string? plain = null, chinese = null, traditional = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { if (section) break; section = text == "[Desktop Entry]"; continue; }
            if (!section) continue;
            if (text.StartsWith("Name=", StringComparison.Ordinal)) plain ??= text[5..].Trim();
            else if (text.StartsWith("Name[zh_TW]=", StringComparison.Ordinal)) traditional ??= text[12..].Trim();
            else if (text.StartsWith("Name[zh_HK]=", StringComparison.Ordinal)) traditional ??= text[12..].Trim();
            else if (text.StartsWith("Name[zh_CN]=", StringComparison.Ordinal) || text.StartsWith("Name[zh]=", StringComparison.Ordinal)) chinese ??= text[(text.IndexOf('=') + 1)..].Trim();
        }
        var name = traditional ?? plain ?? chinese;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
