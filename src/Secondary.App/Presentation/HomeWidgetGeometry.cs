using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Secondary.Shared;

namespace Secondary.App.Presentation;

[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

public readonly record struct WidgetSnapResult(Rect Bounds, double? GuideX, double? GuideY);

// WPF adapter over the shared geometry, which the Linux shell uses unchanged.
public static class HomeWidgetGeometry
{
    public static Rect Resize(Rect start, Vector delta, ResizeEdges edges, Size minimum, Size canvas, Size? maximum = null) =>
        ToWpf(WidgetGeometry.Resize(ToShared(start), new(delta.X, delta.Y), (WidgetEdges)edges, ToShared(minimum), ToShared(canvas), maximum is { } m ? ToShared(m) : null));

    public static WidgetSnapResult Snap(Rect bounds, Size canvas, IReadOnlyList<Rect> peers, ResizeEdges edges, bool moving, double threshold = 8,
        Size? minimum = null, Size? maximum = null)
    {
        var result = WidgetGeometry.Snap(ToShared(bounds), ToShared(canvas), peers.Select(ToShared).ToArray(), (WidgetEdges)edges, moving, threshold,
            minimum is { } min ? ToShared(min) : null, maximum is { } max ? ToShared(max) : null);
        return new(ToWpf(result.Bounds), result.GuideX, result.GuideY);
    }

    internal static LayoutRect ToShared(Rect rect) => rect.IsEmpty ? LayoutRect.Empty : new(rect.X, rect.Y, rect.Width, rect.Height);
    internal static Rect ToWpf(LayoutRect rect) => rect.IsEmpty ? Rect.Empty : new(rect.X, rect.Y, rect.Width, rect.Height);
    internal static LayoutSize ToShared(Size size) => size.IsEmpty ? new(double.NegativeInfinity, double.NegativeInfinity) : new(size.Width, size.Height);
}
