using System;
using System.Windows;
using System.Windows.Media;

namespace Secondary.App.Presentation;

// Static vector decoration, redrawn only on layout/theme changes. No render timer.
public sealed class HudGrid : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(HudGrid),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w < 40 || h < 40) return;
        dc.PushOpacity(.055);
        var fine = new Pen(Stroke, .5);
        for (double x = 24; x < w; x += 48) dc.DrawLine(fine, new(x,0), new(x,h));
        for (double y = 24; y < h; y += 48) dc.DrawLine(fine, new(0,y), new(w,y));
        dc.Pop();
        dc.PushOpacity(.45);
        var accent = new Pen(Stroke, 1);
        foreach (var x in new[] { 16.0, w - 16 })
        foreach (var y in new[] { 16.0, h - 16 })
        {
            var dx = x < w / 2 ? 24 : -24; var dy = y < h / 2 ? 24 : -24;
            dc.DrawLine(accent, new(x,y), new(x+dx,y));
            dc.DrawLine(accent, new(x,y), new(x,y+dy));
        }
        for (double y = 64; y < h-48; y += 24)
            dc.DrawLine(fine, new(w-16,y), new(w-22,y));
        dc.Pop();
    }
}
