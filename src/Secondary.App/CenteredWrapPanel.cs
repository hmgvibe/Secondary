using System;
using System.Windows;
using System.Windows.Controls;

namespace Secondary.App;

// Keep content-sized cards and center every row, including the final wrapped row.
public sealed class CenteredWrapPanel : WrapPanel
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var start = 0;
        double rowWidth = 0, rowHeight = 0, y = 0;
        void ArrangeRow(int end)
        {
            var x = Math.Max(0, (finalSize.Width - rowWidth) / 2);
            for (var i = start; i < end; i++)
            {
                var child = InternalChildren[i];
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, rowHeight));
                x += child.DesiredSize.Width;
            }
            y += rowHeight;
        }
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var size = InternalChildren[i].DesiredSize;
            if (i > start && rowWidth + size.Width > finalSize.Width)
            {
                ArrangeRow(i);
                start = i;
                rowWidth = rowHeight = 0;
            }
            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        ArrangeRow(InternalChildren.Count);
        return finalSize;
    }
}
