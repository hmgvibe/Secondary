using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.App.Presentation;

// WPF adapter over the shared placement search.
public static class HomeWidgetLayout
{
    public static WidgetPlacement FindNewPlacement(Size canvas, IReadOnlyList<Rect> occupied, int ordinal, out bool crowded) =>
        WidgetLayout.FindNewPlacement(HomeWidgetGeometry.ToShared(canvas), occupied.Select(HomeWidgetGeometry.ToShared).ToArray(), ordinal, out crowded);
}
