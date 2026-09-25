using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using Secondary.Core;

namespace Secondary.App.Presentation;

// Reusable interaction frame: widget content does not own dragging, sizing or persistence.
public sealed class HomeWidget : Grid
{
    private readonly Border outline;
    private readonly Thumb mover, resizer;
    private Canvas? surface;
    private Point startPointer, startLocation;
    private WidgetPlacement startPlacement = new();
    private double startWidth, startHeight;
    private bool dragging;
    public WidgetPlacement Placement { get; private set; } = new();
    public Size NaturalSize { get; private set; } = new(462,182);
    public bool IsInteracting => dragging;
    public void CommitPlacement() => PlacementCommitted?.Invoke(Placement);
    public event Action<WidgetPlacement>? PlacementCommitted;
    public HomeWidget()
    {
        outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), IsHitTestVisible = false, Opacity = 0 };
        outline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        Children.Add(outline);
        mover = CreateThumb(Cursors.SizeAll);
        AutomationProperties.SetName(mover, "移動時鐘組件；方向鍵移動，Ctrl 加左右鍵調寬、上下鍵調高，Esc 取消拖曳");
        resizer = CreateThumb(Cursors.SizeNWSE);
        resizer.Width = resizer.Height = 28;
        resizer.HorizontalAlignment = HorizontalAlignment.Right; resizer.VerticalAlignment = VerticalAlignment.Bottom;
        resizer.Opacity = 0;
        resizer.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        var grip = new FrameworkElementFactory(typeof(TextBlock));
        grip.SetValue(TextBlock.TextProperty,"◢");
        grip.SetValue(TextBlock.FontSizeProperty,16d);
        grip.SetValue(TextBlock.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        grip.SetValue(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center);
        grip.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");
        var gripArea = new FrameworkElementFactory(typeof(Border));
        gripArea.SetValue(Border.BackgroundProperty,Brushes.Transparent); gripArea.AppendChild(grip);
        resizer.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = gripArea };
        AutomationProperties.SetName(resizer, "調整時鐘組件寬度與高度");
        Children.Add(mover); Children.Add(resizer);
        foreach (var thumb in new[] { mover, resizer })
        {
            thumb.DragStarted += (_, _) =>
            {
                if (surface is null) return;
                dragging = true; startPointer = Mouse.GetPosition(surface); startPlacement = Placement;
                startLocation = new(Canvas.GetLeft(this),Canvas.GetTop(this)); startWidth = Width; startHeight = Height;
                UpdateChrome();
            };
            thumb.DragDelta += (_, _) =>
            {
                if (surface is null || !dragging) return;
                var delta = Mouse.GetPosition(surface) - startPointer;
                if (thumb == mover) MoveTo(startLocation.X + delta.X, startLocation.Y + delta.Y);
                else
                {
                    ResizeTo(startWidth + delta.X, startHeight + delta.Y, startLocation);
                }
            };
            thumb.DragCompleted += (_, e) =>
            {
                if (!dragging) return;
                dragging = false;
                if (e.Canceled) { Placement = startPlacement; ArrangeWidget(); }
                else CommitPlacement();
                UpdateChrome();
            };
            thumb.PreviewKeyDown += OnKey;
        }
        MouseEnter += (_, _) => UpdateChrome(); MouseLeave += (_, _) => UpdateChrome();
    }
    private static Thumb CreateThumb(Cursor cursor)
    {
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        visual.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));
        return new Thumb { Background = Brushes.Transparent, Cursor = cursor, Focusable = true, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual } };
    }
    public void Attach(Canvas canvas, FrameworkElement content, string? title = null)
    {
        surface = canvas; content.Margin = new Thickness(12); content.IsHitTestVisible = title is not null;
        if (title is not null)
        {
            var backing = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            backing.SetResourceReference(Border.BackgroundProperty,"SheetBrush");
            backing.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); Children.Insert(0,backing);
            mover.Height = 40; mover.VerticalAlignment = VerticalAlignment.Top;
            content.Margin = new Thickness(12,40,12,12);
            var label = new TextBlock { Text = title, Margin = new Thickness(24,14,24,0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, FontSize = 14, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Children.Add(label);
            var hint = new TextBlock { Text = "⠿", Margin = new Thickness(0,12,22,0), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, FontSize = 16 };
            hint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); Children.Add(hint);
            AutomationProperties.SetName(mover, "拖動" + title + "組件；方向鍵移動，Ctrl 加左右鍵調寬、上下鍵調高");
            AutomationProperties.SetName(resizer, "調整" + title + "組件寬度與高度");
        }
        Children.Insert(1,content); canvas.Children.Add(this);
        canvas.SizeChanged += OnSurfaceSizeChanged;
    }
    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) { CancelInteraction(); ArrangeWidget(); }
    public void Detach()
    {
        CancelInteraction();
        if (surface is not null) { surface.SizeChanged -= OnSurfaceSizeChanged; surface.Children.Remove(this); surface = null; }
    }
    public void Configure(WidgetPlacement placement, Size naturalSize)
    {
        CancelInteraction(); Placement = placement.Normalize(); NaturalSize = naturalSize; ArrangeWidget();
    }
    private void UpdateChrome()
    {
        // Thumb keeps keyboard focus after a mouse drag. Focus must not pin the
        // editing frame on screen after the pointer leaves the widget.
        var visible = IsMouseOver || dragging;
        outline.Opacity = resizer.Opacity = visible ? 1 : 0;
    }
    public void ArrangeWidget()
    {
        if (surface is null) return;
        var availableW = Math.Max(0,surface.ActualWidth - 24); var availableH = Math.Max(0,surface.ActualHeight - 24);
        var scale = Math.Min(Placement.Scale,Math.Min(availableW/NaturalSize.Width,availableH/NaturalSize.Height));
        Width = Math.Min(surface.ActualWidth, NaturalSize.Width * Math.Max(0,Placement.WidthScale ?? scale) + 24);
        Height = Math.Min(surface.ActualHeight, NaturalSize.Height * Math.Max(0,Placement.HeightScale ?? scale) + 24);
        Canvas.SetLeft(this,Math.Max(0,surface.ActualWidth-Width)*Placement.X);
        Canvas.SetTop(this,Math.Max(0,surface.ActualHeight-Height)*Placement.Y);
    }
    public void MoveTo(double x, double y)
    {
        if (surface is null) return;
        Placement = (Placement with { X = x/Math.Max(1,surface.ActualWidth-Width), Y = y/Math.Max(1,surface.ActualHeight-Height) }).Normalize();
        ArrangeWidget();
    }
    public void ResizeTo(double width, Point anchor)
    {
        if (surface is null) return;
        var max = Math.Min(Math.Max(0,surface.ActualWidth-anchor.X-24)/NaturalSize.Width,Math.Max(0,surface.ActualHeight-anchor.Y-24)/NaturalSize.Height);
        Placement = (Placement with { Scale = Math.Min(max, (width-24)/NaturalSize.Width), WidthScale = null, HeightScale = null }).Normalize();
        ArrangeWidget(); MoveTo(anchor.X,anchor.Y);
    }
    public void ResizeTo(double width, double height, Point anchor)
    {
        if (surface is null || !double.IsFinite(width) || !double.IsFinite(height)) return;
        Placement = (Placement with {
            WidthScale = (Math.Min(width,Math.Max(24,surface.ActualWidth-anchor.X))-24)/NaturalSize.Width,
            HeightScale = (Math.Min(height,Math.Max(24,surface.ActualHeight-anchor.Y))-24)/NaturalSize.Height
        }).Normalize();
        ArrangeWidget(); MoveTo(anchor.X,anchor.Y);
    }
    public void CancelInteraction()
    {
        if (dragging) { mover.CancelDrag(); resizer.CancelDrag(); }
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && dragging) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 20 : 5;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ResizeTo(
            Width + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),
            Height + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0),new(Canvas.GetLeft(this),Canvas.GetTop(this)));
        else MoveTo(Canvas.GetLeft(this) + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),Canvas.GetTop(this) + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0));
        CommitPlacement(); e.Handled = true;
    }
}
