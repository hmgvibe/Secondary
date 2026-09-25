namespace Secondary.Core;

public static class ShortcutImport
{
    public static ShortcutEntry FromPath(string input, string group)
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
        if (string.IsNullOrWhiteSpace(name)) name = path;
        var kind = !folder && extension is ".ps1" or ".bat" or ".cmd" ? "Script" : "Open";
        var entry = new ShortcutEntry(Guid.NewGuid().ToString("N"),name[..Math.Min(name.Length,80)],group,folder ? "▣" : "↗",[new(kind,target)]);
        if (ShortcutPolicy.Validate(entry) is { } error) throw new InvalidDataException(error);
        return entry;
    }
}
