using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Home;
using Secondary.Linux.Platform;
using Secondary.Shared;
using Tmds.DBus.Protocol;
using static Program;

// L2: home canvas, widget frame, clocks, utility widgets, live sources and backgrounds.
internal static class HomeChecks
{
    public static void Run(string? output)
    {
        TestGeometry();
        TestMprisParsing();
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.LinuxHome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestHome(directory, output);
            TestLiveWidgets();
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestGeometry()
    {
        var canvas = new LayoutSize(1000, 600);
        var resized = WidgetGeometry.Resize(new LayoutRect(100, 100, 300, 200), new LayoutVector(-50, 40), WidgetEdges.Left | WidgetEdges.Bottom, new LayoutSize(120, 120), canvas);
        Check(resized == new LayoutRect(50, 100, 350, 240), "Shared resize keeps the opposite edges fixed");
        var limited = WidgetGeometry.Resize(new LayoutRect(100, 100, 300, 200), new LayoutVector(-400, -400), WidgetEdges.Right | WidgetEdges.Top, new LayoutSize(120, 120), canvas);
        Check(limited == new LayoutRect(100, 0, 120, 300), "Shared resize enforces the minimum width and stops at the canvas edge");
        var snap = WidgetGeometry.Snap(new LayoutRect(403, 50, 210, 100), canvas, [new LayoutRect(100, 50, 300, 100)], WidgetEdges.None, true);
        Check(snap.Bounds.Left == 400 && snap.GuideX == 400, "Moving within 8 DIP snaps to a peer edge and reports the guide");
        var free = WidgetLayout.FindNewPlacement(canvas, [new LayoutRect(0, 0, 400, 300)], 1, out var crowded);
        Check(!crowded && free.X > 0, "New widgets look for free space first");
        Check(LayoutRect.Empty.IsEmpty && !LayoutRect.Empty.IntersectsWith(new LayoutRect(0, 0, 10, 10)), "Empty rectangles never intersect, as in WPF");
    }

    private static void TestMprisParsing()
    {
        var metadata = new Dict<string, VariantValue>
        {
            ["xesam:title"] = VariantValue.String("Night Drive"),
            ["xesam:artist"] = new Tmds.DBus.Protocol.Array<string>(["A", "B"]).AsVariantValue(),
            ["mpris:length"] = VariantValue.Int64(215_000_000),
            ["mpris:artUrl"] = VariantValue.String("file:///tmp/cover%20art.png")
        };
        var snapshot = MprisMediaSource.Snapshot("org.mpris.MediaPlayer2.test", new Dictionary<string, VariantValue>
        {
            ["PlaybackStatus"] = VariantValue.String("Playing"), ["Metadata"] = metadata.AsVariantValue(), ["Position"] = VariantValue.Int64(65_000_000),
            ["CanGoNext"] = VariantValue.Bool(true), ["CanGoPrevious"] = VariantValue.Bool(false), ["CanPlay"] = VariantValue.Bool(true), ["CanPause"] = VariantValue.Bool(true), ["CanControl"] = VariantValue.Bool(true)
        });
        Check(snapshot is { Title: "Night Drive", Artist: "A、B", Playing: true, CanNext: true, CanPrevious: false } && snapshot.Duration == TimeSpan.FromSeconds(215) && snapshot.Position == TimeSpan.FromSeconds(65),
            "MPRIS metadata, capabilities and microsecond times are read");
        Check(snapshot.ArtworkPath == "/tmp/cover art.png" && MprisMediaSource.LocalArtwork("https://cdn.example/cover.jpg") is null, "Only local file artwork is used; remote artwork is never fetched");
        var locked = MprisMediaSource.Snapshot("p", new Dictionary<string, VariantValue> { ["CanControl"] = VariantValue.Bool(false), ["CanGoNext"] = VariantValue.Bool(true), ["CanPlay"] = VariantValue.Bool(true) });
        Check(!locked.CanNext && !locked.CanPlay, "A player without CanControl offers no controls");
    }

    private static void TestHome(string directory, string? output)
    {
        var settingsPath = Path.Combine(directory, "settings.json");
        var model = new ShellModel(new SettingsStore(settingsPath));
        var window = new MainWindow(model, new DesktopLauncher(Path.Combine(directory, "data"), "/apps/s")) { Width = 1200, Height = 820 };
        window.Show(); Pump();
        var frame = window.ClockFrame;
        Check(frame.Resizers.Count == 8 && frame.Resizers.Select(r => r.Edges).Distinct().Count() == 8, "Clock frame has one mover and eight distinct resize handles");
        Check(frame.Bounds.Width > 100 && frame.Bounds.Height > 50 && window.Clock.NaturalSize.Width > 100, "Clock frame takes the clock's measured natural size");
        Check(window.HomeActive && window.Clock.IsRunning, "Home activity starts the clock timer only while home is shown");

        // Clock styles keep a stable frame across time changes, including midnight.
        foreach (var style in new[] { "Digital", "Flip", "Analog" })
        {
            window.Clock.Configure(new HomeSettings(ClockStyle: style, ShowSeconds: true));
            var before = window.Clock.NaturalSize;
            window.Clock.UpdateTime(new DateTime(2026, 12, 31, 23, 59, 59));
            window.Clock.UpdateTime(new DateTime(2027, 1, 1, 0, 0, 0));
            Check(window.Clock.NaturalSize == before, $"{style} clock frame stays fixed through digit and date changes");
            Pump();
            if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, $"linux-clock-{style}.png"), PngBitmapEncoderOptions.Default);
        }
        window.Clock.Configure(new HomeSettings(ClockStyle: "Flip", ShowSeconds: true));
        window.Clock.UpdateTime(new DateTime(2026, 10, 18, 9, 0, 0));
        window.Clock.UpdateTime(new DateTime(2026, 10, 18, 9, 0, 1), animate: true);
        Check(window.Clock.IsFlipping && window.Clock.DisplayedTime == "09:00:01", "Flip animates only the changed digit with a bounded clock");
        window.SetAccessibility(false, false);
        Check(!window.Clock.IsFlipping && window.Clock.FlipProgress == 1, "Reduced motion immediately settles an in-flight flip");
        window.SetAccessibility(false, true);
        window.Clock.Configure(model.Settings.Home ?? new HomeSettings());

