using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.Linux.Home;

// Focusable hit area used for the move header and the eight resize handles.
public sealed class WidgetHandle : Border
{
    public WidgetEdges Edges { get; }
    public WidgetHandle(WidgetEdges edges) { Edges = edges; Focusable = true; Background = Brushes.Transparent; }
}

// Reusable interaction frame: widget content does not own dragging, sizing or persistence.
// Mirrors the WPF HomeWidget so placements written by either shell mean the same thing.
public sealed class WidgetFrame : Grid
{
    private readonly Border outline;
    private readonly WidgetHandle mover;
    private readonly List<WidgetHandle> resizers = new();
    private readonly Line verticalGuide = CreateGuide(), horizontalGuide = CreateGuide();
    private WidgetHandle? activeHandle;
    private Canvas? surface;
    private Point startPointer;
    private LayoutRect startBounds;
    private LayoutRect[] snapTargets = [];
    private WidgetPlacement startPlacement = new();
    private bool dragging, keyboardEditing, touchSelected;
    private readonly Avalonia.Threading.DispatcherTimer longPress = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private Point pressPoint, lastPoint;
    private bool touchGesture;
    private LayoutSize minimumSize;
    public WidgetPlacement Placement { get; private set; } = new();
    public LayoutSize NaturalSize { get; private set; } = new(462, 182);
    public bool IsInteracting => dragging;
    public bool ChromeVisible => outline.Opacity > 0;
    public WidgetHandle Mover => mover;
    public IReadOnlyList<WidgetHandle> Resizers => resizers;
    public event Action<WidgetPlacement>? PlacementCommitted;
    public event Action? Activated;
    // Touch has no hover: a tap selects the widget and shows its frame until another target is tapped.
    public event Action? TouchSelected;
    public event Action? LongPressed;
    public bool IsTouchSelected => touchSelected;
    public void ClearTouchSelection() { if (!touchSelected) return; touchSelected = false; ApplyHandleMetrics(); UpdateChrome(); }
    public void CommitPlacement() => PlacementCommitted?.Invoke(Placement);
    public void SelectForEditing() { keyboardEditing = true; mover.Focus(NavigationMethod.Tab); UpdateChrome(); }

