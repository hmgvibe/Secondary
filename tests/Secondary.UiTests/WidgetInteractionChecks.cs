using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Secondary.App.Presentation;
using Secondary.Core;
using Path = System.IO.Path;

// Off-screen routed events and programmatic controller calls. These never move the
// system pointer, send native input, create a visible window, or claim physical drag coverage.
internal static class WidgetInteractionChecks
{
    private static readonly ResizeEdges[] Edges = [ResizeEdges.Left, ResizeEdges.Top, ResizeEdges.Right, ResizeEdges.Bottom,
        ResizeEdges.Left | ResizeEdges.Top, ResizeEdges.Right | ResizeEdges.Top,
        ResizeEdges.Left | ResizeEdges.Bottom, ResizeEdges.Right | ResizeEdges.Bottom];
    private static readonly MethodInfo DragController = typeof(HomeWidget).GetMethod("DragTo", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(Action<bool, string> check, string? outputDirectory = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.WidgetInteractions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var canvas = Surface(1120, 840);
        var editor = new TextBox { Text = "未儲存的草稿", AcceptsReturn = true, Background = Brushes.White, Foreground = Brushes.Black };
        editor.Select(1, 3);
        var button = new Button { Content = "保存", Height = 38, Background = Brushes.LightGray, Foreground = Brushes.Black };
        var content = new Grid { Background = Brushes.White };
        content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(button, 1); content.Children.Add(editor); content.Children.Add(button);
        var frame = new HomeWidget(); var peer = new HomeWidget();
        frame.Attach(canvas, content, "測試便箋"); peer.Attach(canvas, new TextBlock { Text = "對齊目標", Foreground = Brushes.Black }, "目標");
        Panel.SetZIndex(frame, 1);
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        var commits = 0;
        frame.PlacementCommitted += placement =>
        {
            commits++;
            if (!store.Save(new AppSettings(Home: new HomeSettings(Widgets:
                [new HomeWidgetSettings("fixture", "Note", placement, Text: "已保存內容")])))) throw new IOException(store.LastError);
        };
        try
        {
            Reset(frame, peer, canvas);
            var thumbs = frame.Children.OfType<Thumb>().ToArray();
            check(thumbs.Length == 9 && thumbs.All(t => t.Tag is ResizeEdges), "Widget has one tagged mover and eight tagged resize handles");
            var mover = thumbs.Single(t => (ResizeEdges)t.Tag == ResizeEdges.None);
            var handles = thumbs.Where(t => (ResizeEdges)t.Tag != ResizeEdges.None).ToDictionary(t => (ResizeEdges)t.Tag);
            check(handles.Keys.OrderBy(e => (int)e).SequenceEqual(Edges.OrderBy(e => (int)e)), "All four edge and four corner handles are distinct");
            check(content.IsHitTestVisible && mover.Height == 40, "Utility content stays interactive below its separate move header");
            foreach (var edge in Edges)
            {
                var thumb = handles[edge]; var bounds = thumb.TransformToAncestor(frame).TransformBounds(new Rect(thumb.RenderSize));
                check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= frame.Width + .1 && bounds.Bottom <= frame.Height + .1,
                    edge + " resize hit area stays inside the frame");
                check(thumb.Cursor == CursorFor(edge) && AutomationProperties.GetName(thumb).Contains("測試便箋"), edge + " handle has the matching cursor and accessible widget name");
                check(Ancestor<Thumb>(Hit(frame, Center(bounds))) == thumb, edge + " handle wins off-screen hit testing over content and header");
                check(Panel.GetZIndex(thumb) > Panel.GetZIndex(mover) && thumb.Focusable, edge + " handle is above the move layer and keyboard selectable");
            }
            check(Ancestor<TextBox>(Hit(frame, editor.TranslatePoint(new Point(editor.ActualWidth / 2, editor.ActualHeight / 2), frame))) == editor,
                "Resize chrome does not intercept the off-screen note editor center");
            check(Ancestor<Button>(Hit(frame, button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), frame))) == button,
                "Resize chrome does not intercept the off-screen content action button");
            check(Ancestor<Thumb>(Hit(frame, new Point(frame.Width / 2, 20))) == mover, "The header routes off-screen hits to the mover");