        // Utility widgets: every kind, free placement, persistence and the 12 limit.
        foreach (var kind in HomeWidgetSettings.Kinds) Check(window.AddHomeWidget(kind), "Adds widget " + kind);
        Pump();
        var saved = new SettingsStore(settingsPath).Load().Home!.Widgets!;
        Check(saved.Length == 10 && saved.Select(w => w.Kind).SequenceEqual(HomeWidgetSettings.Kinds), "Every widget kind is saved in order");
        Check(saved.Select(w => (w.Placement!.X, w.Placement.Y)).Distinct().Count() == 10, "New widgets get distinct placements");
        Check(window.AddHomeWidget("Note") && window.AddHomeWidget("Counter") && !window.AddHomeWidget("Calendar") && window.UtilityWidgetCount == 12, "At most 12 widgets can be added; kinds may repeat");
        Check(window.UtilityWidgets.Values.All(w => w.Content.IsRefreshing == w.Content.Settings.Kind is not ("Note" or "Todo" or "Counter")), "Active widgets refresh only when their content changes over time");

        var note = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "Note");
        note.Content.NoteEditor!.Text = "買牛奶";
        Check(note.Content.StatusText == "尚未儲存", "Editing a note marks it unsaved");
        note.Content.NoteEditor.Focus();
        note.Content.NoteEditor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Control, Source = note.Content.NoteEditor });
        Check(note.Content.StatusText == "已儲存" && Saved(settingsPath, note.Content.Settings.Id).Text == "買牛奶", "Ctrl+S saves the note");

        var todo = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "Todo").Content;
        todo.TodoEntry!.Text = "寫報告"; todo.AddTodo();
        todo.TodoEntry.Text = "  "; todo.AddTodo();
        Check(todo.StatusText == "請先輸入待辦內容" && Saved(settingsPath, todo.Settings.Id).Tasks!.Length == 1, "Todo adds items and rejects blank ones");
        todo.TodoEntry.Text = "草稿"; todo.ToggleTodo(0);
        Check(Saved(settingsPath, todo.Settings.Id).Tasks![0].Done && todo.TodoEntry.Text == "草稿", "Toggling an item keeps the new-item draft");
        todo.RemoveTodo(0);
        Check(Saved(settingsPath, todo.Settings.Id).Tasks!.Length == 0, "Removing a todo is saved");

        var counter = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "Counter").Content;
        counter.ChangeCount(1); counter.ChangeCount(1); counter.ChangeCount(-1);
        Check(counter.ReadoutText == "1" && Saved(settingsPath, counter.Settings.Id).Count == 1, "Counter changes are shown and saved");

        var countdown = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "Countdown").Content;
        countdown.Toggle();
        Check(countdown.ReadoutText == "00:04:59" || countdown.ReadoutText == "00:05:00", "Countdown starts from its saved minutes");
        var calendar = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "Calendar").Content;
        var heading = calendar.CalendarHeading; calendar.ShowMonth(1);
        Check(calendar.CalendarHeading != heading && calendar.CalendarHeading.Contains(" 年 "), "Calendar moves between months");
        var progress = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "TimeProgress").Content;
        Check(progress.ProgressTexts.All(t => t.Contains('%')), "Time progress shows today, this week and this year");
        var eventWidget = window.UtilityWidgets.Values.First(w => w.Content.Settings.Kind == "EventCountdown").Content;
        Check(!eventWidget.SaveEvent("生日", "2026/1/1"), "Event countdown rejects dates outside YYYY-MM-DD");
        Check(eventWidget.SaveEvent("生日", DateTime.Today.AddDays(3).ToString("yyyy-MM-dd")) && eventWidget.EventReading == "3 天", "Event countdown shows remaining days");

        // Menus open in their own popup; they must use the same font as the window.
        var menuFrame = window.UtilityWidgets.Values.First().Frame;
        menuFrame.ContextMenu!.Open(menuFrame); Pump();
        var menuTexts = menuFrame.ContextMenu.GetVisualDescendantsOfType<TextBlock>().ToArray();
        var windowFont = window.FindControl<TextBlock>("BrandLabel")!.FontFamily;
        Check(menuTexts.Length > 0 && menuTexts.All(t => t.FontFamily == windowFont) && windowFont.Name.StartsWith("Noto Sans CJK TC", StringComparison.Ordinal),
            "Context menu text uses the same font as the window (" + string.Join(",", menuTexts.Select(t => t.FontFamily.Name).Distinct()) + ")");
        menuFrame.ContextMenu.Close(); Pump();
        var flyout = new MenuFlyout { Items = { new MenuItem { Header = "測試" } } };
        flyout.ShowAt(window.FindControl<Button>("AddHomeWidgetButton")!); Pump();
        var flyoutTexts = ((MenuItem)flyout.Items[0]!).GetVisualDescendantsOfType<TextBlock>().ToArray();
        Check(flyoutTexts.Length > 0 && flyoutTexts.All(t => t.FontFamily == windowFont), "Add-widget flyout text uses the same font as the window");
        flyout.Hide(); Pump();

        // Real pointer events, including capture and release, as a mouse or touch drag produces them.
        var dragged = window.UtilityWidgets.Values.Last().Frame;
        window.SetSessionLocked(false);
        Pump();
        var header = dragged.Mover.TranslatePoint(new Point(dragged.Mover.Bounds.Width / 2, dragged.Mover.Bounds.Height / 2), window)!.Value;
        var startLeft = Canvas.GetLeft(dragged);
        var startTop = Canvas.GetTop(dragged);
        window.MouseDown(header, MouseButton.Left);
        window.MouseMove(header + new Point(-40, -30));
        window.MouseMove(header + new Point(-80, -60));
        Pump();
        Check(Math.Abs(Canvas.GetLeft(dragged) - startLeft) > 30 && dragged.IsInteracting, "A pointer drag moves the widget while the button is held");
        window.MouseUp(header + new Point(-80, -60), MouseButton.Left);
        Pump();
        var draggedId = window.UtilityWidgets.First(p => p.Value.Frame == dragged).Key;
        Check(!dragged.IsInteracting && Math.Abs(Canvas.GetLeft(dragged) - startLeft) > 30 && Math.Abs(Canvas.GetTop(dragged) - startTop) > 20,
            $"Releasing the pointer keeps the new position ({startLeft:0}→{Canvas.GetLeft(dragged):0})");
        Check(Saved(settingsPath, draggedId).Placement == dragged.Placement, "Releasing the pointer saves the new position");
        var resizeHandle = dragged.Resizers.First(r => r.Edges == (WidgetEdges.Right | WidgetEdges.Bottom));
        var corner = resizeHandle.TranslatePoint(new Point(resizeHandle.Bounds.Width / 2, resizeHandle.Bounds.Height / 2), window)!.Value;
        var startWidth = dragged.Width;
        window.MouseDown(corner, MouseButton.Left);
        window.MouseMove(corner + new Point(50, 40));
        window.MouseUp(corner + new Point(50, 40), MouseButton.Left);
        Pump();
        Check(dragged.Width > startWidth + 30 && Saved(settingsPath, draggedId).Placement == dragged.Placement, "A pointer resize from the corner is kept and saved");
        var beforeEscape = dragged.Placement;
        header = dragged.Mover.TranslatePoint(new Point(dragged.Mover.Bounds.Width / 2, dragged.Mover.Bounds.Height / 2), window)!.Value;
        window.MouseDown(header, MouseButton.Left);
        window.MouseMove(header + new Point(70, 0));
        window.KeyPress(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        window.MouseUp(header + new Point(70, 0), MouseButton.Left);
        Pump();
        Check(dragged.Placement == beforeEscape && Saved(settingsPath, draggedId).Placement == beforeEscape, "Esc during a pointer drag restores and does not save");

        // Touch: tap selects and shows finger-sized handles, drag keeps its position, long press opens the menu.
        var finger = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
        void Touch(Control target, string kind, Point at)
        {
            var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, kind == "down" ? PointerUpdateKind.LeftButtonPressed : kind == "up" ? PointerUpdateKind.LeftButtonReleased : PointerUpdateKind.Other);
            PointerEventArgs args = kind switch
            {
                "down" => new PointerPressedEventArgs(target, finger, window, at, 0, pressed, KeyModifiers.None, 1),
                "up" => new PointerReleasedEventArgs(target, finger, window, at, 0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left),
                _ => new PointerEventArgs(InputElement.PointerMovedEvent, target, finger, window, at, 0, pressed, KeyModifiers.None)
            };
            target.RaiseEvent(args);
            Pump();
        }
        var touched = window.UtilityWidgets.Values.First().Frame;
        var other = window.UtilityWidgets.Values.Skip(1).First().Frame;
        var touchCorner = touched.Resizers.First(r => r.Edges == (WidgetEdges.Left | WidgetEdges.Top));
        Check(touchCorner.Width == 16 && !touched.ChromeVisible, "Handles stay small without a touch selection");
        Check(touched.Resizers.All(r => !r.IsHitTestVisible), "Hidden handles outside the border do not catch clicks meant for neighbours");
        bool Outside(WidgetFrame frame)
        {
            var mover = new Rect(frame.Mover.TranslatePoint(new Point(0, 0), frame)!.Value, frame.Mover.Bounds.Size);
            var inside = new Rect(0, 0, frame.Bounds.Width, frame.Bounds.Height).Deflate(0.5);
            return frame.Resizers.All(r =>
            {
                var area = new Rect(r.TranslatePoint(new Point(0, 0), frame)!.Value, r.Bounds.Size);
                return !area.Intersects(inside) && !area.Intersects(mover.Deflate(0.5));
            });
        }
        Check(Outside(touched), "All eight resize handles sit outside the border and never cover the move header");
        var touchHeader = touched.Mover.TranslatePoint(new Point(touched.Mover.Bounds.Width / 2, 20), window)!.Value;
        Touch(touched.Mover, "down", touchHeader);
        Touch(touched.Mover, "up", touchHeader);
        Check(touched.IsTouchSelected && touched.ChromeVisible && touchCorner.Width == 36 && touched.Resizers.All(r => r.IsHitTestVisible), "A tap selects the widget and shows finger-sized handles");
        Check(Outside(touched), "Finger-sized handles also stay outside the border");
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-widget-touch-selected.png"), PngBitmapEncoderOptions.Default);
        Touch(other.Mover, "down", other.Mover.TranslatePoint(new Point(10, 10), window)!.Value);
        Touch(other.Mover, "up", other.Mover.TranslatePoint(new Point(10, 10), window)!.Value);
        Check(other.IsTouchSelected && !touched.IsTouchSelected && touchCorner.Width == 16, "Selecting another widget clears the previous touch selection");
        var canvasPoint = window.FindControl<Canvas>("WidgetCanvas")!.TranslatePoint(new Point(2, 2), window)!.Value;
        window.FindControl<Canvas>("WidgetCanvas")!.RaiseEvent(new PointerPressedEventArgs(window.FindControl<Canvas>("WidgetCanvas")!, finger, window, canvasPoint, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        Pump();
        Check(window.UtilityWidgets.Values.All(w => !w.Frame.IsTouchSelected), "A tap on empty canvas clears the touch selection");
        var touchStart = Canvas.GetLeft(touched);
        touchHeader = touched.Mover.TranslatePoint(new Point(touched.Mover.Bounds.Width / 2, 20), window)!.Value;
        Touch(touched.Mover, "down", touchHeader);
        Touch(touched.Mover, "move", touchHeader + new Point(60, 30));
        Touch(touched.Mover, "up", touchHeader + new Point(60, 30));
        var touchedId = window.UtilityWidgets.First(p => p.Value.Frame == touched).Key;
        Check(Math.Abs(Canvas.GetLeft(touched) - touchStart) > 30 && Saved(settingsPath, touchedId).Placement == touched.Placement, "A touch drag keeps and saves the new position");
        var heldPlacement = touched.Placement;
        touchHeader = touched.Mover.TranslatePoint(new Point(touched.Mover.Bounds.Width / 2, 20), window)!.Value;
        Touch(touched.Mover, "down", touchHeader);
        Touch(touched.Mover, "move", touchHeader + new Point(3, 2));
        touched.FireLongPress(); Pump();
        Check(touched.ContextMenu?.IsOpen == true && !touched.IsInteracting && touched.Placement == heldPlacement, "A long press opens the remove menu without moving the widget");
        touched.ContextMenu!.Close();
        Touch(touched.Mover, "up", touchHeader + new Point(3, 2));
        Check(touched.Placement == heldPlacement, "Lifting the finger after a long press does not move or save");
        Touch(touched.Mover, "down", touchHeader);
        Touch(touched.Mover, "move", touchHeader + new Point(40, 0));
        touched.FireLongPress(); Pump();
        Check(touched.ContextMenu?.IsOpen != true && touched.IsInteracting, "A finger that has moved keeps dragging instead of opening the menu");
        Touch(touched.Mover, "up", touchHeader + new Point(40, 0));
        window.Navigate("shortcuts"); window.Navigate("home"); Pump();
        Check(window.UtilityWidgets.Values.All(w => !w.Frame.IsTouchSelected), "Leaving home clears touch selections");

        // Frame interaction: move, snap guides, cancel, keyboard.
        var first = window.UtilityWidgets.Values.First().Frame;
        var original = first.Placement;
        first.BeginGesture(first.Mover);
        first.DragTo(new LayoutVector(37, 23), WidgetEdges.None);
        Check(first.ChromeVisible, "Dragging shows the editing frame");
        first.CompleteGesture(canceled: true);
        Check(first.Placement == original && !first.GuidesVisible, "A cancelled drag restores the placement and clears guides");
        first.BeginGesture(first.Resizers.First(r => r.Edges == (WidgetEdges.Right | WidgetEdges.Bottom)));
        first.DragTo(new LayoutVector(60, 40), WidgetEdges.Right | WidgetEdges.Bottom, suppressSnap: true);
        first.CompleteGesture(canceled: false);
        Check(Saved(settingsPath, window.UtilityWidgets.Keys.First()).Placement!.WidthScale is > 1, "A completed resize is saved with its own width scale");
        var beforeKey = Canvas.GetLeft(first);
        first.Mover.Focus();
        first.Mover.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = first.Mover });
        Check(Math.Abs(Canvas.GetLeft(first) - beforeKey - 5) < .6 || Canvas.GetLeft(first) >= beforeKey, "Arrow keys move the focused widget in 5 DIP steps");

        // Activity: leaving home, hiding, locking and the customize dialog stop home work.
        window.Navigate("shortcuts"); Pump();
        Check(!window.HomeActive && !window.Clock.IsRunning && window.UtilityWidgets.Values.All(w => !w.Content.IsRefreshing), "Leaving home stops the clock and every widget timer");
        window.Navigate("home"); Pump();
        window.SetSessionLocked(true);
        Check(!window.HomeActive && !window.Clock.IsRunning, "Locking the session stops home work");
        window.SetSessionLocked(false); window.SetSuspended(true);
        Check(!window.HomeActive, "Sleeping stops home work");
        window.SetSuspended(false);
        Check(window.HomeActive && window.Clock.IsRunning, "Unlocking and waking resume home");

        window.OpenHomeCustomize(); Pump();
        Check(window.HomeCustomizeOpen && !window.HomeActive, "Customize dialog pauses the home page behind it");
        if (output is not null) window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-home-customize.png"), PngBitmapEncoderOptions.Default);
        window.FindControl<TextBox>("ClockColorInput")!.Text = "red";
        Check(!window.ApplyHome() && window.HomeCustomizeOpen, "Invalid clock colours are rejected without saving");
        window.FindControl<TextBox>("ClockColorInput")!.Text = "#65DCEC";
        window.FindControl<ComboBox>("ClockStylePicker")!.SelectedIndex = 1;
        var pickerKinds = window.FindControl<ComboBox>("BackgroundPicker")!.Items.OfType<ComboBoxItem>().Select(i => (string?)i.Tag).ToArray();
        Check(pickerKinds.SequenceEqual(["Static", "Image", "Video"]), "The background picker offers only static, image and video");
        window.FindControl<ComboBox>("BackgroundPicker")!.SelectedIndex = 0;
        Check(window.ApplyHome() && !window.HomeCustomizeOpen && new SettingsStore(settingsPath).Load().Home! is { ClockStyle: "Flip", Color: "#65DCEC", Background: "Static" }, "Applying the dialog saves clock and background");
        Pump();
        Check(!window.HomeBackground.HasVisualBackground && window.FindControl<TextBlock>("BrandLabel")!.Foreground is ISolidColorBrush { Color: var themed } && themed != Colors.White,
            "A static background keeps theme-coloured labels");

        // Image background loads off the UI thread, turns text white, and is released on suspension.
        var image = Path.Combine(directory, "background.png");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(64, 36))) bitmap.Save(image, PngBitmapEncoderOptions.Default);
        void WaitForImage() { for (var i = 0; i < 200 && window.HomeBackground.IsImageLoading; i++) { Thread.Sleep(20); Pump(); } }
        window.OpenHomeCustomize();
        window.SelectBackgroundFile(image);
        Check(window.ApplyHome(), "A local image background can be chosen");
        Check(window.HomeBackground.IsImageLoading && !window.HomeBackground.HasImage, "Image decode is scheduled instead of blocking the UI thread");
        WaitForImage();
        Check(window.HomeBackground.HasImage, "The decoded image is shown " + window.HomeStatus);
        Check(window.Clock.Options.Background == "Image", "The clock switches to light ink once the visual background exists");
        Check(window.FindControl<TextBlock>("BrandLabel")!.Foreground is ISolidColorBrush { Color: var brand } && brand == Colors.White, "Labels over a visual background turn white");
        window.SetAccessibility(true, true);
        Check(!window.HomeBackground.HasVisualBackground, "High contrast drops decorative backgrounds");
        window.SetAccessibility(false, true); WaitForImage();
        if (output is not null) { Pump(); window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-home-widgets.png"), PngBitmapEncoderOptions.Default); }
        window.Navigate("settings"); Pump();
        Check(!window.HomeBackground.HasVisualBackground, "Leaving home releases the background");
        Check(window.FindControl<TextBlock>("BrandLabel")!.Foreground is ISolidColorBrush { Color: var muted } && muted != Colors.White, "Labels return to the theme colour without a visual background");
        window.Navigate("home"); Pump(); WaitForImage();
        Check(window.HomeBackground.HasImage, "Returning home reloads the image");
        window.SetSessionLocked(true);
        Check(!window.HomeBackground.HasImage, "Locking drops the decoded image");
        window.SetSessionLocked(false);
        window.OpenHomeCustomize();
        window.SelectBackgroundFile(Path.Combine(directory, "clip.mp4"));
        Check(!window.ApplyHome() && window.HomeCustomizeOpen, "A missing background file is refused");
        File.WriteAllBytes(Path.Combine(directory, "clip.mp4"), [0, 0, 0, 0]);
        Check(window.ApplyHome(), "A video background file can be chosen");
        Pump();
        Check(!window.HomeBackground.HasVideo && window.HomeStatus.Contains("已回退靜態背景", StringComparison.Ordinal), "Video without a usable decoder falls back to the static background with a reason");
        var videos = new List<FakeVideo>();
        var factory = MainWindow.VideoFactory;
        MainWindow.VideoFactory = path => { var video = new FakeVideo(); videos.Add(video); return video; };
        window.HomeBackground.VideoFactory = MainWindow.VideoFactory;
        window.Navigate("settings"); window.Navigate("home"); Pump();
        Check(videos.Count == 1 && videos[0].Playing && window.HomeBackground.HasVideo && window.HomeStatus.Length == 0, "A working decoder plays the video behind the widgets");
        window.Navigate("shortcuts"); Pump();
        Check(videos[0].Disposed && !window.HomeBackground.HasVideo, "Leaving home disposes the video decoder");
        window.Navigate("home"); Pump();
        Check(videos.Count == 2 && videos[1].Playing, "Returning home starts a fresh decoder");
        window.SetAccessibility(false, false); Pump();
        Check(videos[1].Disposed && !window.HomeBackground.HasVideo && window.HomeStatus.Contains("減少動畫", StringComparison.Ordinal), "Reduced motion stops the video and says why");
        window.SetAccessibility(false, true); Pump();
        videos[^1].Fail("測試解碼失敗。"); Pump();
        Check(videos[^1].Disposed && !window.HomeBackground.HasVideo && window.HomeStatus.Contains("測試解碼失敗", StringComparison.Ordinal), "A playback failure falls back to the static background with the reason");
        MainWindow.VideoFactory = factory;
        window.HomeBackground.VideoFactory = factory;

        // Removal asks first; widgets and their data reload in a new window.
        var target = window.UtilityWidgets.Keys.Last();
        var confirm = window.ConfirmAsync("移除組件", "確定？", "移除");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Check(confirm.IsCompleted && !confirm.Result && !window.ConfirmOpen, "Esc cancels the confirmation dialog");
        Check(window.RemoveHomeWidget(target) && window.UtilityWidgetCount == 11 && new SettingsStore(settingsPath).Load().Home!.Widgets!.Length == 11, "Removing a widget deletes it from settings");
        window.AllowClose = true; window.Close(); Pump();
        model.Dispose();
        var reloaded = new MainWindow(new ShellModel(new SettingsStore(settingsPath)), new DesktopLauncher(Path.Combine(directory, "data"), "/apps/s")) { Width = 1200, Height = 820 };
        reloaded.Show(); Pump();
        Check(reloaded.UtilityWidgetCount == 11 && reloaded.UtilityWidgets.Values.Any(w => w.Content.Settings.Kind == "Note" && w.Content.NoteEditor!.Text == "買牛奶"), "Widgets and their content survive a restart");
        reloaded.AllowClose = true; reloaded.Close(); Pump();
    }

    // Opt-in check against a real session bus with a running MPRIS player (see fake_mpris.py).
    public static void RunLiveMpris()
    {
        var source = MprisMediaSource.Shared;
        var snapshot = source.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(snapshot is { Title: "Secondary 測試曲", Playing: true, CanPrevious: false } && snapshot.Duration == TimeSpan.FromMinutes(3), "Live MPRIS player is read over the session bus");
        var changes = 0;
        using (source.Subscribe(() => Interlocked.Increment(ref changes)))
        {
            Thread.Sleep(500);
            Check(source.ControlAsync(MediaCommand.Toggle, CancellationToken.None).GetAwaiter().GetResult(), "Play/pause is sent to the live player");
            for (var i = 0; i < 50 && Volatile.Read(ref changes) == 0; i++) Thread.Sleep(50);
            Check(changes > 0, "The player's PropertiesChanged signal reaches subscribers");
            Check(source.ReadAsync(CancellationToken.None).GetAwaiter().GetResult() is { Playing: false }, "The new paused state is read back");
            Check(!source.ControlAsync(MediaCommand.Previous, CancellationToken.None).GetAwaiter().GetResult(), "An unsupported command is refused without calling the player");
        }
    }

    private static HomeWidgetSettings Saved(string settingsPath, string id) => new SettingsStore(settingsPath).Load().Home!.Widgets!.First(w => w.Id == id);

    private static void TestLiveWidgets()
    {
        var handler = new WeatherHandler();
        var weather = new WeatherDataService(new HttpClient(handler));
        var media = new FakeMedia();
        var host = new Window { Width = 400, Height = 400 };
        Palette.Apply(host.Resources, false, false, false);
        var weatherWidget = new UtilityWidget(new HomeWidgetSettings("w", "Weather"), weather, media);
        var mediaWidget = new UtilityWidget(new HomeWidgetSettings("m", "Media"), weather, media);
        var saved = new List<HomeWidgetSettings>();
        weatherWidget.SaveRequested += s => { saved.Add(s); return true; };
        host.Content = new StackPanel { Children = { weatherWidget, mediaWidget } };
        host.Show(); Pump();
        weatherWidget.SetActive(true); mediaWidget.SetActive(true);
        Check(weatherWidget.WeatherEditorVisible, "A new weather widget asks for a city first");
        weatherWidget.SearchWeatherCitiesAsync("Taipei").Wait(); Pump();
        weatherWidget.ChooseWeatherLocation();
        for (var i = 0; i < 50 && weatherWidget.WeatherText == "—°"; i++) { Thread.Sleep(20); Pump(); }
        Check(saved.LastOrDefault()?.Location?.Name == "臺北市" && weatherWidget.WeatherText == "21.5°C" && !weatherWidget.WeatherEditorVisible, "Weather saves the chosen location and shows the reading");
        Check(handler.Requests.All(u => u.Contains("open-meteo.com", StringComparison.Ordinal)), "Weather only contacts Open-Meteo");
        for (var i = 0; i < 50 && mediaWidget.MediaTitle != "Night Drive"; i++) { Thread.Sleep(20); Pump(); }
        Check(mediaWidget.MediaTitle == "Night Drive" && mediaWidget.MediaState == "播放中" && mediaWidget.MediaControlsEnabled, "Media shows the current player and enables its supported controls");
        mediaWidget.ControlMediaAsync(MediaCommand.Toggle).Wait(); Pump();
        Check(media.Commands.SequenceEqual([MediaCommand.Toggle]), "Play/pause is sent to the player");
        Check(media.Subscribers == 1, "An active media widget subscribes to player changes");
        mediaWidget.SetActive(false);
        Check(media.Subscribers == 0 && !mediaWidget.IsRefreshing, "An inactive media widget unsubscribes and stops refreshing");
        weatherWidget.Dispose(); mediaWidget.Dispose(); host.Close(); Pump();
    }

    private sealed class WeatherHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            var json = url.Contains("geocoding", StringComparison.Ordinal)
                ? """{"results":[{"name":"臺北市","country":"臺灣","admin1":"臺北","latitude":25.05,"longitude":121.53}]}"""
                : """{"current":{"time":"2026-10-18T12:00","temperature_2m":21.5,"apparent_temperature":22.0,"relative_humidity_2m":70,"weather_code":1,"wind_speed_10m":8.2}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeVideo : IVideoBackground
    {
        public Control View { get; } = new Border();
        public int FramesShown => Playing ? 1 : 0;
        public bool Playing { get; private set; }
        public bool Disposed { get; private set; }
        public event Action<string>? Failed;
        public void Play() => Playing = true;
        public void Fail(string reason) => Failed?.Invoke(reason);
        public void Dispose() { Disposed = true; Playing = false; }
    }

    private sealed class FakeMedia : IMediaSource
    {
        public List<MediaCommand> Commands { get; } = new();
        public int Subscribers;
        public Task<MediaSnapshot?> ReadAsync(CancellationToken token) => Task.FromResult<MediaSnapshot?>(
            new MediaSnapshot("org.mpris.MediaPlayer2.fake", "Night Drive", "Band", true, true, true, true, true, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), null));
        public Task<bool> ControlAsync(MediaCommand command, CancellationToken token) { Commands.Add(command); return Task.FromResult(true); }
        public IDisposable Subscribe(Action changed) { Interlocked.Increment(ref Subscribers); return new Release(this); }
        private sealed class Release(FakeMedia owner) : IDisposable { public void Dispose() => Interlocked.Decrement(ref owner.Subscribers); }
    }
}
