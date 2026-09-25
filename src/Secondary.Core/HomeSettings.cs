using System.Globalization;

namespace Secondary.Core;

public sealed record HomeSettings(string ClockStyle = "Digital", double Size = 1,
    string Color = "Auto", double X = .5, double Y = .5, bool Use24Hours = true,
    bool ShowSeconds = false, bool ShowDate = true, string Background = "Static", string? MediaPath = null,
    HomeWidgetSettings[]? Widgets = null, double? ClockWidthScale = null, double? ClockHeightScale = null)
{
    public HomeSettings Normalize() => this with
    {
        ClockStyle = ClockStyle is "Flip" or "Analog" ? ClockStyle : "Digital",
        Size = Bound(Size, .3, 3, 1), X = Bound(X, 0, 1, .5), Y = Bound(Y, 0, 1, .5),
        ClockWidthScale = ClockWidthScale is double w ? Bound(w,.3,3,1) : null,
        ClockHeightScale = ClockHeightScale is double h ? Bound(h,.3,3,1) : null,
        Color = ValidColor(Color) ? Color : "Auto",
        Background = Background is "Image" or "Video" or "Gradient" or "Aurora" ? Background : "Static",
        MediaPath = IsLocalPath(MediaPath) ? MediaPath : null,
        Widgets = Widgets?.Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Id) && HomeWidgetSettings.Supported(w.Kind))
            .DistinctBy(w => w.Id).Take(12).Select(w => w.Normalize()).ToArray()
    };
    private static double Bound(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    public static bool ValidColor(string? color) => color == "Auto" ||
        (color is { Length: 7 } && color[0] == '#' && uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _));
    public static bool IsLocalPath(string? path) => path is { Length: > 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
    public string TimeText(DateTime time) => time.ToString((Use24Hours ? "HH:mm" : "hh:mm") + (ShowSeconds ? ":ss" : ""), CultureInfo.InvariantCulture);
}
