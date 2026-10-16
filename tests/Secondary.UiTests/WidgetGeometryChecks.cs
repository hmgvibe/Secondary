using System;
using System.Linq;
using System.Windows;
using Secondary.App.Presentation;

internal static class WidgetGeometryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var start = new Rect(40, 30, 100, 90);
        var minimum = new Size(40, 30);
        var canvas = new Size(300, 220);
        var directions = new[]
        {
            ResizeEdges.Left, ResizeEdges.Top, ResizeEdges.Right, ResizeEdges.Bottom,
            ResizeEdges.Left | ResizeEdges.Top, ResizeEdges.Right | ResizeEdges.Top,
            ResizeEdges.Right | ResizeEdges.Bottom, ResizeEdges.Left | ResizeEdges.Bottom
        };
        foreach (var edges in directions)
        foreach (var delta in new[] { new Vector(0, 0), new Vector(22, -18), new Vector(-34, 29), new Vector(1000, 1000), new Vector(-1000, -1000) })
        {
            var result = HomeWidgetGeometry.Resize(start, delta, edges, minimum, canvas);
            check(result.Left >= 0 && result.Top >= 0 && result.Right <= canvas.Width && result.Bottom <= canvas.Height,
                edges + " resize stays within the canvas for small and extreme pointer movements");
            check(result.Width >= minimum.Width && result.Height >= minimum.Height, edges + " resize cannot cross its readable minimum");
            check(edges.HasFlag(ResizeEdges.Left) ? Same(result.Right, start.Right) : Same(result.Left, start.Left), edges + " resize fixes the opposite horizontal edge");
            check(edges.HasFlag(ResizeEdges.Top) ? Same(result.Bottom, start.Bottom) : Same(result.Top, start.Top), edges + " resize fixes the opposite vertical edge");
            if ((edges & (ResizeEdges.Left | ResizeEdges.Right)) == ResizeEdges.None) check(Same(result.Width, start.Width), "Vertical handle ignores horizontal pointer movement");
            if ((edges & (ResizeEdges.Top | ResizeEdges.Bottom)) == ResizeEdges.None) check(Same(result.Height, start.Height), "Horizontal handle ignores vertical pointer movement");
        }
        var smallest = HomeWidgetGeometry.Resize(start, new Vector(1000, 1000), ResizeEdges.Left | ResizeEdges.Top, minimum, canvas);
        check(smallest == new Rect(100, 90, 40, 30), "Top-left shrink retains the original bottom-right corner exactly");
        var largest = HomeWidgetGeometry.Resize(start, new Vector(-1000, -1000), ResizeEdges.Left | ResizeEdges.Top, minimum, canvas);
        check(largest == new Rect(0, 0, 140, 120), "Top-left expansion stops at both canvas edges without shifting its anchor");
        var limited = HomeWidgetGeometry.Resize(start, new Vector(1000, 1000), ResizeEdges.Right | ResizeEdges.Bottom, minimum, canvas, new Size(130, 110));
        check(limited == new Rect(40, 30, 130, 110), "Independent maximum dimensions limit bottom-right expansion");
        var leftLimited = HomeWidgetGeometry.Resize(start, new Vector(-1000, -1000), ResizeEdges.Left | ResizeEdges.Top, minimum, canvas, new Size(130, 110));
        check(leftLimited == new Rect(10, 10, 130, 110), "Maximum dimensions also constrain top-left expansion without moving the opposite edge");
        var tiny = HomeWidgetGeometry.Resize(new Rect(0, 0, 10, 10), new Vector(1000, 1000), ResizeEdges.Right | ResizeEdges.Bottom, new Size(240, 230), new Size(20, 18));
        check(tiny == new Rect(0, 0, 20, 18), "Canvas smaller than the requested minimum remains a valid bounded rectangle");
        var conflicting = HomeWidgetGeometry.Resize(start, new Vector(1000, 1000), ResizeEdges.Left | ResizeEdges.Right | ResizeEdges.Top | ResizeEdges.Bottom, minimum, canvas);
        check(conflicting == start, "Contradictory opposite-edge flags do not pick an arbitrary resizing direction");
        check(HomeWidgetGeometry.Resize(start, new Vector(double.NaN, double.PositiveInfinity), ResizeEdges.Right | ResizeEdges.Bottom, minimum, canvas) == start,
            "Non-finite pointer deltas do not corrupt placement geometry");
        var emptyCanvas = HomeWidgetGeometry.Resize(start, new Vector(100, 100), ResizeEdges.Right | ResizeEdges.Bottom, minimum, new Size());
        check(emptyCanvas == new Rect(0, 0, 0, 0), "A temporarily empty canvas produces finite zero geometry");

        var aligned = HomeWidgetGeometry.Snap(new Rect(202, 152, 100, 80), new Size(1000, 600), [new Rect(200, 150, 100, 80)], ResizeEdges.None, true);
        check(aligned.Bounds == new Rect(200, 150, 100, 80) && aligned.GuideX == 200 && aligned.GuideY == 150,
            "Moving snaps both axes to a peer while preserving width and height");
        var center = HomeWidgetGeometry.Snap(new Rect(188, 91, 20, 20), new Size(400, 200), [], ResizeEdges.None, true);
        check(center.Bounds == new Rect(190, 90, 20, 20) && center.GuideX == 200 && center.GuideY == 100,
            "Moving can align its center with both canvas center guides");
        var right = HomeWidgetGeometry.Snap(new Rect(375, 50, 20, 20), new Size(400, 200), [], ResizeEdges.None, true);
        check(right.Bounds.Right == 400 && right.GuideX == 400, "Moving aligns its right edge with the canvas boundary");
        var nearest = HomeWidgetGeometry.Snap(new Rect(6, 100, 20, 20), new Size(500, 500), [new Rect(8, 300, 20, 20)], ResizeEdges.None, true);
        check(nearest.Bounds.Left == 8 && nearest.GuideX == 8, "A nearer peer wins over a more distant canvas boundary");
        var canvasTie = HomeWidgetGeometry.Snap(new Rect(4, 100, 20, 20), new Size(500, 500), [new Rect(8, 300, 20, 20)], ResizeEdges.None, true);
        check(canvasTie.Bounds.Left == 0 && canvasTie.GuideX == 0, "Equidistant snaps prefer a canvas guide deterministically");
        var peers = new[] { new Rect(96, 300, 20, 20), new Rect(104, 300, 20, 20) };
        var tie = HomeWidgetGeometry.Snap(new Rect(100, 100, 20, 20), new Size(500, 500), peers, ResizeEdges.None, true);
        var reversed = HomeWidgetGeometry.Snap(new Rect(100, 100, 20, 20), new Size(500, 500), peers.Reverse().ToArray(), ResizeEdges.None, true);
        check(tie == reversed && tie.GuideX == 96 && tie.Bounds.Left == 96, "Peer order cannot change the winner of an equidistant snap");
        var threshold = HomeWidgetGeometry.Snap(new Rect(100, 100, 20, 20), new Size(500, 500), [new Rect(72, 300, 20, 20)], ResizeEdges.None, true);
        var tooFar = HomeWidgetGeometry.Snap(new Rect(100, 100, 20, 20), new Size(500, 500), [new Rect(72, 300, 20, 20)], ResizeEdges.None, true, 7.99);
        check(threshold.GuideX == 92 && tooFar.GuideX is null, "Snap threshold is inclusive and does not pull a farther candidate");
        var resized = HomeWidgetGeometry.Snap(new Rect(100, 100, 50, 40), new Size(500, 500), [new Rect(156, 143, 30, 30)], ResizeEdges.Right | ResizeEdges.Bottom, false);
        check(resized.Bounds == new Rect(100, 100, 56, 43) && resized.GuideX == 156 && resized.GuideY == 143,
            "Corner snapping adjusts only dragged right and bottom edges");
        var inactive = HomeWidgetGeometry.Snap(new Rect(100, 100, 50, 40), new Size(500, 500), [new Rect(80, 80, 20, 20)], ResizeEdges.Right, false);
        check(inactive.Bounds == new Rect(100, 100, 50, 40) && inactive.GuideX is null && inactive.GuideY is null,
            "An already-aligned inactive edge does not snap or display a resize guide");
        var crossing = HomeWidgetGeometry.Snap(new Rect(96, 60, 5, 40), new Size(500, 300), [new Rect(103, 10, 20, 40)], ResizeEdges.Left, false);
        check(crossing.Bounds == new Rect(96, 60, 5, 40) && crossing.GuideX is null, "Resize snapping rejects a target beyond the opposite edge");
        var undersizedSnap = HomeWidgetGeometry.Snap(new Rect(95, 30, 45, 90), canvas, [new Rect(102, 160, 1, 10)], ResizeEdges.Left, false);
        var minimumClamped = HomeWidgetGeometry.Resize(start, new Vector(undersizedSnap.Bounds.Left - start.Left, 0), ResizeEdges.Left, minimum, canvas);
        check(undersizedSnap.GuideX == 102 && minimumClamped.Width == 40 && minimumClamped.Right == start.Right && minimumClamped.Left != undersizedSnap.GuideX,
            "Reclamping an undersized snap preserves the fixed anchor and makes its outdated guide distinguishable");
        var oversizedSnap = HomeWidgetGeometry.Snap(new Rect(40, 30, 125, 90), canvas, [new Rect(172, 160, 1, 10)], ResizeEdges.Right, false);
        var maximumClamped = HomeWidgetGeometry.Resize(start, new Vector(oversizedSnap.Bounds.Right - start.Right, 0), ResizeEdges.Right, minimum, canvas, new Size(130, 110));
        check(oversizedSnap.GuideX == 172 && maximumClamped.Width == 130 && maximumClamped.Left == start.Left && maximumClamped.Right != oversizedSnap.GuideX,
            "Reclamping an oversized snap preserves the fixed anchor and does not pretend to remain aligned");
        var validMinimum = HomeWidgetGeometry.Snap(new Rect(100, 30, 40, 90), canvas, [new Rect(102, 160, 1, 10), new Rect(94, 160, 1, 10)], ResizeEdges.Left, false, minimum: minimum);
        check(validMinimum.Bounds.Left == 95 && validMinimum.GuideX == 95 && validMinimum.Bounds.Right == 140,
            "A closer snap below minimum cannot suppress the next valid alignment target");
        var validMaximum = HomeWidgetGeometry.Snap(new Rect(40, 30, 130, 90), canvas, [new Rect(172, 160, 1, 10), new Rect(164, 160, 1, 10)], ResizeEdges.Right, false, maximum: new Size(130, 110));
        check(validMaximum.Bounds.Right == 165 && validMaximum.GuideX == 165 && validMaximum.Bounds.Left == 40,
            "A closer snap above maximum cannot suppress the next valid alignment target");
        var ignoredEmptyPeer = HomeWidgetGeometry.Snap(new Rect(100, 100, 20, 20), new Size(500, 500), [new Rect(102, 100, 0, 0), Rect.Empty], ResizeEdges.None, true);
        check(ignoredEmptyPeer.GuideX is null && ignoredEmptyPeer.GuideY is null, "Empty peers cannot create invisible alignment guides");
    }
    private static bool Same(double first, double second) => Math.Abs(first - second) < 1e-9;
}
