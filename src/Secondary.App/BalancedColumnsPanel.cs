using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Secondary.App;

// Equal-width columns with independent vertical flow. Tallest cards are placed first
// in the shortest column; a single column retains the original reading order.
public sealed class BalancedColumnsPanel : Panel
{
    public static readonly DependencyProperty StackOrderProperty = DependencyProperty.RegisterAttached(
        "StackOrder", typeof(int), typeof(BalancedColumnsPanel),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static int GetStackOrder(DependencyObject element) => (int)element.GetValue(StackOrderProperty);
    public static void SetStackOrder(DependencyObject element, int value) => element.SetValue(StackOrderProperty, value);
    private Rect[] slots = [];
    private double Layout(double width, bool measure)
    {
        if (double.IsInfinity(width)) width = 1128;
        width = Math.Max(0, width);
        var count = InternalChildren.Count;
        var grouped = Enumerable.Range(0, count).Where(i => GetStackOrder(InternalChildren[i]) >= 0)
            .OrderBy(i => GetStackOrder(InternalChildren[i])).ToArray();
        // Keep hardware together without reducing the wide dashboard to two columns.
        var units = count - grouped.Length + (grouped.Length > 0 ? 1 : 0);
        var columns = Math.Max(1, Math.Min(Math.Min(3, units), (int)((width + 16) / 296)));
        var cardWidth = Math.Max(0, Math.Min(360, (width - 16 * (columns - 1)) / columns));
        if (measure)
            foreach (UIElement child in InternalChildren) child.Measure(new Size(cardWidth, double.PositiveInfinity));
        slots = new Rect[count];
        var heights = new double[columns];
        var order = Enumerable.Range(0, count).Except(grouped);
        if (columns > 1) order = order.OrderByDescending(i => InternalChildren[i].DesiredSize.Height);
        var left = Math.Max(0, (width - columns * cardWidth - 16 * (columns - 1)) / 2);
        void Place(int i, int column)
        {
            var height = InternalChildren[i].DesiredSize.Height;
            slots[i] = new Rect(left + column * (cardWidth + 16), heights[column], cardWidth, height);
            heights[column] += height + 16;
        }
        foreach (var i in grouped) Place(i, 0);
        foreach (var i in order)
        {
            var firstAvailable = columns > 1 && grouped.Length > 0 ? 1 : 0;
            var column = Enumerable.Range(firstAvailable, columns - firstAvailable)
                .OrderBy(c => heights[c]).First();
            Place(i, column);
        }
        return Math.Max(0, heights.Max() - 16);
    }
    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 1128 : availableSize.Width, Layout(availableSize.Width, true));
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, false);
        for (var i = 0; i < slots.Length; i++) InternalChildren[i].Arrange(slots[i]);
        return finalSize;
    }
}
