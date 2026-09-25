using System;
using System.Windows;
using System.Windows.Media;

namespace Secondary.App.Presentation;

// Deterministic, static DESIGN FIXTURE. No timer, telemetry or game connection.
public sealed class PreviewTrace : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(PreviewTrace), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Seed { get; set; } = 1;
    public bool Dashed { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(24, 130, 180, 200)), 1);
        for (var i = 1; i < 5; i++) dc.DrawLine(grid, new Point(0, h * i / 5), new Point(w, h * i / 5));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i <= 100; i++)
            {
                var y = h * (0.63 + Math.Sin(i * 1.7 + Seed) * .04 + Math.Cos(i * .31 + Seed) * .025);
                if ((i + Seed * 7) % 29 == 0) y -= h * .32;
                var p = new Point(w * i / 100, Math.Clamp(y, 2, Math.Max(2, h - 2)));
                if (i == 0) context.BeginFigure(p, false, false); else context.LineTo(p, true, false);
            }
        }
        geometry.Freeze();
        var tracePen = new Pen(Stroke, 1.6);
        if (Dashed) tracePen.DashStyle = DashStyles.Dash;
        dc.DrawGeometry(null, tracePen, geometry);
    }
}
