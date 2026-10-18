using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Secondary.Linux;

public enum ThemeChoice { System, Light, Dark }

// Same tokens and values as the WPF ThemeService, so both versions read alike.
public static class Palette
{
    public static void Apply(IResourceDictionary resources, bool dark, bool game, bool highContrast)
    {
        void Brush(string key, string hex) => resources[key] = new ImmutableSolidColorBrush(Color.Parse(hex));
        Brush("CanvasBrush", dark ? game ? "#090D10" : "#111B29" : "#E7EDF5");
        Brush("SurfaceBrush", dark ? game ? "#11171B" : "#C9223041" : "#D9FFFFFF");
        Brush("SheetBrush", dark ? "#141E27" : "#F5F8FC");
        Brush("InsetBrush", dark ? game ? "#0C1216" : "#80202C3C" : "#99DCE6F0");
        Brush("TextBrush", dark ? "#EFF4F7" : "#162A37");
        Brush("MutedBrush", dark ? "#BDCDD6" : "#445A69");
        Brush("LineBrush", dark ? game ? "#283C49" : "#425365" : "#BAC9D2");
        Brush("HoverBrush", dark ? "#354455" : "#D1DEE8");
        Brush("SelectedBrush", dark ? "#304D51" : "#C3E3DF");
        Brush("AccentBrush", game ? "#65DCEC" : dark ? "#A8C6E1" : "#46657F");
        Brush("RailBrush", "#222329");
        Brush("RailBorderBrush", "#414249");
        Brush("RailButtonBrush", "#303139");
        Brush("RailHoverBrush", "#41424C");
        Brush("RailIconBrush", "#F4F4F7");
        Brush("RailSelectedBrush", "#FFFFFF");
        Brush("RailSelectedIconBrush", "#08090B");
        resources["PanelRadius"] = new CornerRadius(game ? 4 : 18);
        resources["ControlRadius"] = new CornerRadius(game ? 4 : 10);
        if (!highContrast) return;
        // GNOME high contrast has no system palette to borrow; use maximum contrast for the active brightness.
        var back = dark ? "#000000" : "#FFFFFF";
        var fore = dark ? "#FFFFFF" : "#000000";
        foreach (var key in new[] { "CanvasBrush", "SurfaceBrush", "InsetBrush", "SheetBrush", "RailBrush", "RailButtonBrush" }) Brush(key, back);
        foreach (var key in new[] { "TextBrush", "MutedBrush", "AccentBrush", "LineBrush", "RailBorderBrush", "RailIconBrush" }) Brush(key, fore);
        Brush("RailHoverBrush", dark ? "#333333" : "#DDDDDD");
        Brush("RailSelectedBrush", fore);
        Brush("RailSelectedIconBrush", back);
    }
}
