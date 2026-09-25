using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace Secondary.App.Presentation;

public sealed class ThemeService : IDisposable
{
    private readonly Window window;
    private ShellAppearance appearance;
    private ThemeChoice choice;
    public bool AnimationsEnabled { get; private set; }
    public ThemeService(Window window)
    {
        this.window = window;
        SystemEvents.UserPreferenceChanged += OnSystemChange;
        SystemParameters.StaticPropertyChanged += OnParametersChanged;
    }
    private void OnSystemChange(object sender, UserPreferenceChangedEventArgs e) => Refresh();
    private void OnParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();
    private void Refresh() => window.Dispatcher.BeginInvoke(() => Apply(appearance, choice));
    public void Apply(ShellAppearance appearance, ThemeChoice choice, bool? testDark = null, bool? testHighContrast = null)
    {
        this.appearance = appearance;
        this.choice = choice;
        var highContrast = testHighContrast ?? SystemParameters.HighContrast;
        var dark = testDark ?? (appearance == ShellAppearance.Game || choice == ThemeChoice.Dark || (choice == ThemeChoice.System && IsWindowsDark()));
        const bool light = false;
        var game = appearance == ShellAppearance.Game;
        AnimationsEnabled = !highContrast && SystemParameters.ClientAreaAnimation;
        var resources = window.Resources;
        void Brush(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze(); resources[key] = brush;
        }
        Brush("CanvasBrush", dark ? (light ? "#171B18" : game ? "#090D10" : "#111B29") : (light ? "#F1F0E9" : "#E7EDF5"));
        Brush("SurfaceBrush", dark ? (light ? "#232A24" : game ? "#11171B" : "#C9223041") : (light ? "#FAFAF4" : "#D9FFFFFF"));
        Brush("SheetBrush", dark ? "#141E27" : "#F5F8FC");
        Brush("InsetBrush", dark ? (light ? "#1D231E" : game ? "#0C1216" : "#80202C3C") : (light ? "#EAEDE4" : "#99DCE6F0"));
        Brush("TextBrush", dark ? "#EFF4F7" : "#162A37");
        Brush("MutedBrush", dark ? "#BDCDD6" : "#445A69");
        Brush("LineBrush", dark ? (game ? "#283C49" : "#425365") : "#BAC9D2");
        Brush("HoverBrush", dark ? "#354455" : "#D1DEE8");
        Brush("SelectedBrush", dark ? "#304D51" : "#C3E3DF");
        Brush("AccentBrush", game ? "#65DCEC" : dark ? "#A8C6E1" : "#46657F");
        Brush("AccentSoftBrush", dark ? "#293F40" : "#D5E7DE");
        Brush("DecorationBrush", dark ? "#3C596B" : "#A5C3CD");
        Brush("RailBrush", "#222329");
        Brush("RailBorderBrush", "#414249");
        Brush("RailButtonBrush", "#303139");
        Brush("RailHoverBrush", "#41424C");
        Brush("RailIconBrush", "#F4F4F7");
        Brush("RailSelectedBrush", "#FFFFFF");
        Brush("RailSelectedIconBrush", "#08090B");
        Brush("RailDividerBrush", "#44454D");
        resources["RailGlow"] = !game && !highContrast ? new DropShadowEffect { Color = Colors.White, BlurRadius = light ? 16 : 30, ShadowDepth = 0, Opacity = light ? 0.18 : 0.42 } : null!;
        resources["RailShadow"] = !game && !highContrast ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 4, Opacity = 0.18 } : null!;
        resources["FocusBrush"] = SystemParameters.WindowGlassBrush;
        resources["PanelRadius"] = new CornerRadius(game ? 4 : light ? 24 : 18);
        resources["ControlRadius"] = new CornerRadius(game ? 4 : 10);
        resources["NavigationRadius"] = new CornerRadius(game ? 4 : 16);
        resources["SurfaceBorder"] = new Thickness(light ? 0 : 1);
        resources["AmbientVisibility"] = !game && !highContrast ? Visibility.Visible : Visibility.Collapsed;
        if (highContrast)
        {
            resources["RailBrush"] = SystemColors.WindowBrush;
            resources["RailButtonBrush"] = SystemColors.WindowBrush;
            resources["RailBorderBrush"] = SystemColors.WindowTextBrush;
            resources["RailDividerBrush"] = SystemColors.WindowTextBrush;
            resources["RailIconBrush"] = SystemColors.WindowTextBrush;
            resources["RailHoverBrush"] = SystemColors.ControlBrush;
            resources["RailSelectedBrush"] = SystemColors.HighlightBrush;
            resources["RailSelectedIconBrush"] = SystemColors.HighlightTextBrush;
            foreach (var key in new[] { "CanvasBrush", "SurfaceBrush", "InsetBrush", "SheetBrush" }) resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "TextBrush", "MutedBrush", "AccentBrush", "LineBrush", "DecorationBrush" }) resources[key] = SystemColors.WindowTextBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
            resources["SelectedBrush"] = SystemColors.ControlBrush;
            resources["AccentSoftBrush"] = SystemColors.WindowBrush;
            resources["FocusBrush"] = SystemColors.HighlightBrush;
            resources["SurfaceBorder"] = new Thickness(1);
        }
        ApplyBackdrop(!highContrast && appearance == ShellAppearance.Normal, dark);
    }
    private static bool IsWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
    }
    private void ApplyBackdrop(bool enabled, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } composition)
            composition.RenderMode = RenderMode.Default;
        var darkFlag = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref darkFlag, sizeof(int));
        // One OS-managed backdrop, no per-panel blur. Solid surfaces are the fallback.
        var backdrop = enabled ? 2 : 1;
        var accepted = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0 && enabled;
        var margins = new Margins { Left = accepted ? -1 : 0 };
        if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0) accepted = false;
        if (accepted)
        {
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            var tint = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#E6111B29" : "#E6E7EDF5"));
            tint.Freeze();
            window.Resources["CanvasBrush"] = tint;
        }
        else window.SetResourceReference(Window.BackgroundProperty, "CanvasBrush");
    }
    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemChange;
        SystemParameters.StaticPropertyChanged -= OnParametersChanged;
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
