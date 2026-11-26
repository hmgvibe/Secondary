using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Secondary.Core;

namespace Secondary.App.Presentation;

// Real shell icons for shortcut targets: executables and .lnk files show the program's own icon
// (a .lnk is resolved first so the small shortcut arrow never appears), folders show the folder
// icon, other files show their file-type icon. Jumbo (256 px) images are requested so the home
// widget stays sharp on high-DPI screens. Websites have no local icon, so callers draw an initial
// badge instead — see FallbackBrush and Initial.
internal static class ShortcutIcons
{
    private static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    // Distinct hues that stay readable on both the light and dark theme.
    private static readonly Color[] palette =
    [
        Color.FromRgb(0x4C, 0x7D, 0xD9), Color.FromRgb(0xE0, 0x7A, 0x43), Color.FromRgb(0x3C, 0xA5, 0x76),
        Color.FromRgb(0xA9, 0x65, 0xD0), Color.FromRgb(0xD1, 0x5B, 0x77), Color.FromRgb(0x2E, 0x97, 0xB0),
        Color.FromRgb(0xC2, 0x9B, 0x2E), Color.FromRgb(0x71, 0x83, 0x9B),
    ];

    public static ImageSource? For(ShortcutEntry entry)
    {
        var target = entry.Steps is { Length: > 0 } ? entry.Steps[0].Target : null;
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return null;
        if (!Path.IsPathRooted(target)) return null;
        lock (cache)
        {
            if (cache.TryGetValue(target, out var cached)) return cached;
            var icon = Load(target);
            cache[target] = icon;
            return icon;
        }
    }

    public static bool IsWebsite(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // First meaningful character of a name, used on the fallback badge.
    public static string Initial(string name)
    {
        foreach (var rune in name.Trim())
        {
            if (char.IsWhiteSpace(rune)) continue;
            return char.ToUpperInvariant(rune).ToString();
        }
        return "?";
    }

    public static Brush FallbackBrush(string name)
    {
        var hash = 17;
        foreach (var c in name) hash = unchecked(hash * 31 + c);
        var brush = new SolidColorBrush(palette[(hash & int.MaxValue) % palette.Length]);
        brush.Freeze();
        return brush;
    }

    private static ImageSource? Load(string path)
    {
        try
        {
            var folder = IsDirectory(path);
            var file = !folder && IsFile(path);
            // A .lnk resolves to its target so the tile shows the program icon, not the link overlay.
            var resolved = file ? ResolveLinkTarget(path) : path;
            if (!string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase))
            {
                folder = IsDirectory(resolved);
                file = !folder && IsFile(resolved);
            }
            var attributes = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            // A missing target still gets the icon of its file type instead of the generic blank icon.
            var byType = !folder && !file;
            return FromImageList(resolved, attributes, byType) ?? FromLargeIcon(resolved, attributes, byType);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExternalException or COMException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsDirectory(string path) { try { return Directory.Exists(path); } catch (Exception) { return false; } }
    private static bool IsFile(string path) { try { return File.Exists(path); } catch (Exception) { return false; } }

    private static string ResolveLinkTarget(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return path;
            dynamic shell = Activator.CreateInstance(type)!;
            var target = (string)shell.CreateShortcut(path).TargetPath;
            return string.IsNullOrWhiteSpace(target) ? path : target;
        }
        catch (Exception) { return path; }
    }

    private static ImageSource? FromImageList(string path, uint attributes, bool byType)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_SYSICONINDEX | (byType ? SHGFI_USEFILEATTRIBUTES : 0u);
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero) return null;
        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(SHIL_JUMBO, ref iid, out var list) != 0 || list is null) return null;
        var icon = IntPtr.Zero;
        try
        {
            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref icon) != 0 || icon == IntPtr.Zero) return null;
            return ToImage(icon);
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (Marshal.IsComObject(list)) Marshal.ReleaseComObject(list);
        }
    }

    private static ImageSource? FromLargeIcon(string path, uint attributes, bool byType)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON | (byType ? SHGFI_USEFILEATTRIBUTES : 0u);
        if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero) return null;
        if (info.hIcon == IntPtr.Zero) return null;
        try { return ToImage(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    private static ImageSource ToImage(IntPtr icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private const uint SHGFI_ICON = 0x0000_0100;
    private const uint SHGFI_LARGEICON = 0x0000_0000;
    private const uint SHGFI_SYSICONINDEX = 0x0000_4000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x0000_0010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x0000_0080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0000_0010;
    private const int SHIL_JUMBO = 0x4;
    private const int ILD_TRANSPARENT = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    // The shell's image list, used to pull the 256 px icon behind a system icon index.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, ref int replaced);
        [PreserveSig] int SetOverlayImage(int index, int overlay);
        [PreserveSig] int Replace(int index, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int mask, ref int index);
        [PreserveSig] int Draw(IntPtr drawParams);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, int flags, ref IntPtr icon);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int list, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageList imageList);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