    public WidgetFrame()
    {
        outline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), IsHitTestVisible = false, Opacity = 0, ZIndex = 10 };
        outline.Bind(Border.BorderBrushProperty, outline.GetResourceObservable("AccentBrush"));
        Children.Add(outline);
        mover = new WidgetHandle(WidgetEdges.None) { Cursor = new Cursor(StandardCursorType.SizeAll), ZIndex = 1, CornerRadius = new CornerRadius(8) };
        Children.Add(mover);
        AddResizeHandle(WidgetEdges.Left, StandardCursorType.LeftSide, "左邊");
        AddResizeHandle(WidgetEdges.Top, StandardCursorType.TopSide, "上邊");
        AddResizeHandle(WidgetEdges.Right, StandardCursorType.RightSide, "右邊");
        AddResizeHandle(WidgetEdges.Bottom, StandardCursorType.BottomSide, "下邊");
        AddResizeHandle(WidgetEdges.Left | WidgetEdges.Top, StandardCursorType.TopLeftCorner, "左上角");
        AddResizeHandle(WidgetEdges.Right | WidgetEdges.Top, StandardCursorType.TopRightCorner, "右上角");
        AddResizeHandle(WidgetEdges.Left | WidgetEdges.Bottom, StandardCursorType.BottomLeftCorner, "左下角");
        AddResizeHandle(WidgetEdges.Right | WidgetEdges.Bottom, StandardCursorType.BottomRightCorner, "右下角");
        SetInteractionNames("時鐘");
        longPress.Tick += (_, _) => FireLongPress();
        foreach (var handle in resizers.Prepend(mover))
        {
            InputElement.SetIsHoldingEnabled(handle, false);
            handle.PointerPressed += (_, e) =>
            {
                if (surface is null || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
                CancelInteraction();
                touchGesture = e.Pointer.Type is PointerType.Touch or PointerType.Pen;
                if (touchGesture) SelectByTouch();
                pressPoint = lastPoint = e.GetPosition(surface);
                if (touchGesture) longPress.Start();
                keyboardEditing = false;
                handle.Focus(NavigationMethod.Pointer);
                e.Pointer.Capture(handle);
                dragging = true; activeHandle = handle; startPointer = e.GetPosition(surface); startPlacement = Placement; startBounds = Bounds2D;
                snapTargets = surface.Children.OfType<WidgetFrame>().Where(w => w != this && w.IsVisible)
                    .Select(w => w.Bounds2D).Where(r => r.Width > 0 && r.Height > 0).ToArray();
                Activated?.Invoke();
                UpdateChrome();
                e.Handled = true;
            };
            handle.PointerMoved += (_, e) =>
            {
                if (surface is null || !dragging || activeHandle != handle) return;
                lastPoint = e.GetPosition(surface);
                if (Distance(lastPoint, pressPoint) > 10) longPress.Stop();
                var delta = lastPoint - startPointer;
                DragTo(new LayoutVector(delta.X, delta.Y), handle.Edges, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            };
            // Commit before releasing capture: releasing raises PointerCaptureLost, which cancels.
            handle.PointerReleased += (_, e) => { longPress.Stop(); if (activeHandle == handle) { FinishInteraction(false); e.Pointer.Capture(null); } };
            // A lost capture without release (another window, a grab) cancels like WPF's canceled drag.
            handle.PointerCaptureLost += (_, _) => { longPress.Stop(); if (activeHandle == handle) FinishInteraction(true); };
            handle.KeyDown += OnKey;
            handle.GotFocus += (_, e) => { keyboardEditing = e.NavigationMethod != NavigationMethod.Pointer; UpdateChrome(); };
            handle.LostFocus += (_, _) => { keyboardEditing = false; UpdateChrome(); };
        }
        PointerEntered += (_, _) => UpdateChrome();
        PointerExited += (_, _) => UpdateChrome();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            Activated?.Invoke();
            if (e.Pointer.Type is PointerType.Touch or PointerType.Pen) SelectByTouch();
            if (!dragging) { keyboardEditing = false; UpdateChrome(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => CancelInteraction();
    }

    public LayoutRect Bounds2D => new(Canvas.GetLeft(this) is var x && double.IsFinite(x) ? x : 0, Canvas.GetTop(this) is var y && double.IsFinite(y) ? y : 0, Width, Height);
    private LayoutSize CanvasSize => new(surface!.Bounds.Width, surface.Bounds.Height);
    private LayoutSize MinimumSize => new(Math.Max(minimumSize.Width, NaturalSize.Width * .3 + 24), Math.Max(minimumSize.Height, NaturalSize.Height * .3 + 24));
    private LayoutSize MaximumSize => new(NaturalSize.Width * 3 + 24, NaturalSize.Height * 3 + 24);

    private void AddResizeHandle(WidgetEdges edges, StandardCursorType cursor, string name)
    {
        var corner = (edges.HasFlag(WidgetEdges.Left) || edges.HasFlag(WidgetEdges.Right)) && (edges.HasFlag(WidgetEdges.Top) || edges.HasFlag(WidgetEdges.Bottom));
        var horizontal = edges is WidgetEdges.Top or WidgetEdges.Bottom;
        var marker = new Border
        {
            Width = corner ? 7 : horizontal ? 18 : 3, Height = corner ? 7 : horizontal ? 3 : 18, CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        marker.Bind(Border.BackgroundProperty, marker.GetResourceObservable("AccentBrush"));
        var handle = new WidgetHandle(edges)
        {
            Cursor = new Cursor(cursor), Opacity = 0, IsHitTestVisible = false, ZIndex = 20, Child = marker,
            HorizontalAlignment = edges.HasFlag(WidgetEdges.Left) ? HorizontalAlignment.Left : edges.HasFlag(WidgetEdges.Right) ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            VerticalAlignment = edges.HasFlag(WidgetEdges.Top) ? VerticalAlignment.Top : edges.HasFlag(WidgetEdges.Bottom) ? VerticalAlignment.Bottom : VerticalAlignment.Stretch
        };
        handleMarkers[handle] = marker;
        ToolTip.SetTip(handle, name + "縮放；按住 Alt 暫停吸附");
        handle.Tag = name;
        resizers.Add(handle); Children.Add(handle);
        ApplyHandleMetrics(handle);
    }
    private readonly Dictionary<WidgetHandle, Border> handleMarkers = new();
    // Every resize handle sits entirely outside the border, so none covers the move header or the
    // content. A touch selection grows them to finger size.
    private void ApplyHandleMetrics() { foreach (var handle in resizers) ApplyHandleMetrics(handle); }
    private void ApplyHandleMetrics(WidgetHandle handle)
    {
        var edges = handle.Edges;
        var corner = (edges.HasFlag(WidgetEdges.Left) || edges.HasFlag(WidgetEdges.Right)) && (edges.HasFlag(WidgetEdges.Top) || edges.HasFlag(WidgetEdges.Bottom));
        var horizontal = edges is WidgetEdges.Top or WidgetEdges.Bottom;
        var touch = touchSelected;
        var size = corner ? touch ? 36 : 16 : touch ? 28 : 10;
        handle.Margin = new Thickness(edges.HasFlag(WidgetEdges.Left) ? -size : 0, edges.HasFlag(WidgetEdges.Top) ? -size : 0,
            edges.HasFlag(WidgetEdges.Right) ? -size : 0, edges.HasFlag(WidgetEdges.Bottom) ? -size : 0);
        var marker = handleMarkers[handle];
        // The visible marker hugs the border; the rest of the handle is extra reach outward.
        marker.HorizontalAlignment = edges.HasFlag(WidgetEdges.Left) ? HorizontalAlignment.Right : edges.HasFlag(WidgetEdges.Right) ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        marker.VerticalAlignment = edges.HasFlag(WidgetEdges.Top) ? VerticalAlignment.Bottom : edges.HasFlag(WidgetEdges.Bottom) ? VerticalAlignment.Top : VerticalAlignment.Center;
        marker.Margin = new Thickness(2);
        if (corner)
        {
            handle.Width = handle.Height = size;
            marker.Width = marker.Height = touch ? 12 : 8;
        }
        else
        {
            if (horizontal) { handle.Height = size; handle.Width = double.NaN; }
            else { handle.Width = size; handle.Height = double.NaN; }
            marker.Width = horizontal ? touch ? 36 : 22 : touch ? 5 : 3;
            marker.Height = horizontal ? touch ? 5 : 3 : touch ? 36 : 22;
        }
    }
    private void SelectByTouch()
    {
        var changed = !touchSelected;
        touchSelected = true;
        if (changed) { ApplyHandleMetrics(); TouchSelected?.Invoke(); }
        UpdateChrome();
    }
    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    // A held finger that has not moved opens the widget menu, like a right click; nothing is moved or saved.
    public void FireLongPress()
    {
        longPress.Stop();
        if (!touchGesture || Distance(lastPoint, pressPoint) > 10) return;
        CancelInteraction();
        LongPressed?.Invoke();
        if (ContextMenu is { IsOpen: false } menu) menu.Open(this);
    }
    private void SetInteractionNames(string title)
    {
        AutomationProperties.SetName(mover, "拖動" + title + "組件；方向鍵移動，Ctrl 加方向鍵縮放，Alt 暫停吸附，Esc 取消拖曳");
        foreach (var handle in resizers) AutomationProperties.SetName(handle, "調整" + title + "組件" + handle.Tag + "；按住 Alt 暫停吸附");
    }
    private static Line CreateGuide()
    {
        var line = new Line { IsHitTestVisible = false, StrokeThickness = 1, StrokeDashArray = [4, 3], ZIndex = int.MaxValue };
        line.Bind(Shape.StrokeProperty, line.GetResourceObservable("AccentBrush"));
        return line;
    }

    public void DragTo(LayoutVector delta, WidgetEdges edges, bool suppressSnap = false)
    {
        if (surface is null) return;
        var moving = edges == WidgetEdges.None;
        var canvas = CanvasSize;
        var bounds = moving ? new LayoutRect(Math.Clamp(startBounds.X + delta.X, 0, Math.Max(0, canvas.Width - startBounds.Width)),
            Math.Clamp(startBounds.Y + delta.Y, 0, Math.Max(0, canvas.Height - startBounds.Height)), startBounds.Width, startBounds.Height)
            : WidgetGeometry.Resize(startBounds, delta, edges, MinimumSize, canvas, MaximumSize);
        var snap = suppressSnap ? new SnapResult(bounds, null, null)
            : WidgetGeometry.Snap(bounds, canvas, snapTargets, edges, moving, minimum: MinimumSize, maximum: MaximumSize);
        bounds = snap.Bounds;
        if (moving) MoveTo(bounds.X, bounds.Y);
        else
        {
            // Reapply size limits after snapping; the edge opposite the pointer stays fixed.
            var snappedDelta = new LayoutVector(edges.HasFlag(WidgetEdges.Left) ? bounds.Left - startBounds.Left : bounds.Right - startBounds.Right,
                edges.HasFlag(WidgetEdges.Top) ? bounds.Top - startBounds.Top : bounds.Bottom - startBounds.Bottom);
            bounds = WidgetGeometry.Resize(startBounds, snappedDelta, edges, MinimumSize, canvas, MaximumSize);
            Placement = (startPlacement with
            {
                WidthScale = (edges & (WidgetEdges.Left | WidgetEdges.Right)) != WidgetEdges.None && Math.Abs(bounds.Width - startBounds.Width) > 1e-8
                    ? (bounds.Width - 24) / NaturalSize.Width : startPlacement.WidthScale,
                HeightScale = (edges & (WidgetEdges.Top | WidgetEdges.Bottom)) != WidgetEdges.None && Math.Abs(bounds.Height - startBounds.Height) > 1e-8
                    ? (bounds.Height - 24) / NaturalSize.Height : startPlacement.HeightScale
            }).Normalize();
            ArrangeWidget(); MoveTo(bounds.X, bounds.Y);
        }
        ShowGuides(snap, edges, moving);
    }
    public bool GuidesVisible => surface is not null && (surface.Children.Contains(verticalGuide) || surface.Children.Contains(horizontalGuide));
    private void ShowGuides(SnapResult snap, WidgetEdges edges, bool moving)
    {
        ClearGuides();
        var actual = Bounds2D;
        bool Matches(double? guide, double first, double center, double last, bool useFirst, bool useLast) => guide is double value &&
            ((useFirst && Math.Abs(value - first) < .1) || (moving && Math.Abs(value - center) < .1) || (useLast && Math.Abs(value - last) < .1));
        if (Matches(snap.GuideX, actual.Left, actual.Left + actual.Width / 2, actual.Right, moving || edges.HasFlag(WidgetEdges.Left), moving || edges.HasFlag(WidgetEdges.Right)))
        {
            verticalGuide.StartPoint = new Point(snap.GuideX!.Value, 0); verticalGuide.EndPoint = new Point(snap.GuideX.Value, surface!.Bounds.Height);
            surface.Children.Add(verticalGuide);
        }
        if (Matches(snap.GuideY, actual.Top, actual.Top + actual.Height / 2, actual.Bottom, moving || edges.HasFlag(WidgetEdges.Top), moving || edges.HasFlag(WidgetEdges.Bottom)))
        {
            horizontalGuide.StartPoint = new Point(0, snap.GuideY!.Value); horizontalGuide.EndPoint = new Point(surface!.Bounds.Width, snap.GuideY.Value);
            surface.Children.Add(horizontalGuide);
        }
    }
    private void ClearGuides() { surface?.Children.Remove(verticalGuide); surface?.Children.Remove(horizontalGuide); }
    private void FinishInteraction(bool canceled)
    {
        if (!dragging) return;
        dragging = false; activeHandle = null; snapTargets = []; ClearGuides();
        if (canceled) { Placement = startPlacement; ArrangeWidget(); }
        else CommitPlacement();
        UpdateChrome();
    }

    public void Attach(Canvas canvas, Control content, string? title = null)
    {
        surface = canvas; content.Margin = new Thickness(12); content.IsHitTestVisible = title is not null;
        if (title is not null)
        {
            minimumSize = new LayoutSize(240, 230);
            var backing = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            backing.Bind(Border.BackgroundProperty, backing.GetResourceObservable("SheetBrush"));
            backing.Bind(Border.BorderBrushProperty, backing.GetResourceObservable("LineBrush"));
            Children.Insert(0, backing);
            mover.Height = 40; mover.VerticalAlignment = VerticalAlignment.Top;
            content.Margin = new Thickness(12, 40, 12, 12);
            var label = new TextBlock { Text = title, Margin = new Thickness(24, 14, 24, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, FontSize = 14, FontWeight = FontWeight.SemiBold };
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextBrush"));
            Children.Add(label);
            var hint = new TextBlock { Text = "⠿", Margin = new Thickness(0, 12, 22, 0), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, FontSize = 16 };
            hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("MutedBrush"));
            Children.Add(hint);
            SetInteractionNames(title);
        }
        Children.Insert(1, content); canvas.Children.Add(this);
        canvas.SizeChanged += OnSurfaceSizeChanged;
    }
    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) { CancelInteraction(); ArrangeWidget(); }
    public void Detach()
    {
        CancelInteraction();
        if (surface is not null) { surface.SizeChanged -= OnSurfaceSizeChanged; surface.Children.Remove(this); surface = null; }
    }
    public void Configure(WidgetPlacement placement, LayoutSize naturalSize)
    {
        CancelInteraction(); Placement = placement.Normalize(); NaturalSize = naturalSize; ArrangeWidget();
    }
    private void UpdateChrome()
    {
        // Keyboard selections keep the editing frame visible after the pointer leaves the widget.
        var visible = IsPointerOver || dragging || keyboardEditing || touchSelected;
        outline.Opacity = visible ? 1 : 0;
        foreach (var handle in resizers) { handle.Opacity = visible ? 1 : 0; handle.IsHitTestVisible = visible; }
    }
    public void ArrangeWidget()
    {
        if (surface is null) return;
        var canvasWidth = surface.Bounds.Width; var canvasHeight = surface.Bounds.Height;
        var availableW = Math.Max(0, canvasWidth - 24); var availableH = Math.Max(0, canvasHeight - 24);
        var scale = Math.Min(Placement.Scale, Math.Min(availableW / NaturalSize.Width, availableH / NaturalSize.Height));
        Width = Math.Min(canvasWidth, Math.Max(minimumSize.Width, NaturalSize.Width * Math.Max(0, Placement.WidthScale ?? scale) + 24));
        Height = Math.Min(canvasHeight, Math.Max(minimumSize.Height, NaturalSize.Height * Math.Max(0, Placement.HeightScale ?? scale) + 24));
        Canvas.SetLeft(this, Math.Max(0, canvasWidth - Width) * Placement.X);
        Canvas.SetTop(this, Math.Max(0, canvasHeight - Height) * Placement.Y);
    }
    public void MoveTo(double x, double y)
    {
        if (surface is null) return;
        var travelX = surface.Bounds.Width - Width; var travelY = surface.Bounds.Height - Height;
        Placement = (Placement with { X = travelX > 0 ? x / travelX : 0, Y = travelY > 0 ? y / travelY : 0 }).Normalize();
        ArrangeWidget();
    }
    public void ResizeTo(double width, double height, Point anchor)
    {
        if (surface is null || !double.IsFinite(width) || !double.IsFinite(height)) return;
        Placement = (Placement with
        {
            WidthScale = (Math.Min(width, Math.Max(24, surface.Bounds.Width - anchor.X)) - 24) / NaturalSize.Width,
            HeightScale = (Math.Min(height, Math.Max(24, surface.Bounds.Height - anchor.Y)) - 24) / NaturalSize.Height
        }).Normalize();
        ArrangeWidget(); MoveTo(anchor.X, anchor.Y);
    }
    public void CancelInteraction()
    {
        longPress.Stop();
        FinishInteraction(true);
        ClearGuides();
    }
    // Test and automation entry points mirror a pointer gesture without a device.
    public void BeginGesture(WidgetHandle handle)
    {
        if (surface is null) return;
        CancelInteraction();
        dragging = true; activeHandle = handle; startPlacement = Placement; startBounds = Bounds2D;
        snapTargets = surface.Children.OfType<WidgetFrame>().Where(w => w != this && w.IsVisible).Select(w => w.Bounds2D).Where(r => r.Width > 0 && r.Height > 0).ToArray();
        UpdateChrome();
    }
    public void CompleteGesture(bool canceled) => FinishInteraction(canceled);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        keyboardEditing = true; UpdateChrome();
        if (e.Key == Key.Escape && dragging) { CancelInteraction(); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
        if (dragging) { e.Handled = true; return; }
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 20 : 5;
        var left = Canvas.GetLeft(this); var top = Canvas.GetTop(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ResizeTo(
            Width + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0),
            Height + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0), new Point(left, top));
        else MoveTo(left + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0), top + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0));
        CommitPlacement(); e.Handled = true;
    }
}
