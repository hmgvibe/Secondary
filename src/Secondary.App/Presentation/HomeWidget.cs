using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Shapes;
using Secondary.Core;

namespace Secondary.App.Presentation;

// Reusable interaction frame: widget content does not own dragging, sizing or persistence.
public sealed class HomeWidget : Grid
{
    private readonly Border outline;
    private readonly Thumb mover;
    private readonly Dictionary<Thumb, ResizeEdges> resizers = new();
    private readonly Line verticalGuide = CreateGuide(), horizontalGuide = CreateGuide();
    private Thumb? activeThumb;
    private Canvas? surface;
    private Point startPointer;
    private Rect startBounds;
    private Rect[] snapTargets = [];
    private WidgetPlacement startPlacement = new();
    private bool dragging;
    private bool keyboardEditing;
    private Size minimumSize;
    public WidgetPlacement Placement { get; private set; } = new();
    public Size NaturalSize { get; private set; } = new(462,182);
    public bool IsInteracting => dragging;
    public void CommitPlacement() => PlacementCommitted?.Invoke(Placement);
    public void SelectForEditing() { keyboardEditing = true; mover.Focus(); UpdateChrome(); }
    public event Action<WidgetPlacement>? PlacementCommitted;
    public HomeWidget()
    {
        outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), IsHitTestVisible = false, Opacity = 0 };
        outline.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        Panel.SetZIndex(outline, 10);
        Children.Add(outline);
        mover = CreateThumb(Cursors.SizeAll);
        mover.Tag = ResizeEdges.None;
        Panel.SetZIndex(mover, 1);
        Children.Add(mover);
        AddResizeHandle(ResizeEdges.Left, Cursors.SizeWE, "左邊");
        AddResizeHandle(ResizeEdges.Top, Cursors.SizeNS, "上邊");
        AddResizeHandle(ResizeEdges.Right, Cursors.SizeWE, "右邊");
        AddResizeHandle(ResizeEdges.Bottom, Cursors.SizeNS, "下邊");
        AddResizeHandle(ResizeEdges.Left | ResizeEdges.Top, Cursors.SizeNWSE, "左上角");
        AddResizeHandle(ResizeEdges.Right | ResizeEdges.Top, Cursors.SizeNESW, "右上角");
        AddResizeHandle(ResizeEdges.Left | ResizeEdges.Bottom, Cursors.SizeNESW, "左下角");
        AddResizeHandle(ResizeEdges.Right | ResizeEdges.Bottom, Cursors.SizeNWSE, "右下角");
        SetInteractionNames("時鐘");
        foreach (var thumb in new[] { mover }.Concat(resizers.Keys))
        {
            thumb.DragStarted += (_, _) =>
            {
                if (surface is null) return;
                CancelInteraction();
                dragging = true; activeThumb = thumb; startPointer = Mouse.GetPosition(surface); startPlacement = Placement;
                startBounds = Bounds;
                snapTargets = surface.Children.OfType<HomeWidget>().Where(w => w != this && w.Visibility == Visibility.Visible)
                    .Select(w => w.Bounds).Where(r => r.Width > 0 && r.Height > 0).ToArray();
                UpdateChrome();
            };
            thumb.DragDelta += (_, _) =>
            {
                if (surface is null || !dragging || activeThumb != thumb) return;
                var delta = Mouse.GetPosition(surface) - startPointer;
                DragTo(delta, thumb == mover ? ResizeEdges.None : resizers[thumb]);
            };
            thumb.DragCompleted += (_, e) =>
            {
                if (activeThumb == thumb) FinishInteraction(e.Canceled);
            };
            thumb.PreviewKeyDown += OnKey;
            thumb.GotKeyboardFocus += (_, _) => { keyboardEditing = Mouse.LeftButton != MouseButtonState.Pressed; UpdateChrome(); };
            thumb.LostKeyboardFocus += (_, _) => { keyboardEditing = false; UpdateChrome(); };
        }
        MouseEnter += (_, _) => UpdateChrome(); MouseLeave += (_, _) => UpdateChrome();
        PreviewMouseDown += (_, _) => { keyboardEditing = false; UpdateChrome(); };
        Unloaded += (_, _) => CancelInteraction();
    }
    private Rect Bounds => new(Canvas.GetLeft(this), Canvas.GetTop(this), Width, Height);
    private Size CanvasSize => new(surface!.ActualWidth, surface.ActualHeight);
    private Size MinimumSize => new(Math.Max(minimumSize.Width, NaturalSize.Width * .3 + 24), Math.Max(minimumSize.Height, NaturalSize.Height * .3 + 24));
    private Size MaximumSize => new(NaturalSize.Width * 3 + 24, NaturalSize.Height * 3 + 24);
    private void AddResizeHandle(ResizeEdges edges, Cursor cursor, string name)
    {
        var corner = (edges.HasFlag(ResizeEdges.Left) || edges.HasFlag(ResizeEdges.Right)) &&
            (edges.HasFlag(ResizeEdges.Top) || edges.HasFlag(ResizeEdges.Bottom));
        var horizontal = edges is ResizeEdges.Top or ResizeEdges.Bottom;
        var thumb = CreateThumb(cursor);
        thumb.Tag = edges; thumb.Opacity = 0;
        thumb.HorizontalAlignment = edges.HasFlag(ResizeEdges.Left) ? HorizontalAlignment.Left : edges.HasFlag(ResizeEdges.Right) ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        thumb.VerticalAlignment = edges.HasFlag(ResizeEdges.Top) ? VerticalAlignment.Top : edges.HasFlag(ResizeEdges.Bottom) ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        if (corner) thumb.Width = thumb.Height = 22;
        else if (horizontal) { thumb.Height = 10; thumb.Margin = new Thickness(22, 0, 22, 0); }
        else { thumb.Width = 10; thumb.Margin = new Thickness(0, 22, 0, 22); }
        var marker = new FrameworkElementFactory(typeof(Border));
        marker.SetValue(Border.WidthProperty, corner ? 7d : horizontal ? 18d : 3d);
        marker.SetValue(Border.HeightProperty, corner ? 7d : horizontal ? 3d : 18d);
        marker.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        marker.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        marker.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        marker.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var area = new FrameworkElementFactory(typeof(Border));
        area.SetValue(Border.BackgroundProperty, Brushes.Transparent); area.AppendChild(marker);
        thumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = area };
        thumb.ToolTip = name + "縮放；按住 Alt 暫停吸附";
        Panel.SetZIndex(thumb, 20); resizers.Add(thumb, edges); Children.Add(thumb);
    }
    private void SetInteractionNames(string title)
    {
        AutomationProperties.SetName(mover, "拖動" + title + "組件；方向鍵移動，Ctrl 加方向鍵縮放，Alt 暫停吸附，Esc 取消拖曳");
        foreach (var thumb in resizers.Keys)
            AutomationProperties.SetName(thumb, "調整" + title + "組件" + ((string)thumb.ToolTip).Replace("縮放；", "；"));
    }
    private static Line CreateGuide()
    {
        var line = new Line { IsHitTestVisible = false, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, SnapsToDevicePixels = true };
        line.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); Panel.SetZIndex(line, int.MaxValue);
        return line;
    }
    private void DragTo(Vector delta, ResizeEdges edges)
    {
        var moving = edges == ResizeEdges.None;
        var bounds = moving ? new Rect(Math.Clamp(startBounds.X + delta.X, 0, Math.Max(0, CanvasSize.Width - startBounds.Width)),
            Math.Clamp(startBounds.Y + delta.Y, 0, Math.Max(0, CanvasSize.Height - startBounds.Height)), startBounds.Width, startBounds.Height)
            : HomeWidgetGeometry.Resize(startBounds, delta, edges, MinimumSize, CanvasSize, MaximumSize);
        var snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? new WidgetSnapResult(bounds, null, null)
            : HomeWidgetGeometry.Snap(bounds, CanvasSize, snapTargets, edges, moving, minimum: MinimumSize, maximum: MaximumSize);
        bounds = snap.Bounds;
        if (moving) MoveTo(bounds.X, bounds.Y);
        else
        {
            // Reapply size limits after snapping; the edge opposite the pointer stays fixed.
            var snappedDelta = new Vector(edges.HasFlag(ResizeEdges.Left) ? bounds.Left - startBounds.Left : bounds.Right - startBounds.Right,
                edges.HasFlag(ResizeEdges.Top) ? bounds.Top - startBounds.Top : bounds.Bottom - startBounds.Bottom);
            bounds = HomeWidgetGeometry.Resize(startBounds, snappedDelta, edges, MinimumSize, CanvasSize, MaximumSize);
            Placement = (startPlacement with {
                WidthScale = (edges & (ResizeEdges.Left | ResizeEdges.Right)) != ResizeEdges.None && Math.Abs(bounds.Width - startBounds.Width) > 1e-8
                    ? (bounds.Width - 24) / NaturalSize.Width : startPlacement.WidthScale,
                HeightScale = (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != ResizeEdges.None && Math.Abs(bounds.Height - startBounds.Height) > 1e-8
                    ? (bounds.Height - 24) / NaturalSize.Height : startPlacement.HeightScale
            }).Normalize();
            ArrangeWidget(); MoveTo(bounds.X, bounds.Y);
        }
        ShowGuides(snap, edges, moving);
    }
    private void ShowGuides(WidgetSnapResult snap, ResizeEdges edges, bool moving)
    {
        ClearGuides();
        var actual = Bounds;
        bool Matches(double? guide, double first, double center, double last, bool useFirst, bool useLast) => guide is double value &&
            ((useFirst && Math.Abs(value - first) < .1) || (moving && Math.Abs(value - center) < .1) || (useLast && Math.Abs(value - last) < .1));
        if (Matches(snap.GuideX, actual.Left, actual.Left + actual.Width / 2, actual.Right, moving || edges.HasFlag(ResizeEdges.Left), moving || edges.HasFlag(ResizeEdges.Right)))
        {
            verticalGuide.X1 = verticalGuide.X2 = snap.GuideX!.Value; verticalGuide.Y1 = 0; verticalGuide.Y2 = surface!.ActualHeight;
            surface.Children.Add(verticalGuide);
        }
        if (Matches(snap.GuideY, actual.Top, actual.Top + actual.Height / 2, actual.Bottom, moving || edges.HasFlag(ResizeEdges.Top), moving || edges.HasFlag(ResizeEdges.Bottom)))
        {
            horizontalGuide.Y1 = horizontalGuide.Y2 = snap.GuideY!.Value; horizontalGuide.X1 = 0; horizontalGuide.X2 = surface!.ActualWidth;
            surface.Children.Add(horizontalGuide);
        }
    }
    private void ClearGuides() { surface?.Children.Remove(verticalGuide); surface?.Children.Remove(horizontalGuide); }
    private void FinishInteraction(bool canceled)
    {
        if (!dragging) return;
        dragging = false; activeThumb = null; snapTargets = []; ClearGuides();
        if (canceled) { Placement = startPlacement; ArrangeWidget(); }
        else CommitPlacement();
        UpdateChrome();
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
            minimumSize = new Size(240, 230);
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
            SetInteractionNames(title);
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
        // A mouse drag may leave Thumb focused; keyboard selections alone keep
        // the editing frame visible after the pointer leaves the widget.
        var visible = IsMouseOver || dragging || keyboardEditing;
        outline.Opacity = visible ? 1 : 0;
        foreach (var thumb in resizers.Keys) thumb.Opacity = visible ? 1 : 0;
    }
    public void ArrangeWidget()
    {
        if (surface is null) return;
        var availableW = Math.Max(0,surface.ActualWidth - 24); var availableH = Math.Max(0,surface.ActualHeight - 24);
        var scale = Math.Min(Placement.Scale,Math.Min(availableW/NaturalSize.Width,availableH/NaturalSize.Height));
        Width = Math.Min(surface.ActualWidth, Math.Max(minimumSize.Width, NaturalSize.Width * Math.Max(0,Placement.WidthScale ?? scale) + 24));
        Height = Math.Min(surface.ActualHeight, Math.Max(minimumSize.Height, NaturalSize.Height * Math.Max(0,Placement.HeightScale ?? scale) + 24));
        Canvas.SetLeft(this,Math.Max(0,surface.ActualWidth-Width)*Placement.X);
        Canvas.SetTop(this,Math.Max(0,surface.ActualHeight-Height)*Placement.Y);
    }
    public void MoveTo(double x, double y)
    {
        if (surface is null) return;
        var travelX = surface.ActualWidth - Width; var travelY = surface.ActualHeight - Height;
        Placement = (Placement with { X = travelX > 0 ? x / travelX : 0, Y = travelY > 0 ? y / travelY : 0 }).Normalize();
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
        var thumb = activeThumb;
        FinishInteraction(true);
        thumb?.CancelDrag();
        ClearGuides();
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        keyboardEditing = true; UpdateChrome();
        if (e.Key == Key.Escape && dragging) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        if (dragging) { e.Handled = true; return; }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 20 : 5;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ResizeTo(
            Width + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),
            Height + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0),new(Canvas.GetLeft(this),Canvas.GetTop(this)));
        else MoveTo(Canvas.GetLeft(this) + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),Canvas.GetTop(this) + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0));
        CommitPlacement(); e.Handled = true;
    }
}
