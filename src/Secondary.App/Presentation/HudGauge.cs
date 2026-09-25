using System;
using System.Windows;
using System.Windows.Media;

namespace Secondary.App.Presentation;

// Segmented 270-degree instrument. Static preview values, no timer or animation.
public sealed class HudGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(HudGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(HudGauge), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 3;
        if (radius <= 10) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var value = double.IsFinite(Value) ? Math.Clamp(Value,0,100) : 0;
        for (var i = 0; i <= 40; i++)
        {
            var a = (135 + i * 270d / 40) * Math.PI / 180;
            var inner = radius - (i % 5 == 0 ? 10 : 6);
            dc.PushOpacity(double.IsFinite(Value) && i <= value * .4 ? .95 : .16);
            dc.DrawLine(new Pen(Stroke, 2), new(center.X+Math.Cos(a)*inner,center.Y+Math.Sin(a)*inner),
                new(center.X+Math.Cos(a)*radius,center.Y+Math.Sin(a)*radius));
            dc.Pop();
        }
    }
}
