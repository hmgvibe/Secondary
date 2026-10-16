using System;
using System.Collections.Generic;
using System.Windows;

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

// Pointer geometry only. The frame owns persistence, interaction and guide visuals.
public static class HomeWidgetGeometry
{
    public static Rect Resize(Rect start, Vector delta, ResizeEdges edges, Size minimum, Size canvas, Size? maximum = null)
    {
        var canvasWidth = CanvasExtent(canvas.Width);
        var canvasHeight = CanvasExtent(canvas.Height);
        var bounds = WithinCanvas(start, canvasWidth, canvasHeight);
        var left = bounds.Left; var right = bounds.Right;
        var top = bounds.Top; var bottom = bounds.Bottom;
        var widthLimit = SizeLimit(maximum?.Width ?? double.PositiveInfinity, double.PositiveInfinity);
        var heightLimit = SizeLimit(maximum?.Height ?? double.PositiveInfinity, double.PositiveInfinity);
        var minWidth = SizeLimit(minimum.Width, 0);
        var minHeight = SizeLimit(minimum.Height, 0);
        var horizontal = edges & (ResizeEdges.Left | ResizeEdges.Right);
        var vertical = edges & (ResizeEdges.Top | ResizeEdges.Bottom);

        // Conflicting opposite flags leave that axis unchanged. Every real handle
        // selects one edge per axis. Canvas/maximum bounds take precedence if the
        // available space cannot accommodate the requested minimum.
        if (horizontal == ResizeEdges.Left)
        {
            var largest = Math.Min(right, widthLimit);
            var smallest = Math.Min(minWidth, largest);
            left = Math.Clamp(left + FiniteDelta(delta.X), right - largest, right - smallest);
        }
        else if (horizontal == ResizeEdges.Right)
        {
            var largest = Math.Min(canvasWidth - left, widthLimit);
            var smallest = Math.Min(minWidth, largest);
            right = Math.Clamp(right + FiniteDelta(delta.X), left + smallest, left + largest);
        }
        if (vertical == ResizeEdges.Top)
        {
            var largest = Math.Min(bottom, heightLimit);
            var smallest = Math.Min(minHeight, largest);
            top = Math.Clamp(top + FiniteDelta(delta.Y), bottom - largest, bottom - smallest);
        }
        else if (vertical == ResizeEdges.Bottom)
        {
            var largest = Math.Min(canvasHeight - top, heightLimit);
            var smallest = Math.Min(minHeight, largest);
            bottom = Math.Clamp(bottom + FiniteDelta(delta.Y), top + smallest, top + largest);
        }
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static WidgetSnapResult Snap(Rect bounds, Size canvas, IReadOnlyList<Rect> peers, ResizeEdges edges, bool moving, double threshold = 8,
        Size? minimum = null, Size? maximum = null)
    {
        if (!FiniteBounds(bounds)) return new(Rect.Empty, null, null);
        var width = CanvasExtent(canvas.Width); var height = CanvasExtent(canvas.Height);
        var tolerance = double.IsFinite(threshold) ? Math.Max(0, threshold) : 0;
        var horizontal = edges & (ResizeEdges.Left | ResizeEdges.Right);
        var vertical = edges & (ResizeEdges.Top | ResizeEdges.Bottom);
        bool AllowedResize(double adjustment, bool xAxis)
        {
            var edge = xAxis ? horizontal : vertical;
            var resized = Resize(bounds, xAxis ? new Vector(adjustment, 0) : new Vector(0, adjustment), edge,
                minimum ?? new Size(), canvas, maximum);
            var actual = xAxis ? edge == ResizeEdges.Left ? resized.Left : resized.Right : edge == ResizeEdges.Top ? resized.Top : resized.Bottom;
            var target = (xAxis ? edge == ResizeEdges.Left ? bounds.Left : bounds.Right : edge == ResizeEdges.Top ? bounds.Top : bounds.Bottom) + adjustment;
            return Math.Abs(actual - target) < 1e-8;
        }
        var xs = moving ? new[] { bounds.Left, bounds.Left + bounds.Width / 2, bounds.Right }
            : horizontal == ResizeEdges.Left ? new[] { bounds.Left }
            : horizontal == ResizeEdges.Right ? new[] { bounds.Right } : Array.Empty<double>();
        var ys = moving ? new[] { bounds.Top, bounds.Top + bounds.Height / 2, bounds.Bottom }
            : vertical == ResizeEdges.Top ? new[] { bounds.Top }
            : vertical == ResizeEdges.Bottom ? new[] { bounds.Bottom } : Array.Empty<double>();
        var x = Nearest(xs, width, peers, true, tolerance, adjustment => moving
            ? bounds.Left + adjustment >= 0 && bounds.Right + adjustment <= width
            : (horizontal == ResizeEdges.Left ? bounds.Left + adjustment < bounds.Right : bounds.Right + adjustment > bounds.Left) && AllowedResize(adjustment, true));
        var y = Nearest(ys, height, peers, false, tolerance, adjustment => moving
            ? bounds.Top + adjustment >= 0 && bounds.Bottom + adjustment <= height
            : (vertical == ResizeEdges.Top ? bounds.Top + adjustment < bounds.Bottom : bounds.Bottom + adjustment > bounds.Top) && AllowedResize(adjustment, false));
        var left = bounds.Left; var right = bounds.Right;
        var top = bounds.Top; var bottom = bounds.Bottom;
        if (x is { } snapX)
        {
            if (moving) { left += snapX.Adjustment; right += snapX.Adjustment; }
            else if (horizontal == ResizeEdges.Left) left += snapX.Adjustment;
            else if (horizontal == ResizeEdges.Right) right += snapX.Adjustment;
        }
        if (y is { } snapY)
        {
            if (moving) { top += snapY.Adjustment; bottom += snapY.Adjustment; }
            else if (vertical == ResizeEdges.Top) top += snapY.Adjustment;
            else if (vertical == ResizeEdges.Bottom) bottom += snapY.Adjustment;
        }
        return new(new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)), x?.Guide, y?.Guide);
    }

    private readonly record struct Candidate(double Adjustment, double Guide, int TargetPriority, int SourcePriority);
    private static Candidate? Nearest(double[] sources, double extent, IReadOnlyList<Rect> peers, bool horizontal, double threshold, Func<double, bool> allowed)
    {
        Candidate? best = null;
        void Consider(double coordinate, int targetPriority)
        {
            if (!double.IsFinite(coordinate) || coordinate < 0 || coordinate > extent) return;
            for (var i = 0; i < sources.Length; i++)
            {
                var adjustment = coordinate - sources[i];
                if (Math.Abs(adjustment) > threshold || !allowed(adjustment)) continue;
                var candidate = new Candidate(adjustment, coordinate, targetPriority, i);
                if (best is not { } current || Compare(candidate, current) < 0) best = candidate;
            }
        }
        Consider(0, 0); Consider(extent / 2, 0); Consider(extent, 0);
        foreach (var peer in peers)
        {
            if (!FiniteBounds(peer) || peer.Width <= 0 || peer.Height <= 0) continue;
            var start = horizontal ? peer.Left : peer.Top;
            var length = horizontal ? peer.Width : peer.Height;
            Consider(start, 1); Consider(start + length / 2, 1); Consider(start + length, 1);
        }
        return best;
    }
    private static int Compare(Candidate first, Candidate second)
    {
        var distance = Math.Abs(first.Adjustment).CompareTo(Math.Abs(second.Adjustment));
        if (distance != 0) return distance;
        var priority = first.TargetPriority.CompareTo(second.TargetPriority);
        if (priority != 0) return priority;
        var coordinate = first.Guide.CompareTo(second.Guide);
        return coordinate != 0 ? coordinate : first.SourcePriority.CompareTo(second.SourcePriority);
    }
    private static Rect WithinCanvas(Rect bounds, double width, double height)
    {
        if (!FiniteBounds(bounds)) return new(0, 0, 0, 0);
        var left = Math.Clamp(bounds.Left, 0, width); var right = Math.Clamp(bounds.Right, left, width);
        var top = Math.Clamp(bounds.Top, 0, height); var bottom = Math.Clamp(bounds.Bottom, top, height);
        return new(left, top, right - left, bottom - top);
    }
    private static bool FiniteBounds(Rect bounds) => !bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) && bounds.Width >= 0 && bounds.Height >= 0;
    private static double CanvasExtent(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double SizeLimit(double value, double fallback) => double.IsNaN(value) || double.IsNegativeInfinity(value) ? fallback : Math.Max(0, value);
    private static double FiniteDelta(double value) => double.IsFinite(value) ? value : 0;
}
