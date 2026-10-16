using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Secondary.Core;

namespace Secondary.App.Presentation;

public static class HomeWidgetLayout
{
    public static WidgetPlacement FindNewPlacement(Size canvas, IReadOnlyList<Rect> occupied, int ordinal, out bool crowded)
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
                var candidate = new Rect(x, y, width, height);
                var separated = new Rect(x - 6, y - 6, width + 12, height + 12);
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
        return Placement(new Rect(fallbackX, fallbackY, fallbackWidth, fallbackHeight), canvas, 1);
    }
    private static double[] CandidateAxes(IEnumerable<(double Start, double End)> ranges, double extent, double length)
    {
        var limit = Math.Max(0, extent - length);
        return ranges.SelectMany(r => new[] { r.End + 12, r.Start - length - 12 })
            .Append(Math.Min(8, limit)).Append(limit).Where(x => x >= 0 && x <= limit).Distinct().Order().ToArray();
    }
    private static WidgetPlacement Placement(Rect rectangle, Size canvas, double scale) => new(
        rectangle.X / Math.Max(1, canvas.Width - rectangle.Width), rectangle.Y / Math.Max(1, canvas.Height - rectangle.Height), scale);
}