            foreach (var edge in Edges)
            {
                Reset(frame, peer, canvas);
                var before = Bounds(frame); var count = commits; var thumb = handles[edge];
                Begin(thumb);
                check(frame.IsInteracting && handles.Values.All(t => t.Opacity == 1), edge + " routed drag start exposes every resize handle");
                var delta = new Vector(edge.HasFlag(ResizeEdges.Left) ? -44 : edge.HasFlag(ResizeEdges.Right) ? 44 : 155,
                    edge.HasFlag(ResizeEdges.Top) ? -36 : edge.HasFlag(ResizeEdges.Bottom) ? 36 : 97);
                Drag(frame, delta, edge); Layout(canvas, 1120, 840);
                var after = Bounds(frame);
                var horizontal = edge.HasFlag(ResizeEdges.Left) || edge.HasFlag(ResizeEdges.Right);
                var vertical = edge.HasFlag(ResizeEdges.Top) || edge.HasFlag(ResizeEdges.Bottom);
                check(Near(after.Width, before.Width + (horizontal ? 44 : 0)) && Near(after.Height, before.Height + (vertical ? 36 : 0)),
                    edge + " programmatic controller resizes only its tagged axes");
                check((!edge.HasFlag(ResizeEdges.Left) || Near(after.Right, before.Right)) && (!edge.HasFlag(ResizeEdges.Right) || Near(after.Left, before.Left)) &&
                    (!edge.HasFlag(ResizeEdges.Top) || Near(after.Bottom, before.Bottom)) && (!edge.HasFlag(ResizeEdges.Bottom) || Near(after.Top, before.Top)), edge + " controller keeps opposite edges fixed");
                check(editor.Text == "未儲存的草稿" && editor.SelectionStart == 1 && editor.SelectionLength == 3 && content.Children.Contains(editor), edge + " interaction preserves draft controls and selection");
                if (outputDirectory is not null && edge == (ResizeEdges.Right | ResizeEdges.Bottom)) Save(canvas, outputDirectory, "widget-edit-handles.png");
                Complete(thumb, false);
                check(!frame.IsInteracting && commits == count + 1 && !Guides(canvas).Any(), edge + " routed completion commits exactly once and removes guides");
                check(store.Load().Home!.Widgets!.Single().Placement == frame.Placement, edge + " committed placement survives settings reload");
                Complete(thumb, false); check(commits == count + 1, edge + " duplicate completion cannot write another placement");
            }

            Reset(frame, peer, canvas); frame.CommitPlacement();
            var original = frame.Placement; var originalPersisted = store.Load().Home!.Widgets!.Single().Placement; var originalCount = commits;
            Begin(mover); Drag(frame, new Vector(426, -249), ResizeEdges.None); Layout(canvas, 1120, 840);
            check(Guides(canvas).Count() == 2 && Guides(canvas).All(l => !l.IsHitTestVisible && !l.Focusable && Panel.GetZIndex(l) > Panel.GetZIndex(frame)),
                "Programmatic alignment exposes both non-interactive guides above widget chrome");
            check(Near(Canvas.GetLeft(frame), Canvas.GetLeft(peer)) && Near(Canvas.GetTop(frame), Canvas.GetTop(peer)), "Move controller snaps to the nearby peer's shared axes");
            check(Ancestor<TextBox>(Hit(frame, editor.TranslatePoint(new Point(editor.ActualWidth / 2, editor.ActualHeight / 2), frame))) == editor,
                "Alignment guides leave content hit testing intact");
            if (outputDirectory is not null) Save(canvas, outputDirectory, "widget-edit-snapping.png");
            var source = new KeySource(canvas);
            var arrow = Key(mover, source, System.Windows.Input.Key.Right);
            check(arrow.Handled && commits == originalCount && store.Load().Home!.Widgets!.Single().Placement == originalPersisted,
                "Arrow keys during a drag cannot commit an intermediate placement");
            var escape = Key(mover, source, System.Windows.Input.Key.Escape);
            check(escape.Handled && !frame.IsInteracting && frame.Placement == original && commits == originalCount && !Guides(canvas).Any(),
                "Routed Escape restores the starting placement without saving and removes guides");
            check(store.Load().Home!.Widgets!.Single().Placement == originalPersisted, "Escape keeps the original persisted placement");
            Complete(mover, false); check(commits == originalCount, "A late completion after Escape cannot commit the canceled drag");

