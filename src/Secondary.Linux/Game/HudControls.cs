using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Secondary.Core;

namespace Secondary.Linux.Game;

// Segmented 270-degree instrument, drawn only when its value changes. NaN draws an empty gauge.
public sealed class HudGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<HudGauge, double>(nameof(Value), double.NaN);
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<HudGauge, IBrush?>(nameof(Stroke), Brushes.Cyan);
    static HudGauge() => AffectsRender<HudGauge>(ValueProperty, StrokeProperty);
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public override void Render(DrawingContext context)
    {
        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;
        if (radius <= 10 || Stroke is null) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var known = double.IsFinite(Value);
        var value = known ? Math.Clamp(Value, 0, 100) : 0;
        var pen = new Pen(Stroke, 2);
        for (var i = 0; i <= 40; i++)
        {
            var angle = (135 + i * 270d / 40) * Math.PI / 180;
            var inner = radius - (i % 5 == 0 ? 10 : 6);
            using (context.PushOpacity(known && i <= value * .4 ? .95 : .16))
                context.DrawLine(pen, new(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner),
                    new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius));
        }
    }
}

// Last 60 seconds as a line; gaps (null values) break the line instead of drawing zero.
public sealed class TelemetryTrace : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<TelemetryTrace, IBrush?>(nameof(Stroke), Brushes.Cyan);
    static TelemetryTrace() => AffectsRender<TelemetryTrace>(StrokeProperty);
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool Dashed { get; set; }
    public double Maximum { get; set; }
    public TracePoint[] Points { get; private set; } = [];
    public void Update(TracePoint[] values) { Points = values; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        if (Points.Length == 0 || Bounds.Width <= 0 || Bounds.Height <= 0 || Stroke is null) return;
        var end = Points[^1].Seconds;
        var maximum = Maximum > 0 ? Maximum : Math.Max(1, Points.Max(p => p.Value ?? 0) * 1.1);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            var connected = false;
            foreach (var point in Points)
            {
                if (!point.Value.HasValue) { if (connected) figure.EndFigure(false); connected = false; continue; }
                var p = new Point(Math.Clamp((point.Seconds - end + 60) / 60, 0, 1) * Bounds.Width,
                    (1 - Math.Clamp(point.Value.Value / maximum, 0, 1)) * Bounds.Height);
                if (!connected) figure.BeginFigure(p, false); else figure.LineTo(p);
                connected = true;
            }
            if (connected) figure.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Stroke, 1.6, Dashed ? new DashStyle([4, 3], 0) : null), geometry);
    }
}

// Static grid and corner brackets behind the charts; no timer.
public sealed class HudGrid : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<HudGrid, IBrush?>(nameof(Stroke), Brushes.Cyan);
    static HudGrid() => AffectsRender<HudGrid>(StrokeProperty);
    public HudGrid() => IsHitTestVisible = false;
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w < 40 || h < 40 || Stroke is null) return;
        var fine = new Pen(Stroke, .5);
        using (context.PushOpacity(.055))
        {
            for (double x = 24; x < w; x += 48) context.DrawLine(fine, new(x, 0), new(x, h));
            for (double y = 24; y < h; y += 48) context.DrawLine(fine, new(0, y), new(w, y));
        }
        using (context.PushOpacity(.45))
        {
            var accent = new Pen(Stroke, 1);
            foreach (var x in new[] { 16.0, w - 16 })
            foreach (var y in new[] { 16.0, h - 16 })
            {
                var dx = x < w / 2 ? 24 : -24; var dy = y < h / 2 ? 24 : -24;
                context.DrawLine(accent, new(x, y), new(x + dx, y));
                context.DrawLine(accent, new(x, y), new(x, y + dy));
            }
            for (double y = 64; y < h - 48; y += 24) context.DrawLine(fine, new(w - 16, y), new(w - 22, y));
        }
    }
}
