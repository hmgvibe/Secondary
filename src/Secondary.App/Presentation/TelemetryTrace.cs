using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Secondary.Core;

namespace Secondary.App.Presentation;

public sealed class TelemetryTrace : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(TelemetryTrace),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Dashed { get; set; }
    public double Maximum { get; set; }
    private TracePoint[] points = Array.Empty<TracePoint>();
    public void Update(TracePoint[] values) { points = values; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (points.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var end = points[^1].Seconds;
        var maximum = Maximum > 0 ? Maximum : Math.Max(1, points.Max(p => p.Value ?? 0) * 1.1);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var connected = false;
            foreach (var point in points)
            {
                if (!point.Value.HasValue) { connected = false; continue; }
                var p = new Point(Math.Clamp((point.Seconds - end + 60) / 60, 0, 1) * ActualWidth,
                    (1 - Math.Clamp(point.Value.Value / maximum, 0, 1)) * ActualHeight);
                if (!connected) context.BeginFigure(p, false, false); else context.LineTo(p, true, false);
                connected = true;
            }
        }
        geometry.Freeze();
        var pen = new Pen(Stroke, 1.6);
        if (Dashed) pen.DashStyle = DashStyles.Dash;
        dc.DrawGeometry(null, pen, geometry);
    }
}