            foreach (var edge in Edges)
            {
                Reset(frame, peer, canvas); var placement = frame.Placement; var count = commits;
                Begin(handles[edge]); Drag(frame, new Vector(25, 20), edge); Complete(handles[edge], true);
                check(!frame.IsInteracting && frame.Placement == placement && commits == count && !Guides(canvas).Any(), edge + " canceled native completion restores placement without committing");
            }
            Reset(frame, peer, canvas); original = frame.Placement; originalCount = commits;
            Begin(mover); Drag(frame, new Vector(426, -249), ResizeEdges.None); Layout(canvas, 980, 760);
            check(!frame.IsInteracting && frame.Placement == original && commits == originalCount && !Guides(canvas).Any(), "Canvas resize cancels a gesture and clears its transient alignment guides");
            Reset(frame, peer, canvas); Begin(mover); Drag(frame, new Vector(426, -249), ResizeEdges.None);
            var replacement = new WidgetPlacement(.2, .3, 1.1);
            frame.Configure(replacement, new Size(320, 300)); Layout(canvas, 1120, 840);
            check(!frame.IsInteracting && frame.Placement == replacement && !Guides(canvas).Any(), "Configuration replacement cancels the previous gesture and clears guides");

            Reset(frame, peer, canvas); peer.MoveTo(356, 90); Begin(handles[ResizeEdges.Left]); Drag(frame, new Vector(150, 0), ResizeEdges.Left);
            check(Near(Bounds(frame).Left, 354) && !Guides(canvas).Any(), "Reclamping a snapped minimum width cannot leave a guide at the rejected edge");
            Complete(handles[ResizeEdges.Left], true);
            Reset(frame, peer, canvas); frame.MoveTo(70, 340); peer.ResizeTo(240, 324, new Point(816, 90));
            Begin(handles[ResizeEdges.Right]); Drag(frame, new Vector(1000, 0), ResizeEdges.Right);
            check(Near(Bounds(frame).Right, 1054) && !Guides(canvas).Any(), "Reclamping a snapped maximum width cannot leave a guide at the rejected edge");
            Complete(handles[ResizeEdges.Right], true);

