namespace Secondary.Core;

public static class ClockTextLayout
{
    // Slots depend on the format, never on the current digit's glyph width.
    public static double[] Centers(string text, double digitWidth, double separatorWidth)
    {
        var total = text.Sum(c => c == ':' ? separatorWidth : digitWidth);
        var left = -total / 2;
        var centers = new double[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var width = text[i] == ':' ? separatorWidth : digitWidth;
            centers[i] = left + width / 2;
            left += width;
        }
        return centers;
    }
}
