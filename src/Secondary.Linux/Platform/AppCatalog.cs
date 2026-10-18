using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Secondary.Linux.Platform;

public sealed record InstalledApp(string Id, string Name, string DesktopFile, string? Icon, string? Comment);

// Installed applications as GNOME lists them: .desktop files from the XDG data directories (plus the
// Flatpak and Snap exports), where a user's entry shadows a system entry with the same desktop ID.
public sealed class AppCatalog(Func<string, string?> environment, string home, Func<string, bool>? onPath = null, IReadOnlyList<string>? extraDirectories = null)
{
    // Sessions started without the Flatpak or Snap profile scripts still show those apps.
    private readonly IReadOnlyList<string> extraDirectories = extraDirectories ?? [Path.Combine(home, ".local", "share", "flatpak", "exports", "share"), "/var/lib/flatpak/exports/share", "/var/lib/snapd/desktop"];
    private readonly Func<string, bool> onPath = onPath ?? FindOnPath;
    public static AppCatalog Current { get; } = new(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public IReadOnlyList<string> DataDirectories()
    {
        string Absolute(string? value, string fallback) => !string.IsNullOrEmpty(value) && value.StartsWith('/') ? value : fallback;
        var directories = new List<string> { Absolute(environment("XDG_DATA_HOME"), Path.Combine(home, ".local", "share")) };
        var system = environment("XDG_DATA_DIRS");
        directories.AddRange((string.IsNullOrEmpty(system) ? "/usr/local/share:/usr/share" : system).Split(':', StringSplitOptions.RemoveEmptyEntries).Where(d => d.StartsWith('/')));
        directories.AddRange(extraDirectories);
        return directories.Select(d => d.TrimEnd('/')).Distinct().ToArray();
    }

    public IReadOnlyList<InstalledApp> Load()
    {
        var desktops = (environment("XDG_CURRENT_DESKTOP") ?? "GNOME").Split(':', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apps = new List<InstalledApp>();
        foreach (var data in DataDirectories())
        {
            var root = Path.Combine(data, "applications");
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.desktop", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                // The desktop ID is the path below applications/, with '/' turned into '-'.
                var id = Path.GetRelativePath(root, file).Replace('/', '-');
                if (!seen.Add(id)) continue;
                if (Read(file, id, desktops) is { } app) apps.Add(app);
            }
        }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal InstalledApp? Read(string file, string id, string[] desktops)
    {
        Dictionary<string, string> values;
        try
        {
            if (new FileInfo(file).Length > 65536) return null;
            values = DesktopEntry(File.ReadLines(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
        string[] List(string key) => (Get(key) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (Get("Type") != "Application" || Get("NoDisplay") == "true" || Get("Hidden") == "true") return null;
        if (List("OnlyShowIn") is { Length: > 0 } only && !only.Intersect(desktops).Any()) return null;
        if (List("NotShowIn").Intersect(desktops).Any()) return null;
        // TryExec names a program that must exist; otherwise the app has been removed.
        if (Get("TryExec") is { Length: > 0 } tryExec && !(tryExec.StartsWith('/') ? File.Exists(tryExec) : onPath(tryExec))) return null;
        if (Get("Exec") is not { Length: > 0 } && Get("DBusActivatable") != "true") return null;
        var name = Localized(values, "Name");
        return string.IsNullOrWhiteSpace(name) ? null : new InstalledApp(id, name, file, Get("Icon"), Localized(values, "Comment"));
    }
    private static string? Localized(Dictionary<string, string> values, string key) =>
        new[] { key + "[zh_TW]", key + "[zh_HK]", key, key + "[zh_CN]", key + "[zh]" }.Select(k => values.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    public static Dictionary<string, string> DesktopEntry(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { if (section) break; section = line == "[Desktop Entry]"; continue; }
            if (!section || line.StartsWith('#')) continue;
            var equals = line.IndexOf('=');
            if (equals > 0) values.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
        }
        return values;
    }
    private static bool FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(d => File.Exists(Path.Combine(d, program)));

    // Icon lookup following the icon theme layout: the current theme, then the common GNOME themes
    // and hicolor; scalable SVG first, then the largest PNG; finally pixmaps.
    private readonly ConcurrentDictionary<string, string?> iconPaths = new(StringComparer.Ordinal);
    // The user's GNOME icon theme, when the desktop portal reports it.
    public string? Theme { get; set; }
    public string? IconPath(string? icon, string? theme = null)
    {
        theme ??= Theme;
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (icon.StartsWith('/')) return File.Exists(icon) ? icon : null;
        return iconPaths.GetOrAdd((theme ?? "") + "|" + icon, _ =>
        {
            var roots = DataDirectories().Select(d => Path.Combine(d, "icons")).Prepend(Path.Combine(home, ".icons")).ToArray();
            var themes = new[] { theme, "Yaru", "Adwaita", "hicolor" }.Where(t => !string.IsNullOrEmpty(t)).Distinct();
            var sizes = new[] { "scalable", "512x512", "256x256", "192x192", "128x128", "96x96", "64x64", "48x48", "32x32" };
            foreach (var t in themes)
            foreach (var size in sizes)
            foreach (var root in roots)
            foreach (var extension in size == "scalable" ? new[] { ".svg" } : [".png", ".svg"])
            {
                var candidate = Path.Combine(root, t!, size, "apps", icon + extension);
                if (File.Exists(candidate)) return candidate;
            }
            foreach (var root in DataDirectories().Select(d => Path.Combine(d, "pixmaps")).Append("/usr/share/pixmaps"))
            foreach (var extension in new[] { ".svg", ".png" })
                if (File.Exists(Path.Combine(root, icon + extension))) return Path.Combine(root, icon + extension);
            return null;
        });
    }

    // Icons decode off the UI thread; SVG renders through Svg.Skia at the requested size.
    private readonly ConcurrentDictionary<(string, int), Task<Bitmap?>> bitmaps = new();
    public Task<Bitmap?> LoadIconAsync(string? path, int size) =>
        path is null ? Task.FromResult<Bitmap?>(null) : bitmaps.GetOrAdd((path, size), key => Task.Run(() => Render(key.Item1, key.Item2)));
    public static Bitmap? Render(string path, int size)
    {
        try
        {
            if (new FileInfo(path).Length > 2_000_000) return null;
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var svg = new Svg.Skia.SKSvg();
                if (svg.Load(path) is not { } picture) return null;
                var bounds = picture.CullRect;
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                var scale = Math.Min(size / bounds.Width, size / bounds.Height);
                canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
                using var image = surface.Snapshot();
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = png.AsStream();
                return new Bitmap(stream);
            }
            using var file = File.OpenRead(path);
            return Bitmap.DecodeToWidth(file, size, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException or System.Xml.XmlException) { return null; }
    }
}