            Reset(frame, peer, canvas); Begin(mover); Drag(frame, new Vector(426, -249), ResizeEdges.None); originalCount = commits;
            frame.Detach(); check(!frame.IsInteracting && !canvas.Children.Contains(frame) && !Guides(canvas).Any() && commits == originalCount, "Detach cancels editing, clears guides and removes the frame without saving");
            mover.RaiseEvent(new DragDeltaEventArgs(30, 20) { RoutedEvent = Thumb.DragDeltaEvent }); Complete(mover, false);
            check(commits == originalCount && !canvas.Children.Contains(frame), "Detached stale routed drag events cannot write or reattach the widget");
            CheckSmallCanvases(check);
        }
        finally { frame.Detach(); peer.Detach(); Directory.Delete(directory, true); }
    }
    private static void CheckSmallCanvases(Action<bool, string> check)
    {
        var fractional = Surface(801.5, 600); var frame = new HomeWidget();
        frame.Attach(fractional, new TextBlock { Text = "clock" }); frame.Configure(new WidgetPlacement(), new Size(777, 182));
        Layout(fractional, 801.5, 600); frame.MoveTo(.5, 0);
        check(Near(frame.Width, 801) && Near(Canvas.GetLeft(frame), .5, .00001) && frame.Placement.X == 1, "Fractional travel below one DIP still reaches the exact canvas edge");
        frame.Detach();
        var tiny = Surface(1100, 50); frame = new HomeWidget();
        frame.Attach(tiny, new TextBlock { Text = "clock" }); frame.Configure(new WidgetPlacement(), new Size(462, 182)); frame.MoveTo(0, 0); Layout(tiny, 1100, 50);
        var before = Bounds(frame); var left = frame.Children.OfType<Thumb>().Single(t => (ResizeEdges)t.Tag == ResizeEdges.Left);
        Begin(left); Drag(frame, new Vector(-10, 0), ResizeEdges.Left); Layout(tiny, 1100, 50);
        check(Near(before.Width, 90) && Near(Bounds(frame).Width, before.Width) && Near(Bounds(frame).Right, before.Right) && Near(frame.Height, before.Height),
            "Tiny fitted clock keeps its fixed opposite edge when minimum resize cannot fit");
        check(frame.Placement.WidthScale is null && frame.Placement.HeightScale is null, "An unchanged fitted axis keeps its original proportional sizing policy");
        Complete(left, true); frame.Detach();
    }
    private static Canvas Surface(double width, double height)
    {
        var canvas = new Canvas { Background = Brushes.WhiteSmoke };
        canvas.Resources["AccentBrush"] = Brushes.DodgerBlue; canvas.Resources["SheetBrush"] = Brushes.White;
        canvas.Resources["LineBrush"] = Brushes.SlateGray; canvas.Resources["TextBrush"] = Brushes.Black; canvas.Resources["MutedBrush"] = Brushes.Gray;
        Layout(canvas, width, height); return canvas;
    }
    private static void Reset(HomeWidget frame, HomeWidget peer, Canvas canvas)
    {
        Layout(canvas, 1120, 840); frame.Configure(new WidgetPlacement(), new Size(320, 300)); peer.Configure(new WidgetPlacement(), new Size(320, 300));
        frame.MoveTo(250, 340); peer.MoveTo(680, 90); Layout(canvas, 1120, 840);
    }
    private static void Begin(Thumb thumb) => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
    private static void Complete(Thumb thumb, bool canceled) => thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
    private static void Drag(HomeWidget frame, Vector delta, ResizeEdges edges) => DragController.Invoke(frame, [delta, edges]);
    private static KeyEventArgs Key(Thumb thumb, PresentationSource source, System.Windows.Input.Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        thumb.RaiseEvent(args); return args;
    }
    private static System.Collections.Generic.IEnumerable<Line> Guides(Canvas canvas) => canvas.Children.OfType<Line>();
    private static Rect Bounds(HomeWidget frame) => new(Canvas.GetLeft(frame), Canvas.GetTop(frame), frame.Width, frame.Height);
    private static Point Center(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static bool Near(double a, double b, double tolerance = .1) => Math.Abs(a - b) < tolerance;
    private static Cursor CursorFor(ResizeEdges edges) => edges is ResizeEdges.Left or ResizeEdges.Right ? Cursors.SizeWE : edges is ResizeEdges.Top or ResizeEdges.Bottom ? Cursors.SizeNS :
        edges == (ResizeEdges.Left | ResizeEdges.Top) || edges == (ResizeEdges.Right | ResizeEdges.Bottom) ? Cursors.SizeNWSE : Cursors.SizeNESW;
    private static DependencyObject? Hit(Visual root, Point point)
    {
        DependencyObject? hit = null;
        // Match input visibility/layer filtering without requiring a native presentation source.
        VisualTreeHelper.HitTest(root, node => node is UIElement element && (!element.IsHitTestVisible || element.Visibility != Visibility.Visible)
            ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        return hit;
    }
    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T target) return target; node = VisualTreeHelper.GetParent(node); }
        return null;
    }
    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Width = width; element.Height = height; element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
    }
    private static void Save(Canvas canvas, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    // Only supplies a non-null source to routed KeyEventArgs; it never connects to a HWND.
    private sealed class KeySource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
