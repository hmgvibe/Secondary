using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace Secondary.App;

// Fully owner-drawn tray menu; no system/visual-style renderer is invoked.
public sealed class TrayMenuRenderer(Window window) : Forms.ToolStripRenderer
{
    private Color ColorFor(string key, Color fallback)
    {
        if (window.TryFindResource(key) is SolidColorBrush brush)
            return Color.FromArgb(brush.Color.R,brush.Color.G,brush.Color.B);
        return fallback;
    }
    private static float Scale(Forms.ToolStrip? strip) => (strip?.DeviceDpi ?? 96) / 96f;
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); var d = radius * 2;
        path.AddArc(bounds.Left,bounds.Top,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
        path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
    }
    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(ColorFor("SheetBrush",Color.FromArgb(24,32,42)));
    }
    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),10*Scale(e.ToolStrip));
        using var pen = new Pen(ColorFor("LineBrush",Color.Gray)); e.Graphics.DrawPath(pen,path);
    }
    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ColorFor("HoverBrush",Color.DimGray));
        using var path = Rounded(new RectangleF(3,1,e.Item.Width-6,e.Item.Height-2),7*Scale(e.ToolStrip)); e.Graphics.FillPath(brush,path);
    }
    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e) =>
        Forms.TextRenderer.DrawText(e.Graphics,e.Text,e.TextFont,e.TextRectangle,
            ColorFor(e.Item.Enabled ? "TextBrush" : "MutedBrush",Color.White),e.TextFormat);
    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(ColorFor("LineBrush",Color.Gray));
        var inset = (int)(12*Scale(e.ToolStrip)); e.Graphics.DrawLine(pen,inset,e.Item.Height/2,e.Item.Width-inset,e.Item.Height/2);
    }
    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }
}
