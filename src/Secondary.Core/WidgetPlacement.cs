namespace Secondary.Core;

// Relative position within the remaining canvas space; scale is independent of display DPI.
public sealed record WidgetPlacement(double X = .5, double Y = .5, double Scale = 1, double? WidthScale = null, double? HeightScale = null)
{
    public WidgetPlacement Normalize() => new(Clean(X,0,1,.5), Clean(Y,0,1,.5), Clean(Scale,.3,3,1),
        WidthScale is double w ? Clean(w,.3,3,1) : null, HeightScale is double h ? Clean(h,.3,3,1) : null);
    private static double Clean(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value,min,max) : fallback;
}
