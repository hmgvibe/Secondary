using System;
using System.Collections.Generic;
using System.Linq;
using Secondary.Core;

namespace Secondary.Shared;

// UI-framework-neutral geometry. Semantics follow WPF's Rect so both shells behave identically.
public readonly record struct LayoutSize(double Width, double Height);
public readonly record struct LayoutVector(double X, double Y);
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public static LayoutRect Empty => new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);
    public bool IsEmpty => Width < 0;
    public double Left => X;
    public double Top => Y;
    public double Right => IsEmpty ? double.NegativeInfinity : X + Width;
    public double Bottom => IsEmpty ? double.NegativeInfinity : Y + Height;
    public bool IntersectsWith(LayoutRect other) => !IsEmpty && !other.IsEmpty &&
        other.Left <= Right && other.Right >= Left && other.Top <= Bottom && other.Bottom >= Top;
}

[Flags]
public enum WidgetEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public readonly record struct SnapResult(LayoutRect Bounds, double? GuideX, double? GuideY);

// Pointer geometry only. The frame owns persistence, interaction and guide visuals.
public static class WidgetGeometry
{
    public static LayoutRect Resize(LayoutRect start, LayoutVector delta, WidgetEdges edges, LayoutSize minimum, LayoutSize canvas, LayoutSize? maximum = null)
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
        var horizontal = edges & (WidgetEdges.Left | WidgetEdges.Right);
        var vertical = edges & (WidgetEdges.Top | WidgetEdges.Bottom);

        // Conflicting opposite flags leave that axis unchanged. Every real handle
        // selects one edge per axis. Canvas/maximum bounds take precedence if the
        // available space cannot accommodate the requested minimum.
        if (horizontal == WidgetEdges.Left)
        {
            var largest = Math.Min(right, widthLimit);
            var smallest = Math.Min(minWidth, largest);
            left = Math.Clamp(left + FiniteDelta(delta.X), right - largest, right - smallest);
        }
        else if (horizontal == WidgetEdges.Right)
        {
            var largest = Math.Min(canvasWidth - left, widthLimit);
            var smallest = Math.Min(minWidth, largest);
            right = Math.Clamp(right + FiniteDelta(delta.X), left + smallest, left + largest);
        }
        if (vertical == WidgetEdges.Top)
        {
            var largest = Math.Min(bottom, heightLimit);
            var smallest = Math.Min(minHeight, largest);
            top = Math.Clamp(top + FiniteDelta(delta.Y), bottom - largest, bottom - smallest);
        }
        else if (vertical == WidgetEdges.Bottom)
        {
            var largest = Math.Min(canvasHeight - top, heightLimit);
            var smallest = Math.Min(minHeight, largest);
            bottom = Math.Clamp(bottom + FiniteDelta(delta.Y), top + smallest, top + largest);
        }
        return new LayoutRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static SnapResult Snap(LayoutRect bounds, LayoutSize canvas, IReadOnlyList<LayoutRect> peers, WidgetEdges edges, bool moving, double threshold = 8,
        LayoutSize? minimum = null, LayoutSize? maximum = null)
    {
        if (!FiniteBounds(bounds)) return new(LayoutRect.Empty, null, null);
        var width = CanvasExtent(canvas.Width); var height = CanvasExtent(canvas.Height);
        var tolerance = double.IsFinite(threshold) ? Math.Max(0, threshold) : 0;
        var horizontal = edges & (WidgetEdges.Left | WidgetEdges.Right);
        var vertical = edges & (WidgetEdges.Top | WidgetEdges.Bottom);
        bool AllowedResize(double adjustment, bool xAxis)
        {
            var edge = xAxis ? horizontal : vertical;
            var resized = Resize(bounds, xAxis ? new LayoutVector(adjustment, 0) : new LayoutVector(0, adjustment), edge,
                minimum ?? new LayoutSize(), canvas, maximum);
            var actual = xAxis ? edge == WidgetEdges.Left ? resized.Left : resized.Right : edge == WidgetEdges.Top ? resized.Top : resized.Bottom;
            var target = (xAxis ? edge == WidgetEdges.Left ? bounds.Left : bounds.Right : edge == WidgetEdges.Top ? bounds.Top : bounds.Bottom) + adjustment;
            return Math.Abs(actual - target) < 1e-8;
        }
        var xs = moving ? new[] { bounds.Left, bounds.Left + bounds.Width / 2, bounds.Right }
            : horizontal == WidgetEdges.Left ? new[] { bounds.Left }
            : horizontal == WidgetEdges.Right ? new[] { bounds.Right } : Array.Empty<double>();
        var ys = moving ? new[] { bounds.Top, bounds.Top + bounds.Height / 2, bounds.Bottom }
            : vertical == WidgetEdges.Top ? new[] { bounds.Top }
            : vertical == WidgetEdges.Bottom ? new[] { bounds.Bottom } : Array.Empty<double>();
        var x = Nearest(xs, width, peers, true, tolerance, adjustment => moving
            ? bounds.Left + adjustment >= 0 && bounds.Right + adjustment <= width
            : (horizontal == WidgetEdges.Left ? bounds.Left + adjustment < bounds.Right : bounds.Right + adjustment > bounds.Left) && AllowedResize(adjustment, true));
        var y = Nearest(ys, height, peers, false, tolerance, adjustment => moving
            ? bounds.Top + adjustment >= 0 && bounds.Bottom + adjustment <= height
            : (vertical == WidgetEdges.Top ? bounds.Top + adjustment < bounds.Bottom : bounds.Bottom + adjustment > bounds.Top) && AllowedResize(adjustment, false));
        var left = bounds.Left; var right = bounds.Right;
        var top = bounds.Top; var bottom = bounds.Bottom;
        if (x is { } snapX)
        {
            if (moving) { left += snapX.Adjustment; right += snapX.Adjustment; }
            else if (horizontal == WidgetEdges.Left) left += snapX.Adjustment;
            else if (horizontal == WidgetEdges.Right) right += snapX.Adjustment;
        }
        if (y is { } snapY)
        {
            if (moving) { top += snapY.Adjustment; bottom += snapY.Adjustment; }
            else if (vertical == WidgetEdges.Top) top += snapY.Adjustment;
            else if (vertical == WidgetEdges.Bottom) bottom += snapY.Adjustment;
        }
        return new(new LayoutRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)), x?.Guide, y?.Guide);
    }

    private readonly record struct Candidate(double Adjustment, double Guide, int TargetPriority, int SourcePriority);
    private static Candidate? Nearest(double[] sources, double extent, IReadOnlyList<LayoutRect> peers, bool horizontal, double threshold, Func<double, bool> allowed)
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
    private static LayoutRect WithinCanvas(LayoutRect bounds, double width, double height)
    {
        if (!FiniteBounds(bounds)) return new(0, 0, 0, 0);
        var left = Math.Clamp(bounds.Left, 0, width); var right = Math.Clamp(bounds.Right, left, width);
        var top = Math.Clamp(bounds.Top, 0, height); var bottom = Math.Clamp(bounds.Bottom, top, height);
        return new(left, top, right - left, bottom - top);
    }
    private static bool FiniteBounds(LayoutRect bounds) => !bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top)
        && double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom) && bounds.Width >= 0 && bounds.Height >= 0;
    private static double CanvasExtent(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double SizeLimit(double value, double fallback) => double.IsNaN(value) || double.IsNegativeInfinity(value) ? fallback : Math.Max(0, value);
    private static double FiniteDelta(double value) => double.IsFinite(value) ? value : 0;
}

public static class WidgetLayout
{
    public static WidgetPlacement FindNewPlacement(LayoutSize canvas, IReadOnlyList<LayoutRect> occupied, int ordinal, out bool crowded)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0)
        {
            crowded = true;
            return new WidgetPlacement(Math.Clamp(.06 + ordinal * .07, 0, 1), Math.Clamp(.06 + ordinal * .06, 0, 1));
        }
        foreach (var scale in new[] { 1d, .85, .72 })
        {
            var width = Math.Min(canvas.Width, Math.Max(240, 320 * scale + 24));
            var height = Math.Min(canvas.Height, Math.Max(230, 300 * scale + 24));
            var xs = CandidateAxes(occupied.Select(r => (r.Left, r.Right)), canvas.Width, width);
            var ys = CandidateAxes(occupied.Select(r => (r.Top, r.Bottom)), canvas.Height, height);
            foreach (var y in ys)
            foreach (var x in xs)
            {
                var candidate = new LayoutRect(x, y, width, height);
                var separated = new LayoutRect(x - 6, y - 6, width + 12, height + 12);
                if (occupied.Any(r => r.Width > 0 && r.Height > 0 && separated.IntersectsWith(r))) continue;
                crowded = false;
                return Placement(candidate, canvas, scale);
            }
        }
        // There is no unused rectangle. A distinct cascade keeps the new title visible
        // without moving or shrinking a user's existing widgets.
        crowded = true;
        var fallbackWidth = Math.Min(canvas.Width, 344);
        var fallbackHeight = Math.Min(canvas.Height, 324);
        var travelX = Math.Max(0, canvas.Width - fallbackWidth);
        var travelY = Math.Max(0, canvas.Height - fallbackHeight);
        var fallbackX = travelX * ((ordinal + 1) * .61803398875 % 1);
        var fallbackY = travelY * ((ordinal + 1) * .38196601125 % 1);
        return Placement(new LayoutRect(fallbackX, fallbackY, fallbackWidth, fallbackHeight), canvas, 1);
    }
    private static double[] CandidateAxes(IEnumerable<(double Start, double End)> ranges, double extent, double length)
    {
        var limit = Math.Max(0, extent - length);
        return ranges.SelectMany(r => new[] { r.End + 12, r.Start - length - 12 })
            .Append(Math.Min(8, limit)).Append(limit).Where(x => x >= 0 && x <= limit).Distinct().Order().ToArray();
    }
    private static WidgetPlacement Placement(LayoutRect rectangle, LayoutSize canvas, double scale) => new(
        rectangle.X / Math.Max(1, canvas.Width - rectangle.Width), rectangle.Y / Math.Max(1, canvas.Height - rectangle.Height), scale);
}
