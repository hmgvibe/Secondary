using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;
using Secondary.App.Monitoring;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

internal static class Program
{
    private static int assertions;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--wait-target") { Thread.Sleep(2000); return 0; }
        var code = 0;
        try { Run(args); }
        catch (Exception e) { Console.Error.WriteLine(e); code = 1; }
        // Explicit WPF shutdown can leave native media broker threads alive.
        // Run's finally closes owned windows and stops monitoring first.
        Environment.Exit(code);
        return code;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        assertions++;
    }
    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static string? OutputDirectory(string[] args) => args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    private static void Run(string[] args)
    {
        if (args.Contains("--widget-edit-only"))
        {
            var widgetApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                WidgetGeometryChecks.Run(Check);
                WidgetInteractionChecks.Run(Check, OutputDirectory(args));
                Console.WriteLine($"PASS: {assertions} widget editing assertions");
            }
            finally { widgetApp.Shutdown(); }
            return;
        }
        if (args.Contains("--data-only"))
        {
            var dataApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { UiDataOptimizationChecks.Run(Check); Console.WriteLine($"PASS: {assertions} data integration assertions"); }
            finally { dataApp.Shutdown(); }
            return;
        }
        if (args.Contains("--game-telemetry"))
        {
            using var selected = Process.GetCurrentProcess();
            var live = new GameTelemetrySession(true, true, new GameTarget(selected.Id, selected.StartTime.ToUniversalTime().Ticks, selected.ProcessName));
            using var stop = new CancellationTokenSource();
            var worker = Task.Run(() => live.RunAsync(stop.Token));
            Thread.Sleep(8000);
            var snapshot = live.Read();
            stop.Cancel(); worker.GetAwaiter().GetResult();
            Check(snapshot.System is { TotalMemory: > 0, Cpu: not null }, "Live session reads actual CPU and physical memory");
            Check(snapshot.Sensors is not null && snapshot.Sensors.Readings.Length > 0, "Persistent helper produces real snapshots");
            Check(snapshot.Frames.Fps is null || snapshot.Frames.Fps > 0, "No frames remain unavailable, never fake zero FPS");
            Console.WriteLine("LIVE FRAME SOURCE: " + snapshot.FrameStatus);
            var remainingHelpers = Process.GetProcessesByName("Secondary.SensorHelper");
            Check(remainingHelpers.All(p => p.HasExited), "Streaming hardware helper stops with session");
            foreach (var helper in remainingHelpers) helper.Dispose();
            var changedIdentity = new GameTelemetrySession(true, false, new GameTarget(selected.Id, selected.StartTime.ToUniversalTime().Ticks + 1, selected.ProcessName));
            using var stopIdentity = new CancellationTokenSource();
            var identityWorker = Task.Run(() => changedIdentity.RunAsync(stopIdentity.Token));
            Thread.Sleep(500);
            Check(changedIdentity.Read().FrameStatus.Contains("PID"), "PID reuse or identity mismatch never captures another process");
            stopIdentity.Cancel(); identityWorker.GetAwaiter().GetResult();
            using var shortTarget = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-target" } })!;
            var exitSession = new GameTelemetrySession(true, false, new GameTarget(shortTarget.Id, shortTarget.StartTime.ToUniversalTime().Ticks, "test target"));
            using var exitCancel = new CancellationTokenSource();
            var exitWorker = Task.Run(() => exitSession.RunAsync(exitCancel.Token));
            Thread.Sleep(3500);
            Check(exitSession.Read().TargetExited && exitSession.Read().Frames.Fps is null, "Process exit is detected without retargeting or stale FPS");
            exitCancel.Cancel(); exitWorker.GetAwaiter().GetResult();
        }
        if (args.Contains("--sensors"))
        {
            var snapshot = Task.Run(() => SensorHelperClient.ReadAsync(CancellationToken.None)).GetAwaiter().GetResult();
            Check(snapshot.Schema == 1 && snapshot.Readings.Length > 0, "Live optional sensor helper returns a bounded snapshot");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "LibreHardwareMonitorLib"), "Hardware library remains outside UI process");
            using var stop = new CancellationTokenSource(100);
            var cancelled = false;
            try { Task.Run(() => SensorHelperClient.ReadAsync(stop.Token)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Sensor helper cancellation is observed");
            var helpers = Process.GetProcessesByName("Secondary.SensorHelper");
            Check(helpers.All(p => p.HasExited), "One-shot and cancelled sensor helpers exit");
            foreach (var helper in helpers) helper.Dispose();
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var displays = new DisplayService();
        var testDirectory = Path.Combine(Path.GetTempPath(), "Secondary.UiTests-" + Guid.NewGuid());
        using var model = new ShellViewModel(displays, new SettingsStore(Path.Combine(testDirectory, "settings.json")));
        var window = new MainWindow(model, registerHotkey: false);
        using var theme = new ThemeService(window);
        var root = (FrameworkElement)window.Content;
        try
        {
            foreach (var look in Enum.GetValues<ShellAppearance>())
            foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (var size in new[] { new Size(560,480), new Size(960,720), new Size(1200,820), new Size(1920,1080), new Size(2560,1440) })
            {
                window.SetAppearance(look);
                theme.Apply(look, dark ? ThemeChoice.Dark : ThemeChoice.Light, look == ShellAppearance.Game || dark, false);
                root.LayoutTransform = new ScaleTransform(scale, scale);
                foreach (var route in ModePolicy.Sections(model.Modes.Mode))
                {
                    window.Navigate(route);
                    Layout(window, root, size.Width, size.Height, scale);
                    var nav = (Border)window.FindName("NavigationBar");
                    Check(Math.Abs(nav.CornerRadius.TopLeft - nav.ActualHeight / 2) <= .5, "Navigation caps are semicircular");
                    Check(nav.ActualWidth <= size.Width - 30, "Navigation fits narrow view");
                    var items = ((StackPanel)window.FindName("NavigationItems")).Children.OfType<RadioButton>().ToArray();
                    Check(items.Select(b => (string)b.Tag).SequenceEqual(look == ShellAppearance.Game
                        ? new[] { "game", "exit-game", "settings" } : new[] { "home", "shortcuts", "enter-game", "settings" }), "Correct bottom navigation order");
                    foreach (var button in items)
                    {
                        Check(button.ActualHeight >= 44, "Accessible navigation target");
                        Check(button.ToolTip is ToolTip t && t.Style == window.FindResource("NavigationToolTip"), "Custom tooltip retained");
                    }
                    if (route != "home")
                    {
                        var area = (FrameworkElement)window.FindName("ContentArea");
                        Check(area.ActualWidth <= 1601 && area.ActualWidth > 200, "Bounded content width");
                        var left = area.TranslatePoint(new Point(), root).X;
                        Check(left >= 20 && left + area.ActualWidth <= size.Width - 15, "Centered content stays within gutters");
                    }
                    if (route == "game")
                    {
                        var hardware = (StackPanel)window.FindName("HardwarePreview");
                        Check(hardware.Children.Count == 3, "Hardware grouped vertically");
                        var gauges = Descendants(hardware).OfType<HudGauge>().ToArray();
                        Check(gauges.Length == 4 && gauges.All(g => double.IsNaN(g.Value)), "CPU, GPU, VRAM and memory start unavailable without fabricated fixtures");
                        var header = (WrapPanel)window.FindName("GameHeaderControls");
                        Check(header.Children[0] == window.FindName("GameTitle") && header.Children[1] == window.FindName("GameProcessPicker") && header.Children[2] == window.FindName("SamplingPicker"), "Game controls follow the title in the wrapping header");
                        Check(Descendants(hardware.Children[1]).Contains((DependencyObject)window.FindName("GpuMemoryPreview")), "VRAM is integrated into the GPU instrument group");
                        Check(!Descendants((DependencyObject)window.FindName("AnalysisPreview")).OfType<TextBlock>().Any(t => t.Text.Contains("VIDEO MEMORY") || t.Text.Contains("6.2 / 12")), "VRAM is not duplicated in the analysis area");
                        Check(!Descendants((DependencyObject)window.FindName("GamePage")).OfType<Border>().Any(b => b.Style == window.FindResource("Panel")), "Sci-fi game HUD has no card panels");
                        Check(Grid.GetRow((UIElement)window.FindName("AnalysisPreview")) == (((FrameworkElement)window.FindName("ContentArea")).ActualWidth < 820 ? 1 : 0), "Game columns adapt without compressing text");
                        Check(!((TextBlock)window.FindName("GameNotice")).Text.Contains("示意"), "Game dashboard has no synthetic telemetry");
                        var networkChart = (Grid)window.FindName("NetworkChart");
                        var networkTraces = Descendants(networkChart).OfType<TelemetryTrace>().ToArray();
                        Check(networkTraces.Length == 2 && !networkTraces[0].Dashed && networkTraces[1].Dashed, "Network chart distinguishes download and upload with solid and dashed traces");
                        if (!window.CompactGameLayout)
                            Check(networkChart.ActualHeight == 190 && networkChart.ActualWidth > 200, "Expanded network chart uses the full analysis width and frame-pacing chart height");
                        else
                        {
                            var summary = (FrameworkElement)window.FindName("CompactGameSummary");
                            var viewport = (ScrollViewer)window.FindName("ContentScroll");
                            Check(summary.ActualHeight > 100 && summary.ActualHeight <= viewport.ActualHeight, "Compact HUD keeps its six primary readings in the first viewport");
                        }
                    }
                    window.OpenSettings();
                    Layout(window, root, size.Width, size.Height, scale);
                    Check(window.SettingsOpen && window.CurrentPage == "settings" && model.Modes.Current == route, "Settings are independent page and preserve return destination");
                    Check(window.FindName("HomeSettingsBar") is null && !window.HomeCustomizeOpen, "Global settings have no home footer or open home dialog");
                    Check(((Button)window.FindName("SensorStart")).IsEnabled && !((Button)window.FindName("SensorStop")).IsEnabled, "Sensor helper starts disabled and is manually enabled only");
                    Check(!Descendants((DependencyObject)window.FindName("SettingsPanels")).Contains((DependencyObject)window.FindName("HomePreferences")), "Home preferences are not part of global settings");
                    Check(((FrameworkElement)window.FindName("SettingsPage")).IsVisible == root.IsVisible, "Settings page is current content");
                    foreach (var otherPage in new[] { "HomePage", "ShortcutsPage", "GamePage" })
                        Check(((FrameworkElement)window.FindName(otherPage)).Visibility == Visibility.Collapsed, "Previous page is removed from view");
                    Check(window.FindName("SettingsOverlay") is null && window.FindName("SettingsSheet") is null, "No modal overlay remains");
                    var settingsPanels = (StackPanel)window.FindName("SettingsPanels");
                    double lastBottom = -16;
                    foreach (var panel in settingsPanels.Children.OfType<Border>())
                    {
                        var position = panel.TranslatePoint(new Point(), settingsPanels);
                        Check(panel.ActualWidth <= 801 && panel.TranslatePoint(new Point(panel.ActualWidth,0),root).X <= size.Width - 15, "Wide settings cards fit viewport");
                        Check(Math.Abs(position.Y - lastBottom - 16) <= 1, "Settings always stack vertically without rows");
                        Check(Math.Abs(position.X * 2 + panel.ActualWidth - settingsPanels.ActualWidth) <= 1, "Settings column is centered");
                        if (size.Width >= 1200) Check(Math.Abs(panel.ActualWidth - 800) <= 1, "Wide settings use 800 DIP cards");
                        lastBottom = position.Y + panel.ActualHeight;
                    }
                    window.CloseSettings();
                    Check(window.CurrentPage == route, "Settings back restores previous page");
                }
                Check(Contrast(window, "TextBrush", "CanvasBrush") >= 4.5, "Text contrast");
            }
            root.LayoutTransform = Transform.Identity;
            TestNavigation(window, model, root, args);
            window.SetAppearance(ShellAppearance.Normal); window.Navigate("shortcuts");
            window.Navigate("enter-game");
            Check(window.IsGame, "Dock enters game");
            window.Navigate("exit-game");
            Check(model.Modes.Current == "shortcuts", "Exit returns to previous page");
            window.OpenSettings();
            Check(!window.TransitionRunning, "Settings cancel transition");
            window.CloseSettings();
            var toggle = (CheckBox)window.FindName("GameAnimationToggle");
            toggle.IsChecked = false;
            Check(!model.Settings.GameAnimation, "Animation preference saved");
            window.Navigate("enter-game");
            Check(!window.TransitionRunning, "Disabled animation does not start");
            window.CancelTransition();
            Check(((FrameworkElement)window.FindName("GameTransition")).Visibility == Visibility.Collapsed, "Cancelled overlay removed");
            ((ComboBox)window.FindName("SamplingPicker")).SelectedIndex = 1;
            Check(model.Settings.GameHighSampling, "Sampling preference retained for future monitoring");
            Check(model.SystemDashboard.UpdateCount == 0, "Preview never starts real telemetry");
            using (var restarted = new ShellViewModel(displays, new SettingsStore(Path.Combine(testDirectory,"settings.json"))))
                Check(restarted.Modes.Mode == AppMode.Game && !restarted.Settings.GameAnimation && restarted.Settings.GameHighSampling, "V3 settings survive app restart");
            theme.Apply(ShellAppearance.Game, ThemeChoice.Dark, true, true);
            Check(!theme.AnimationsEnabled && (Brush)window.Resources["CanvasBrush"] == SystemColors.WindowBrush, "High contrast overrides visuals");
            TestMonitoring(); // Keep data-layer regression coverage with injected fakes only.
            TestHome(window, model, testDirectory, root, args);
            TestWidget(window, model, root, testDirectory);
            TestCustomEditors(window,args);
            TestLaunchpad(window,root,testDirectory,args);
            TestLaunchpadWrapping(window,root,testDirectory);
            TestUtilityWidgets(window, model, root, testDirectory, args);
            ClockOptimizationChecks.Run(window, model, Check, OutputDirectory(args));
            WidgetOptimizationChecks.Run(window, model, Check, OutputDirectory(args));
            WidgetGeometryChecks.Run(Check);
            WidgetInteractionChecks.Run(Check, OutputDirectory(args));
            BackgroundOptimizationChecks.Run(window, model, Check, OutputDirectory(args));
            UiDataOptimizationChecks.Run(Check);
            RootOptimizationChecks.Run(window, model, Check);
            LaunchpadOptimizationChecks.Run(window, model, Check, OutputDirectory(args));
            ShortcutPathOptimizationChecks.Run(Check, OutputDirectory(args));
            if (args.Contains("--live"))
            {
                LiveOptimizationChecks.Run(window, model, Check, OutputDirectory(args));
                // Opt-in integration check: real WPF animation clocks, isolated settings,
                // no global hotkeys and no display changes.
                window.SetAppearance(ShellAppearance.Normal);
                model.SaveRuntime(gameAnimation: true);
                window.Show(); Flush();
                LaunchpadOptimizationChecks.RunLive(window, Check);
                ShortcutEntry? edited = null;
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var editor = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "編輯快捷方式");
                    var fields = Descendants(editor).OfType<TextBox>().ToArray();
                    Check(fields.All(f => EditorMenu.GetEnabled(f)), "Shortcut dialog editors inherit custom input and context menus");
                    fields[0].Text = "Edited fixture";
                    var sampleDirectory = args.FirstOrDefault(a => !a.StartsWith("--"));
                    if (sampleDirectory is not null)
                    {
                        editor.UpdateLayout(); var surface = (FrameworkElement)editor.Content;
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth + surface.Margin.Left + surface.Margin.Right),(int)Math.Ceiling(surface.ActualHeight + surface.Margin.Top + surface.Margin.Bottom),96,96,PixelFormats.Pbgra32);
                        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)editor.FindResource("SheetBrush"),null,new Rect(0,0,bitmap.PixelWidth,bitmap.PixelHeight));
                        bitmap.Render(background); bitmap.Render(surface);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(sampleDirectory);
                        using var file = File.Create(Path.Combine(sampleDirectory,"shortcut-editor.png")); png.Save(file);
                    }
                    Descendants(editor).OfType<Button>().Single(b => b.Content as string == "儲存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                },DispatcherPriority.ApplicationIdle);
                edited = ShortcutDialogs.Edit(window,new ShortcutEntry("fixture","Fixture",Steps:[new("Open","https://example.com")]));
                Check(edited?.Name == "Edited fixture", "Native shortcut editor saves draft without launching target");
                window.Navigate("home"); Flush();
                var clockWidget = window.ClockWidget;
                var clockMover = clockWidget.Children.OfType<Thumb>().Single(t => t.Tag is ResizeEdges.None);
                Check(clockMover.Focus(), "Clock can retain keyboard focus for arrow-key editing");
                Flush();
                var expectedChrome = 1d;
                Check(clockWidget.Children.OfType<Border>().First().Opacity == expectedChrome,
                    "Keyboard selection keeps the clock outline visible for editing");
                Check(clockWidget.Children.OfType<Thumb>().Where(t => t.Tag is ResizeEdges e && e != ResizeEdges.None).All(t => t.Opacity == expectedChrome),
                    "Keyboard selection keeps all eight resize handles visible for editing");
                window.Navigate("enter-game");
                if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                {
                    Check(window.TransitionRunning, "Real window starts transition");
                    PumpUntil(() => !window.TransitionRunning);
                    Check(((FrameworkElement)window.FindName("GameTransition")).Visibility == Visibility.Collapsed, "Completion removes transition layer");
                    for (var i = 0; i < 8; i++)
                    {
                        window.ExitGame(); window.Navigate("enter-game");
                        Check(window.TransitionRunning, "Repeated entry owns one transition");
                        window.ExitGame();
                        Check(!window.TransitionRunning && !window.IsGame, "Mid-transition exit cancels immediately");
                    }
                    window.Navigate("enter-game"); window.OpenSettings();
                    Check(!window.TransitionRunning && window.SettingsOpen, "Settings interrupt running animation");
                    window.CloseSettings(); window.StartGameTransition(); window.Hide();
                    Check(!window.TransitionRunning, "Hiding removes running clocks");
                }
                else Check(!window.TransitionRunning, "OS reduced-motion skips transition");
                window.Hide();
            }
            // Native window layout constrains its content to the real client size.
            if (args.Contains("--game-telemetry"))
            {
                window.Show(); window.Navigate("enter-game"); Flush();
                ((CheckBox)window.FindName("GameSensorsToggle")).IsChecked = true;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 6500) { Flush(); Thread.Sleep(30); }
                Check(window.GameMonitoringActive, "Visible game page owns one monitoring session");
                Check(((TextBlock)window.FindName("CpuValue")).Text != "—" && ((TextBlock)window.FindName("MemoryValue")).Text != "—", "Live HUD renders actual system readings");
                var outputDirectory = args.FirstOrDefault(a => !a.StartsWith("--"));
                if (outputDirectory is not null)
                {
                    Directory.CreateDirectory(outputDirectory);
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outputDirectory, "game-LIVE.png")); png.Save(file);
                }
                window.OpenSettings(); Flush();
                Check(!window.GameMonitoringActive, "Settings suspends game telemetry");
                window.GameMonitoringCompletion.GetAwaiter().GetResult();
                window.CloseSettings(); Flush();
                Check(window.GameMonitoringActive, "Returning to game creates fresh baselines");
                window.WindowState = WindowState.Minimized; Flush();
                Check(!window.GameMonitoringActive, "Minimize cancels telemetry");
                window.GameMonitoringCompletion.GetAwaiter().GetResult();
                window.WindowState = WindowState.Normal; Flush();
                window.ExitGame(); Flush();
                Check(!window.GameMonitoringActive, "Leaving game releases monitoring");
                window.GameMonitoringCompletion.GetAwaiter().GetResult();
                window.Hide();
            }
            // Render fixtures only in the independent off-screen run.
            if (OutputDirectory(args) is not null && !args.Contains("--live") && !args.Contains("--game-telemetry"))
            {
                Directory.CreateDirectory(OutputDirectory(args)!);
                foreach (var route in new[] { "home", "shortcuts", "game", "settings", "settings-sensors", "home-dialog" })
                foreach (var width in new[] { 560, 1200, 1920 })
                {
                    window.SetAppearance(route == "game" ? ShellAppearance.Game : ShellAppearance.Normal);
                    theme.Apply(route == "game" ? ShellAppearance.Game : ShellAppearance.Normal, ThemeChoice.Light, route == "game", false);
                    if (route is "settings" or "settings-sensors") window.OpenSettings();
                    else if (route == "home-dialog") { window.Navigate("home"); window.OpenHomeCustomize(); }
                    else window.Navigate(route);
                    var height = width == 1920 ? 1080 : 820;
                    Layout(window, root, width, height);
                    if (route == "settings-sensors") { ((ScrollViewer)window.FindName("ContentScroll")).ScrollToBottom(); Flush(); }
                    var bitmap = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(OutputDirectory(args)!, route + "-" + width + "-DESIGN-PREVIEW.png"));
                    encoder.Save(file);
                    window.CloseSettings(); window.CloseHomeCustomize();
                }
            }
            Console.WriteLine($"PASS: {assertions} assertions; 80 viewport/scale/theme combinations; home clocks/backgrounds, HUD preview and legacy sampler regression.");
        }
        finally
        {
            window.AllowClose = true; window.Close();
            try { window.GameMonitoringCompletion.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            app.Shutdown();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory,true);
        }
    }
    private static void Layout(MainWindow window, FrameworkElement root, double width, double height, double scale = 1)
    {
        if (window.IsVisible)
        {
            window.Width = Math.Max(window.MinWidth, width); window.Height = Math.Max(window.MinHeight, height);
            Flush(); window.ApplyResponsiveLayout(root.ActualWidth, root.ActualHeight); root.UpdateLayout(); Flush(); return;
        }
        window.ApplyResponsiveLayout(width,height); Flush();
        root.Measure(new Size(width*scale,height*scale)); root.Arrange(new Rect(0,0,width*scale,height*scale)); root.UpdateLayout(); Flush();
    }
    private static void TestNavigation(MainWindow window, ShellViewModel model, FrameworkElement root, string[] args)
    {
        foreach (var mode in new[] { ShellAppearance.Normal, ShellAppearance.Game })
        foreach (var viewport in new[] { new Size(560,480), new Size(1200,820) })
        foreach (var size in new[] { "Standard", "Small" })
        foreach (var position in new[] { "Top", "Bottom", "Left", "Right" })
        {
            model.SaveRuntime(navigationSize: size, navigationPosition: position);
            window.SetAppearance(mode); if (mode == ShellAppearance.Normal) window.Navigate("home");
            Layout(window, root, viewport.Width, viewport.Height);
            var bar = (Border)window.FindName("NavigationBar");
            var items = (StackPanel)window.FindName("NavigationItems");
            var scale = size == "Small" ? .8 : 1;
            var bounds = bar.TransformToAncestor(root).TransformBounds(new Rect(0,0,bar.ActualWidth,bar.ActualHeight));
            Check(Math.Abs(bounds.Width - bar.ActualWidth * scale) < 1 && Math.Abs(bounds.Height - bar.ActualHeight * scale) < 1, "Small navigation scales the entire capsule to 80 percent");
            Check(bounds.Left >= 15 && bounds.Top >= 15 && bounds.Right <= viewport.Width - 15 && bounds.Bottom <= viewport.Height - 15, "All navigation placements fit viewport");
            Check(items.Orientation == (position is "Left" or "Right" ? Orientation.Vertical : Orientation.Horizontal), "Navigation orientation follows chosen edge");
            Check(Math.Abs(bar.CornerRadius.TopLeft - Math.Min(bar.ActualWidth,bar.ActualHeight)/2) < 1, "Horizontal and vertical capsule ends are semicircular");
            var frame = (FrameworkElement)window.FindName("Frame");
            var content = frame.TransformToAncestor(root).TransformBounds(new Rect(0,0,frame.ActualWidth,frame.ActualHeight));
            Check(position switch { "Left" => bounds.Right < content.Left, "Right" => bounds.Left > content.Right, "Top" => bounds.Bottom < content.Top, _ => bounds.Top > content.Bottom }, "Content reserves space on the chosen navigation edge");
            var tooltip = (ToolTip)((RadioButton)items.Children[0]).ToolTip;
            var tip = tooltip.CustomPopupPlacementCallback!(new Size(100,40),new Size(64,64),new Point())[0].Point;
            Check(position switch { "Left" => tip.X >=64, "Right" => tip.X < -99, "Top" => tip.Y >=64, _=>tip.Y < -39 }, "Custom tooltip opens inward from selected edge");
            if (OutputDirectory(args) is not null && !args.Contains("--live") && mode == ShellAppearance.Normal && viewport.Width == 1200)
            {
                Directory.CreateDirectory(OutputDirectory(args)!);
                var bitmap = new RenderTargetBitmap(1200,820,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(OutputDirectory(args)!, "nav-"+size+"-"+position+".png")); png.Save(file);
            }
        }
        model.SaveRuntime(navigationSize: "Standard", navigationPosition: "Bottom");
        window.SetAppearance(ShellAppearance.Normal);
    }
    private static void TestWidget(MainWindow window, ShellViewModel model, FrameworkElement root, string directory)
    {
        window.ExitGame(); window.Navigate("home");
        var canvas = (Canvas)window.FindName("WidgetCanvas");
        var widget = window.ClockWidget;
        foreach (var size in new[] { new Size(560,480),new Size(1200,820),new Size(1920,1080) })
        {
            Layout(window,root,size.Width,size.Height);
            widget.Configure(new WidgetPlacement(.5,.5,1),new Size(462,182));
            widget.MoveTo(-100,-100);
            Check(Canvas.GetLeft(widget) == 0 && Canvas.GetTop(widget) == 0, "Widget movement clamps upper and left edges");
            widget.MoveTo(10000,10000);
            Check(Canvas.GetLeft(widget)+widget.Width <= canvas.ActualWidth+.1 && Canvas.GetTop(widget)+widget.Height <= canvas.ActualHeight+.1, "Widget cannot leave canvas");
            widget.MoveTo(0,0); widget.ResizeTo(10000,new Point());
            Check(widget.Width <= canvas.ActualWidth+.1 && widget.Height <= canvas.ActualHeight+.1, "Oversized widget remains inside canvas");
            Check(Math.Abs((widget.Width-24)/(widget.Height-24)-462d/182) < .01, "Resize preserves clock aspect ratio");
            widget.ResizeTo(200,new Point()); widget.MoveTo(30,20); widget.CommitPlacement();
            var saved = new SettingsStore(Path.Combine(directory,"settings.json")).Load().Home!;
            Check(saved.X == widget.Placement.X && saved.Y == widget.Placement.Y && saved.Size == widget.Placement.Scale, "Widget placement persists after completed interaction");
            widget.MoveTo(0,0); widget.ResizeTo(210,110,new Point());
            var initialHeight = widget.Height;
            widget.ResizeTo(250,initialHeight,new Point());
            Check(Math.Abs(widget.Width-250)<.1 && Math.Abs(widget.Height-initialHeight)<.1, "Horizontal resizing does not alter height");
            widget.ResizeTo(widget.Width,150,new Point());
            Check(Math.Abs(widget.Width-250)<.1 && Math.Abs(widget.Height-150)<.1, "Vertical resizing does not alter width");
            widget.CommitPlacement();
            var separate = new SettingsStore(Path.Combine(directory,"settings.json")).Load().Home!;
            Check(separate.ClockWidthScale == widget.Placement.WidthScale && separate.ClockHeightScale == widget.Placement.HeightScale, "Independent clock dimensions survive settings round trip");
            widget.ResizeTo(10000,10000,new Point());
            Check(widget.Width <= canvas.ActualWidth+.1 && widget.Height <= canvas.ActualHeight+.1, "Independent dimensions remain inside canvas");
        }
        model.SaveHome(new()); ((HomeClock)window.FindName("ClockView")).Configure(new());

    }
    private static void TestLaunchpad(MainWindow window, FrameworkElement root, string directory, string[] args)
    {
        var path = Path.Combine(directory,"ui-shortcuts.json");
        var library = new ShortcutLibrary(path);
        library.Save([new("one","文件入口","工作","↗",[new("Open","https://example.com/docs")]),new("two","遊戲資料夾","遊戲","▣",[new("Open",@"C:\Games")])]);
        using var view = new LaunchpadView(path);
        var page = (StackPanel)window.FindName("ShortcutsPage"); page.Children.Remove(window.Launchpad); page.Children.Add(view);
        try
        {
            window.Navigate("shortcuts"); Layout(window,root,1200,820);
            Check(view.EntryCount == 2, "Launchpad restores saved entries without executing them");
            var layoutButton = Descendants(view).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "切換分組排列方向");
            var groupScroll = Descendants(view).OfType<ScrollViewer>().Single(s => s.Content is StackPanel);
            var sections = (StackPanel)groupScroll.Content;
            Check(view.GroupLayout == "Vertical" && sections.Children.Count == 2,
                "Launchpad separates shortcuts into two vertical group sections");
            var verticalFirst = ((UIElement)sections.Children[0]).TranslatePoint(new Point(),sections);
            var verticalSecond = ((UIElement)sections.Children[1]).TranslatePoint(new Point(),sections);
            Check(verticalSecond.Y > verticalFirst.Y && Math.Abs(verticalSecond.X-verticalFirst.X) < 1,
                "Vertical group sections retain their original stacked arrangement");
            layoutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window,root,1200,820);
            var reloadedLayout = new ShortcutLibrary(path); reloadedLayout.Load();
            Check(view.GroupLayout == "Horizontal" && reloadedLayout.GroupLayout == "Horizontal" &&
                groupScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto,
                "Horizontal group arrangement persists across reload");
            Layout(window,root,560,480);
            var horizontalFirst = ((UIElement)sections.Children[0]).TranslatePoint(new Point(),sections);
            var horizontalSecond = ((UIElement)sections.Children[1]).TranslatePoint(new Point(),sections);
            Check(horizontalSecond.X > horizontalFirst.X && Math.Abs(horizontalSecond.Y-horizontalFirst.Y) < 1,
                "Horizontal group sections retain their original side-by-side arrangement");
            Layout(window,root,1200,820);
            if (OutputDirectory(args) is not null && !args.Contains("--live"))
            {
                Directory.CreateDirectory(OutputDirectory(args)!);
                var horizontalBitmap = new RenderTargetBitmap(1200,820,96,96,PixelFormats.Pbgra32);
                horizontalBitmap.Render(root);
                var horizontalPng = new PngBitmapEncoder(); horizontalPng.Frames.Add(BitmapFrame.Create(horizontalBitmap));
                using var horizontalFile = File.Create(Path.Combine(OutputDirectory(args)!,"launchpad-horizontal.png")); horizontalPng.Save(horizontalFile);
            }
            layoutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window,root,1200,820);
            Check(view.GroupLayout == "Vertical", "Group arrangement switches back to vertical");
            var search = Descendants(view).OfType<TextBox>().Single(); search.Text = "文件"; view.RenderPendingTilesForMeasurement(); Flush();
            Check(Descendants(view).OfType<Button>().Count(b => b.Content is StackPanel) == 1, "Launchpad search filters visible tiles");
            search.Text = ""; var groups = Descendants(view).OfType<ComboBox>().Single(); groups.SelectedItem = "遊戲"; Flush();
            Check(Descendants(view).OfType<Button>().Count(b => b.Content is StackPanel) == 1, "Launchpad group filters visible tiles");
            groups.SelectedIndex = 0; Layout(window,root,560,820);
            Check(Descendants(view).OfType<Button>().Where(b => b.Content is StackPanel).All(b => b.ActualWidth <= view.ActualWidth), "Shortcut tiles fit narrow content");
            var scriptPath = Path.Combine(directory,"hash-check.ps1"); File.WriteAllText(scriptPath,"# inert test fixture");
            var executor = new ShortcutExecutor(); var entry = new ShortcutEntry("script","Fixture",Steps:[new("Script",scriptPath)]);
            Check(executor.Review(entry).Contains("SHA256"), "Script confirmation identifies exact file content hash");
            File.WriteAllText(scriptPath,"# changed inert fixture");
            var blocked = false;
            try { executor.Execute(entry.Steps![0],CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "Changed script is rejected before any process launch");
            var linkPath = Path.Combine(directory,"拖入測試.lnk"); File.WriteAllText(linkPath,"inert shell-link fixture, never executed");
            var urlPath = Path.Combine(directory,"網站.url"); File.WriteAllText(urlPath,"[InternetShortcut]\nURL=https://example.com/imported\n");
            var badUrl = Path.Combine(directory,"blocked.url"); File.WriteAllText(badUrl,"[InternetShortcut]\nURL=javascript:alert(1)\n");
            search.Text = "no match";
            Check(view.ImportFiles([linkPath,urlPath,linkPath,badUrl,Path.Combine(directory,"missing.lnk")]) == 2, "Drop imports valid shortcuts directly and skips duplicate missing or unsafe entries");
            var imported = new ShortcutLibrary(path).Load();
            Check(imported.Length == 4 && imported.Single(e => e.Name == "拖入測試").Steps![0].Target == linkPath, "Lnk import preserves original link path and name");
            Check(imported.Where(e => e.Name is "拖入測試" or "網站").All(e => e.ApprovedFingerprint is null), "Dropped shortcuts still require first-run confirmation");
            Check(search.Text == "" && view.EntryCount == 4, "Successful drop reveals added entries despite previous search");
            Check(view.ImportFiles([linkPath]) == 0 && view.EntryCount == 4, "Repeated drop does not duplicate an existing shortcut");
            Check(view.ImportFiles(Enumerable.Repeat(linkPath,21).ToArray()) == 0 && view.EntryCount == 4, "Oversized drop does not partially import");
            if (OutputDirectory(args) is not null && !args.Contains("--live"))
            {
                Layout(window,root,1200,820); Directory.CreateDirectory(OutputDirectory(args)!);
                var bitmap = new RenderTargetBitmap(1200,820,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(OutputDirectory(args)!,"launchpad-fixture.png")); png.Save(file);
            }
        }
        finally { page.Children.Remove(view); page.Children.Add(window.Launchpad); window.Navigate("home"); }
    }
    private static void TestLaunchpadWrapping(MainWindow window, FrameworkElement root, string directory)
    {
        var path = Path.Combine(directory,"wrapping-shortcuts.json");
        var library = new ShortcutLibrary(path);
        library.Save(Enumerable.Range(1,6).Select(i => new ShortcutEntry($"wrap-{i}",$"換行{i:00}","測試分組","↗",[new("Open","https://example.com")])).ToArray());
        using var view = new LaunchpadView(path);
        var page = (StackPanel)window.FindName("ShortcutsPage"); page.Children.Remove(window.Launchpad); page.Children.Add(view);
        try
        {
            window.Navigate("shortcuts"); Layout(window,root,560,480);
            var tiles = Enumerable.Range(1,6).Select(i => Descendants(view).OfType<Button>().Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == $"換行{i:00}")).ToArray();
            var verticalPanel = (WrapPanel)VisualTreeHelper.GetParent(tiles[0]);
            var first = tiles[0].TranslatePoint(new Point(),verticalPanel);
            var second = tiles[1].TranslatePoint(new Point(),verticalPanel);
            var columns = Math.Max(1, (int)Math.Floor(verticalPanel.ActualWidth / (tiles[0].ActualWidth + tiles[0].Margin.Left + tiles[0].Margin.Right)));
            Check(columns < tiles.Length, "Legal minimum-width viewport requires another shortcut row");
            var wrapped = tiles[columns].TranslatePoint(new Point(), verticalPanel);
            Check(wrapped.Y > first.Y && Math.Abs(wrapped.X-first.X) < 1,
                "Vertical group wraps according to actual available width in a legal window");
            Layout(window,root,1200,820);
            first = tiles[0].TranslatePoint(new Point(),verticalPanel);
            second = tiles[1].TranslatePoint(new Point(),verticalPanel);
            Check(second.X > first.X && Math.Abs(second.Y-first.Y) < 1,
                "Vertical group returns shortcut cards to the same row when widened");
            var layoutButton = Descendants(view).OfType<Button>().Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == "切換分組排列方向");
            layoutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(window,root,560,480);
            tiles = Enumerable.Range(1,6).Select(i => Descendants(view).OfType<Button>().Single(b =>
                System.Windows.Automation.AutomationProperties.GetName(b) == $"換行{i:00}")).ToArray();
            var horizontalPanel = (WrapPanel)VisualTreeHelper.GetParent(tiles[0]);
            first = tiles[0].TranslatePoint(new Point(),horizontalPanel);
            second = tiles[1].TranslatePoint(new Point(),horizontalPanel);
            Check(second.X > first.X && Math.Abs(second.Y-first.Y) < 1,
                "Horizontal group wraps shortcut cards to another column when height is short");
            Check(second.X-first.X >= tiles[0].ActualWidth+10,
                "Horizontal group keeps visible space between adjacent shortcut columns");
            Check(horizontalPanel.ActualWidth > 200 && horizontalPanel.ActualHeight <= 154.5,
                $"Horizontal group grows by columns without extending cards below its height limit ({horizontalPanel.ActualWidth:F0}x{horizontalPanel.ActualHeight:F0}, limit={horizontalPanel.Height:F0})");
            Layout(window,root,1200,1200);
            first = tiles[0].TranslatePoint(new Point(),horizontalPanel);
            second = tiles[1].TranslatePoint(new Point(),horizontalPanel);
            Check(horizontalPanel.ActualHeight >= tiles[0].ActualHeight * 2
                ? second.Y > first.Y && Math.Abs(second.X-first.X) < 1
                : second.X > first.X && Math.Abs(second.Y-first.Y) < 1,
                "Horizontal groups respect actual row capacity after resizing, including desktop-constrained native windows");
        }
        finally { page.Children.Remove(view); page.Children.Add(window.Launchpad); window.Navigate("home"); }
    }
    private static void TestCustomEditors(MainWindow window, string[] args)
    {
        foreach (var name in new[] { "HotkeyInput", "ClockColorInput", "BackgroundPathInput" })
        {
            var input = (TextBox)window.FindName(name);
            input.ApplyTemplate();
            Check(EditorMenu.GetEnabled(input) && input.ContextMenu is not null, "All application inputs use custom edit menus");
            Check(input.Template.FindName("InputBorder",input) is Border, "All application inputs use custom rounded template");
            EditorMenu.Populate(input,input.ContextMenu!);
            Check(input.ContextMenu!.Style == window.FindResource("WidgetMenu"), "Editor menu uses shared custom menu surface");
            var items = input.ContextMenu.Items.OfType<MenuItem>().ToArray();
            Check(items.Length == 5 && items.All(item => item.CommandTarget == input && item.Style == window.FindResource("WidgetMenuItem")), "Editing commands explicitly target the originating editor");
        }
        var path = (TextBox)window.FindName("BackgroundPathInput");
        var original = path.Text; path.Text = "C:\\example.png"; path.SelectAll();
        Check(!System.Windows.Input.ApplicationCommands.Cut.CanExecute(null,path), "Read-only custom editor forbids cutting");
        Check(System.Windows.Input.ApplicationCommands.Copy.CanExecute(null,path), "Read-only custom editor still allows copying");
        path.Text = original;
        if (OutputDirectory(args) is not null && !args.Contains("--live"))
        {
            var input = (TextBox)window.FindName("HotkeyInput");
            var menu = input.ContextMenu!;
            menu.Width = 280; menu.Measure(new Size(280,400)); menu.Arrange(new Rect(0,0,280,menu.DesiredSize.Height)); menu.UpdateLayout();
            var image = new RenderTargetBitmap(280,(int)Math.Ceiling(menu.ActualHeight),96,96,PixelFormats.Pbgra32); image.Render(menu);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(OutputDirectory(args)!); using var file = File.Create(Path.Combine(OutputDirectory(args)!,"custom-editor-menu.png")); png.Save(file);
        }
    }
    private static void TestUtilityWidgets(MainWindow window, ShellViewModel model, FrameworkElement root, string directory, string[] args)
    {
        window.Navigate("home");
        foreach (var kind in new[] { "Calendar", "Note", "Countdown", "Stopwatch" }) Check(window.AddHomeWidget(kind), "Add utility " + kind);
        Check(window.UtilityWidgetCount == 4 && !window.AddHomeWidget("Unknown"), "Four widget kinds available and unknown kind rejected");
        var canvas = (Canvas)window.FindName("WidgetCanvas");
        Layout(window,root,1200,820);
        var contents = canvas.Children.OfType<HomeWidget>().SelectMany(w => w.Children.OfType<HomeUtilityWidget>()).ToArray();
        var note = contents.Single(w => w.Settings.Kind == "Note");
        Check(Descendants(note).OfType<TextBox>().Single().Style == window.FindResource("WidgetInput"), "Note uses theme-aware rounded editor");
        foreach (var content in contents)
        {
            var buttons = Descendants(content).OfType<Button>().ToArray();
            Check(buttons.All(b => b.Height == 36), "Widget controls share consistent button height");
        }
        Descendants(note).OfType<TextBox>().Single().Text = "測試便箋\n第二行";
        Check(note.SaveNote(), "Note saves editable text");
        Check(new SettingsStore(Path.Combine(directory,"settings.json")).Load().Home!.Widgets!.Single(w => w.Kind == "Note").Text == "測試便箋\n第二行", "Note text persists");
        var timer = contents.Single(w => w.Settings.Kind == "Countdown");
        var duration = Descendants(timer).OfType<TextBox>().Single();
        var start = Descendants(timer).OfType<Button>().First(b => (string)b.Content == "開始");
        duration.Text = "0"; start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(duration.IsEnabled && Descendants(timer).OfType<TextBlock>().Any(t => t.Text.Contains("1–1440")), "Invalid countdown duration rejected");
        duration.Text = "2"; start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!duration.IsEnabled && (string)start.Content == "暫停" && model.Settings.Home!.Widgets!.Single(w => w.Kind == "Countdown").Minutes == 2, "Countdown starts and persists valid duration");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((string)start.Content == "繼續", "Countdown can pause");
        var stopwatch = contents.Single(w => w.Settings.Kind == "Stopwatch");
        var stopwatchStart = Descendants(stopwatch).OfType<Button>().First(b => (string)b.Content == "開始");
        stopwatchStart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); stopwatch.SetActive(true);
        PumpUntil(() => Descendants(stopwatch).OfType<TextBlock>().Any(t => t.Text.StartsWith("00:00:") && t.Text != "00:00:00"));
        Check(stopwatch.IsRefreshing, "Stopwatch advances using elapsed time");
        stopwatch.SetActive(false); Check(!stopwatch.IsRefreshing, "Hidden widget stops its UI refresh timer");
        window.OpenHomeCustomize();
        ((Button)window.FindName("HomeApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.Settings.Home!.Widgets!.Length == 4, "Home customization preserves utility widgets");
        foreach (var width in new[] { 560,1200 })
        {
            Layout(window,root,width,820);
            foreach (var frame in canvas.Children.OfType<HomeWidget>())
                Check(Canvas.GetLeft(frame)+frame.Width <= canvas.ActualWidth+.1 && Canvas.GetTop(frame)+frame.Height <= canvas.ActualHeight+.1, "Utility widgets remain in canvas after resize");
        }
        if (OutputDirectory(args) is not null && !args.Contains("--live"))
        {
            Layout(window,root,1920,1080);
            var i = 0;
            foreach (var frame in canvas.Children.OfType<HomeWidget>().Where(w => w != window.ClockWidget)) frame.Configure(new WidgetPlacement(i++ / 3d,.9,1),new Size(320,300));
            window.ClockWidget.Configure(new WidgetPlacement(.5,0,1),new Size(462,182));
            Layout(window,root,1920,1080);
            Directory.CreateDirectory(OutputDirectory(args)!);
            var bitmap = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(OutputDirectory(args)!,"utility-widgets.png")); png.Save(file);
            using var widgetTheme = new ThemeService(window);
            widgetTheme.Apply(ShellAppearance.Normal,ThemeChoice.Dark,true,false);
            Layout(window,root,1920,1080);
            var darkBitmap = new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); darkBitmap.Render(root);
            var darkPng = new PngBitmapEncoder(); darkPng.Frames.Add(BitmapFrame.Create(darkBitmap));
            using var darkFile = File.Create(Path.Combine(OutputDirectory(args)!,"utility-widgets-dark.png")); darkPng.Save(darkFile);
        }
        foreach (var id in model.Settings.Home!.Widgets!.Select(w => w.Id).ToArray()) Check(window.RemoveHomeWidget(id), "Remove widget persists and detaches");
        Check(window.UtilityWidgetCount == 0 && canvas.Children.Count == 1 && contents.All(w => !w.IsRefreshing), "Removed widgets release their resources");
        model.SaveHome(new()); ((HomeClock)window.FindName("ClockView")).Configure(new());
        foreach (var kind in new[] { "Todo", "Counter", "TimeProgress" }) Check(window.AddHomeWidget(kind), "Add new widget " + kind);
        Layout(window, root, 1200, 820);
        var extras = canvas.Children.OfType<HomeWidget>().SelectMany(w => w.Children.OfType<HomeUtilityWidget>()).ToArray();
        var todo = extras.Single(w => w.Settings.Kind == "Todo");
        Descendants(todo).OfType<TextBox>().Single().Text = "完成首頁";
        Descendants(todo).OfType<Button>().Single(b => b.Content as string == "新增").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(todo.Settings.Tasks is [{ Text: "完成首頁", Done: false }], "Todo add persists");
        Layout(window, root, 1200, 820);
        Descendants(todo).OfType<Button>().Single(b => b.Content is TextBlock).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(todo.Settings.Tasks![0].Done, "Todo complete persists");
        var counter = extras.Single(w => w.Settings.Kind == "Counter");
        Descendants(counter).OfType<Button>().Single(b => b.Content as string == "＋ 1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(counter.Settings.Count == 1 && model.Settings.Home!.Widgets!.Single(w => w.Kind == "Counter").Count == 1, "Counter increment saves immediately");
        Layout(window, root, 1200, 820);
        var resetCounter = Descendants(counter).OfType<Button>().Single(b => b.Content as string == "歸零");
        Check(resetCounter.HorizontalAlignment == HorizontalAlignment.Center && resetCounter.ActualWidth < counter.ActualWidth * .8,
            "Counter reset button keeps a compact centered width");
        foreach (var extra in extras) { extra.SetActive(true); Check(extra.IsRefreshing == (extra.Settings.Kind == "TimeProgress"), "Only time progress needs refresh"); extra.SetActive(false); Check(!extra.IsRefreshing, "New widget stops background refresh"); }
        Layout(window, root, 1200, 820);
        if (OutputDirectory(args) is not null && !args.Contains("--live"))
        {
            var surface = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)window.FindResource("CanvasBrush"), Resources = window.Resources };
            foreach (var extra in extras)
            {
                var preview = new HomeUtilityWidget(extra.Settings) { Width = extra.Settings.Kind == "Todo" ? 440 : extra.Settings.Kind == "Counter" ? 220 : 300,
                    Height = extra.Settings.Kind == "Counter" ? 230 : 300, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Top };
                surface.Children.Add(preview);
            }
            surface.Measure(new Size(1032,324)); surface.Arrange(new Rect(0,0,1032,324)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1032,324,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(OutputDirectory(args)!);
            using var file = File.Create(Path.Combine(OutputDirectory(args)!, "new-home-widgets.png")); png.Save(file);
            foreach (var preview in surface.Children.OfType<HomeUtilityWidget>()) preview.Dispose();
        }
        foreach (var id in model.Settings.Home!.Widgets!.Select(w => w.Id).ToArray()) window.RemoveHomeWidget(id);
        foreach (var kind in new[] { "Media", "Weather", "EventCountdown" }) Check(window.AddHomeWidget(kind), "Add live widget " + kind);
        Layout(window, root, 1200, 820);
        var liveWidgets = canvas.Children.OfType<HomeWidget>().SelectMany(w => w.Children.OfType<HomeUtilityWidget>()).ToArray();
        var eventWidget = liveWidgets.Single(w => w.Settings.Kind == "EventCountdown");
        var eventFrame = (HomeWidget)eventWidget.Parent;
        var eventSettings = Descendants(eventFrame).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "設定倒數日");
        var settingsPosition = eventSettings.TranslatePoint(new Point(0, 0), eventWidget);
        Check(settingsPosition.Y >= 0 && settingsPosition.Y < 48 &&
            settingsPosition.X >= eventWidget.ActualWidth - 52 &&
            settingsPosition.X + eventSettings.ActualWidth <= eventWidget.ActualWidth,
            "Event settings icon sits in the content upper right, below the title bar");
        var eventInputs = Descendants(eventWidget).OfType<TextBox>().ToArray();
        eventInputs[0].Text = "旅行"; eventInputs[1].Text = "2026-12-31";
        Descendants(eventWidget).OfType<Button>().Single(b => b.Content as string == "儲存日期").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(model.Settings.Home!.Widgets!.Single(w => w.Kind == "EventCountdown") is { EventTitle: "旅行", EventDate: "2026-12-31" }, "Event countdown saves title and date");
        Layout(window, root, 1200, 820);
        Check(((StackPanel)eventInputs[0].Parent).Visibility == Visibility.Collapsed && Descendants(eventWidget).OfType<TextBlock>().Any(t => t.Text.Contains("天") && t.FontSize >= 24 && t.FontSize <= 110), "Saved event countdown shows day view and hides editor");
        Check(Descendants(eventWidget).OfType<TextBlock>().Any(t => t.Text == "2026 年 12 月 31 日"), "Saved event view shows only the target date below the day count");
        eventSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window, root, 1200, 820);
        Check(((StackPanel)eventInputs[0].Parent).Visibility == Visibility.Visible, "Event settings button reopens editor");
        Check(liveWidgets.Single(w => w.Settings.Kind == "Weather").Settings.City == "", "Weather waits for a city before requesting network data");
        var mediaWidget = liveWidgets.Single(w => w.Settings.Kind == "Media");
        Check(new[] { "上一首", "播放或暫停", "下一首" }.All(name => Descendants(mediaWidget).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == name)), "Media has graphical playback controls");
        if (OutputDirectory(args) is not null && !args.Contains("--live"))
        {
            var surface = new StackPanel { Orientation = Orientation.Horizontal, Background = (Brush)window.FindResource("CanvasBrush"), Resources = window.Resources };
            foreach (var live in liveWidgets)
            {
                var previewSettings = live.Settings.Kind == "Weather" ? live.Settings with { City = "台北" } : live.Settings;
                surface.Children.Add(new HomeUtilityWidget(previewSettings) { Width = 320, Height = 300, Margin = new Thickness(12) });
            }
            surface.Measure(new Size(1032,324)); surface.Arrange(new Rect(0,0,1032,324)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1032,324,96,96,PixelFormats.Pbgra32); bitmap.Render(surface);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory(OutputDirectory(args)!);
            using var file = File.Create(Path.Combine(OutputDirectory(args)!, "media-weather-event-widgets.png")); png.Save(file);
            foreach (var preview in surface.Children.OfType<HomeUtilityWidget>()) preview.Dispose();
            var eventSurface = new Canvas { Width = 360, Height = 340, Background = (Brush)window.FindResource("CanvasBrush"), Resources = window.Resources };
            var eventPreview = new HomeUtilityWidget(eventWidget.Settings);
            var previewFrame = new HomeWidget(); previewFrame.Attach(eventSurface, eventPreview, "倒數日");
            eventSurface.Measure(new Size(360, 340)); eventSurface.Arrange(new Rect(0, 0, 360, 340)); eventSurface.UpdateLayout();
            previewFrame.Configure(new WidgetPlacement(0, 0, 1), new Size(320, 300)); eventSurface.UpdateLayout();
            var framed = new RenderTargetBitmap(360, 340, 96, 96, PixelFormats.Pbgra32); framed.Render(eventSurface);
            var framedPng = new PngBitmapEncoder(); framedPng.Frames.Add(BitmapFrame.Create(framed));
            using var framedFile = File.Create(Path.Combine(OutputDirectory(args)!, "event-widget-header.png")); framedPng.Save(framedFile);
            eventPreview.Dispose(); previewFrame.Detach();
        }
        foreach (var id in model.Settings.Home!.Widgets!.Select(w => w.Id).ToArray()) window.RemoveHomeWidget(id);
        model.SaveHome(new());
    }
    private static void TestHome(MainWindow window, ShellViewModel model, string directory, FrameworkElement root, string[] args)
    {
        using (var stableClock = new HomeClock { FitWidget = true, AutomaticBrush = Brushes.White })
        {
            stableClock.Configure(new HomeSettings(ShowSeconds:true,ShowDate:false));
            byte[] ClockPixels(int second)
            {
                stableClock.UpdateTime(new DateTime(2026,9,19,12,34,second));
                stableClock.Measure(new Size(940,260)); stableClock.Arrange(new Rect(0,0,940,260)); stableClock.UpdateLayout();
                var bitmap = new RenderTargetBitmap(940,260,96,96,PixelFormats.Pbgra32); bitmap.Render(stableClock);
                var pixels = new byte[940*260*4]; bitmap.CopyPixels(pixels,940*4,0); return pixels;
            }
            var narrow = ClockPixels(11); var wide = ClockPixels(58);
            Check(!narrow.SequenceEqual(wide), "Clock pixel regression actually renders different seconds");
            Check(Enumerable.Range(0,260).All(row => narrow.AsSpan(row*940*4,470*4).SequenceEqual(wide.AsSpan(row*940*4,470*4))),
                "Hours and minutes remain pixel-stable when seconds change width");
        }
        window.ExitGame(); window.Navigate("home");
        var clock = (HomeClock)window.FindName("ClockView");
        var backdrop = (HomeBackdrop)window.FindName("HomeBackground");
        Check(!clock.IsRunning && !backdrop.HasMedia && !backdrop.HasMotion, "Unshown home has no live resources");
        foreach (var style in new[] { "Digital", "Flip", "Analog" })
        foreach (var seconds in new[] { false, true })
        foreach (var width in new[] { 560, 1200, 1920 })
        {
            clock.Configure(new HomeSettings(ClockStyle: style, ShowSeconds: seconds, Size: 1.6, X: 1, Y: 1));
            clock.UpdateTime(new DateTime(2026, 9, 15, 23, 59, 59));
            Layout(window, root, width, 820);
            Check(clock.DisplayedTime == (seconds ? "23:59:59" : "23:59"), "Every clock style receives real formatted time");
            Check(clock.ActualWidth > 0 && clock.ActualHeight > 0, "Clock bounds fit the responsive home page");
            clock.UpdateTime(new DateTime(2026, 9, 16, 0, 0, 0));
            Check(clock.DisplayedTime == (seconds ? "00:00:00" : "00:00"), "Clock rolls over midnight");
        }
        clock.Configure(new()); clock.SetActive(true);
        Check(clock.IsRunning, "Clock starts one timer");
        clock.SetActive(false);
        Check(!clock.IsRunning && !clock.HasAnimatedProperties, "Clock suspension removes timer and flip animation");
        window.OpenHomeCustomize();
        Check(window.HomeCustomizeOpen && !window.SettingsOpen && window.CurrentPage == "home", "Home customization is a modal, not global settings");
        foreach (var viewport in new[] { new Size(560,480), new Size(1200,820), new Size(1920,1080) })
        {
            Layout(window, root, viewport.Width, viewport.Height);
            var dialog = (Border)window.FindName("HomeCustomizeDialog");
            var point = dialog.TranslatePoint(new Point(), root);
            Check(point.X >= 15 && point.Y >= 15 && point.X + dialog.ActualWidth <= viewport.Width - 15 && point.Y + dialog.ActualHeight <= viewport.Height - 15, "Home modal fits viewport");
            var scroll = (ScrollViewer)window.FindName("HomeEditorScroll");
            var footer = (Grid)window.FindName("HomeDialogActions");
            Check(scroll.TranslatePoint(new Point(0,scroll.ActualHeight),root).Y <= footer.TranslatePoint(new Point(),root).Y, "Dialog footer does not overlay controls");
        }
        ((TextBox)window.FindName("ClockColorInput")).Text = "#FFFFFF";
        window.CloseHomeCustomize(); window.OpenHomeCustomize();
        Check(((TextBox)window.FindName("ClockColorInput")).Text == (model.Settings.Home?.Color ?? "Auto"), "Closing discards unsaved changes");
        var backgroundPicker = (ComboBox)window.FindName("BackgroundPicker");
        foreach (var index in new[] { 0, 1, 2, 3, 4 })
        {
            backgroundPicker.SelectedIndex = index; Flush();
            Check(((FrameworkElement)window.FindName("BackgroundFileFields")).Visibility == (index is 1 or 2 ? Visibility.Visible : Visibility.Collapsed), "Only image and video options show file controls");
        }
        backgroundPicker.SelectedIndex = 0;
        ((Slider)window.FindName("ClockSizeSlider")).Value = 1.3; Flush();
        Check(((TextBlock)window.FindName("ClockSizeReadout")).Text.Contains("130"), "Size slider reports actual percentage");
        ((Slider)window.FindName("ClockXSlider")).Value = .9;
        ((Slider)window.FindName("ClockYSlider")).Value = .1;
        ((Button)window.FindName("CenterClockButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((Slider)window.FindName("ClockXSlider")).Value == .5 && ((Slider)window.FindName("ClockYSlider")).Value == .5, "Center action resets both position axes");
        ((ComboBox)window.FindName("ClockStylePicker")).SelectedIndex = 1;
        ((CheckBox)window.FindName("ClockSecondsToggle")).IsChecked = true;
        ((TextBox)window.FindName("ClockColorInput")).Text = "bad";
        var apply = (Button)window.FindName("HomeApplyButton");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.HomeCustomizeOpen && ((TextBlock)window.FindName("HomeSettingsStatus")).Text.Contains("顏色"), "Invalid editor color does not save or leave settings");
        ((TextBox)window.FindName("ClockColorInput")).Text = "#65DCEC";
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.HomeCustomizeOpen && window.CurrentPage == "home" && model.Settings.Home is { ClockStyle: "Flip", ShowSeconds: true, Color: "#65DCEC" }, "Home editor saves and returns home");
        Check(new SettingsStore(Path.Combine(directory, "settings.json")).Load().Home == model.Settings.Home, "Home editor persists to disk");
        var imagePath = Path.Combine(directory, "test-background.png");
        var pixels = new byte[] { 0x60, 0x80, 0xA0, 0xFF };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(imagePath)) encoder.Save(output);
        backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: imagePath));
        backdrop.SetActive(true, false);
        PumpUntil(() => backdrop.ImageLoadCompletion.IsCompleted);
        if (!SystemParameters.HighContrast)
        {
            Check(backdrop.HasImage && !backdrop.HasMedia, "Image background loads in reduced motion");
            using (File.Open(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check(true, "Decoded image does not lock the source file");
        }
        backdrop.SetActive(false, false);
        Check(!backdrop.HasImage && backdrop.Children.Count == 0, "Leaving home releases decoded bitmap references");
        backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: imagePath + ".missing"));
        backdrop.SetActive(true, true);
        PumpUntil(() => backdrop.ImageLoadCompletion.IsCompleted);
        if (!SystemParameters.HighContrast) Check(backdrop.Status.Contains("回退") && !backdrop.HasImage, "Missing media falls back explicitly");
        foreach (var kind in new[] { "Gradient", "Aurora" })
        {
            backdrop.Configure(new HomeSettings(Background: kind)); backdrop.SetActive(true, true);
            if (!SystemParameters.HighContrast) Check(backdrop.HasMotion, "Built-in background owns a running animation");
            backdrop.SetActive(true, false);
            Check(!backdrop.HasMotion, "Reduced motion removes background clocks");
            backdrop.SetActive(false, false);
            Check(backdrop.Children.Count == 0, "Suspension clears built-in visuals");
        }
        var brokenVideo = Path.Combine(directory, "broken.mp4"); File.WriteAllText(brokenVideo, "invalid media fixture");
        backdrop.Configure(new HomeSettings(Background: "Video", MediaPath: brokenVideo)); backdrop.SetActive(true, false);
        Check(!backdrop.HasMedia, "Reduced motion never constructs video playback");
        backdrop.SetActive(false, false);
        if (OutputDirectory(args) is not null && !args.Contains("--live"))
        {
            Directory.CreateDirectory(OutputDirectory(args)!);
            foreach (var style in new[] { "Digital", "Flip", "Analog" })
            {
                clock.Configure(new HomeSettings(ClockStyle: style, ShowSeconds: true));
                clock.UpdateTime(new DateTime(2026, 9, 15, 10, 8, 32));
                Layout(window, root, 1200, 820);
                var render = new RenderTargetBitmap(1200, 820, 96, 96, PixelFormats.Pbgra32); render.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
                using var output = File.Create(Path.Combine(OutputDirectory(args)!, "clock-" + style + "-FIXTURE.png")); png.Save(output);
            }
        }
        // Live lifecycle assertions are opt-in, in an isolated test window, never global input.
        if (args.Contains("--live"))
        {
            window.Show(); Flush();
            Check(clock.IsRunning, "Visible home runs clock");
            window.OpenHomeCustomize();
            Check(!clock.IsRunning && !((FrameworkElement)window.FindName("NavigationBar")).IsEnabled, "Home modal suspends clock and prevents background navigation");
            var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
            window.RaiseEvent(escape);
            Check(!window.HomeCustomizeOpen && clock.IsRunning && ((FrameworkElement)window.FindName("NavigationBar")).IsEnabled, "Escape closes only home modal and restores activity");
            var beforeTick = clock.DisplayedTime;
            PumpUntil(() => clock.DisplayedTime != beforeTick);
            Check(clock.IsRunning, "Visible clock advances on the wall-clock timer");
            var sessionHandler = typeof(MainWindow).GetMethod("OnSessionSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            sessionHandler.Invoke(window, new object[] { window, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionLock) }); Flush();
            Check(!clock.IsRunning && !backdrop.HasMedia, "Simulated session-lock notification suspends home without locking Windows");
            sessionHandler.Invoke(window, new object[] { window, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionUnlock) }); Flush();
            Check(clock.IsRunning, "Simulated unlock notification resumes home");
            var powerHandler = typeof(MainWindow).GetMethod("OnPowerModeChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            powerHandler.Invoke(window, new object[] { window, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Suspend) }); Flush();
            Check(!clock.IsRunning, "Simulated suspend notification stops clock");
            powerHandler.Invoke(window, new object[] { window, new Microsoft.Win32.PowerModeChangedEventArgs(Microsoft.Win32.PowerModes.Resume) }); Flush();
            Check(clock.IsRunning, "Simulated resume notification restarts clock");
            window.OpenSettings(); Check(!clock.IsRunning && !backdrop.HasMedia, "Settings suspends home");
            window.CloseSettings(); Check(clock.IsRunning, "Returning from settings resumes clock");
            window.Navigate("shortcuts"); Check(!clock.IsRunning, "Shortcuts suspends home");
            window.Navigate("home");
            window.WindowState = WindowState.Minimized; Flush(); Check(!clock.IsRunning, "Minimizing suspends home");
            window.WindowState = WindowState.Normal; Flush(); Check(clock.IsRunning, "Restoring refreshes clock");
            window.Navigate("enter-game"); Check(!clock.IsRunning && !backdrop.HasMotion && !backdrop.HasMedia, "Game mode releases all home activity");
            window.ExitGame(); window.Navigate("home");
            var videoArgument = args.FirstOrDefault(a => a.StartsWith("--video="));
            if (videoArgument is not null && SystemParameters.HighContrast) throw new InvalidOperationException("影片驗證尚未完成：高對比模式會停用影片背景。");
            if (videoArgument is not null && !SystemParameters.HighContrast)
            {
                var videoPath = Path.GetFullPath(videoArgument[8..]);
                backdrop.Configure(new HomeSettings(Background: "Video", MediaPath: videoPath)); backdrop.SetActive(true, true);
                PumpUntil(() => backdrop.MediaReady);
                PumpUntil(() => backdrop.PlaybackPosition > TimeSpan.FromMilliseconds(150));
                PumpUntil(() => backdrop.CompletedLoops > 0, 20000);
                Check(backdrop.HasMedia && backdrop.MediaReady, "Local MP4 opens, advances and loops");
                window.OpenSettings();
                Check(!backdrop.HasMedia && !backdrop.MediaReady, "Leaving home closes successfully playing video");
                using (File.Open(videoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Check(true, "Released video file is no longer locked");
                window.CloseSettings();
            }
            backdrop.Configure(new HomeSettings(Background: "Video", MediaPath: brokenVideo)); backdrop.SetActive(true, true);
            if (!SystemParameters.HighContrast)
            {
                PumpUntil(() => !backdrop.HasMedia);
                Check(backdrop.Status.Contains("回退"), "Invalid video failure releases media and reports fallback");
            }
            window.Hide(); Check(!clock.IsRunning && !backdrop.HasMedia, "Hiding releases home resources");
        }
        model.SaveHome(new()); clock.Configure(new()); backdrop.Configure(new());
    }
    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromMilliseconds(timeoutMilliseconds)) { Flush(); Thread.Sleep(10); }
        Check(condition(), "Monitoring operation completes within timeout");
    }
    private static void TestMonitoring()
    {
        var dashboard = new SystemDashboardModel();
        var created = 0;
        FakeSampler? sampler = null;
        using var module = new SystemModule(dashboard, Dispatcher.CurrentDispatcher, () => { Interlocked.Increment(ref created); return sampler = new FakeSampler(); });
        module.Activate(); Flush();
        Check(created == 0, "Hidden dashboard never constructs sampler");
        module.Configure(true, true);
        PumpUntil(() => dashboard.Cards[0].Value == "25%");
        Check(sampler!.LastGame, "Game policy reaches native sampler");
        var previousSampler = sampler;
        module.Configure(true, true, true);
        PumpUntil(() => sampler != previousSampler && sampler!.LastHigh && dashboard.Status.Contains("高採樣率"));
        Check(previousSampler.Disposed, "Changing rate stops and disposes previous sampler");
        module.Configure(false, true);
        PumpUntil(() => module.Completion.IsCompleted);
        Check(sampler.Disposed, "Hiding view disposes native resources");
        var count = created;
        module.Activate(); Flush();
        Check(created == count, "Hidden reactivation cannot poll");
        module.Configure(true, false);
        PumpUntil(() => created > count && dashboard.Status.Contains("採樣 1 秒"));
        Check(!sampler!.LastGame, "Full mode restores regular sampling");
        module.Suspend(); PumpUntil(() => module.Completion.IsCompleted);
        Check(sampler.Disposed, "Leaving section releases sampler");
        var changes = 0;
        dashboard.Cards[0].PropertyChanged += (_, _) => changes++;
        dashboard.Update(FakeSampler.Sample, false); changes = 0;
        dashboard.Update(FakeSampler.Sample, false);
        Check(changes == 0, "Unchanged metric does not invalidate UI");
        dashboard.Update(FakeSampler.Sample with { Cpu = null, TotalMemory = 0, AvailableMemory = 0, Gpu = null, Download = null, Upload = null }, false);
        Check(dashboard.Cards.All(c => c.Value == "—"), "Unavailable metrics never masquerade as zero");
        // Hold a synchronous read across suspension: the old generation must not publish.
        var slow = new FakeSampler { Block = true };
        var lateDashboard = new SystemDashboardModel();
        using var lateModule = new SystemModule(lateDashboard, Dispatcher.CurrentDispatcher, () => slow);
        lateModule.Configure(true, true); lateModule.Activate();
        PumpUntil(() => slow.Entered.IsSet);
        lateModule.Suspend(); slow.Release.Set();
        PumpUntil(() => lateModule.Completion.IsCompleted);
        Check(slow.Disposed, "Cancelled in-flight read disposes its sampler");
        Check(lateDashboard.Cards[0].Value == "—", "Cancelled generation cannot publish stale values");
        var failureDashboard = new SystemDashboardModel();
        var failureSampler = new FakeSampler { Throw = true };
        using var failedModule = new SystemModule(failureDashboard, Dispatcher.CurrentDispatcher, () => failureSampler);
        failedModule.Configure(true, false); failedModule.Activate();
        PumpUntil(() => failedModule.Completion.IsCompleted);
        Check(failureDashboard.Status.Contains("採樣失敗") && failureSampler.Disposed, "Native read failure is visible and releases resources");
    }

    private sealed class FakeSampler : ISystemSampler
    {
        public static SystemSample Sample => new(25, 16UL * 1073741824, 8UL * 1073741824, 1024, 2048, 40, "TEST FIXTURE", "TEST FIXTURE", "TEST FIXTURE", DateTimeOffset.UnixEpoch);
        public bool LastGame, Disposed, Block, Throw;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public SystemSample Read(bool game, bool high = false) { LastGame = game; LastHigh = high; Entered.Set(); if (Throw) throw new InvalidOperationException("Test failure"); if (Block) Release.Wait(TimeSpan.FromSeconds(5)); return Sample; }
        public bool LastHigh;
        public void Dispose() { Disposed = true; Entered.Dispose(); Release.Dispose(); }
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static double Contrast(Window window, string fg, string bg)
    {
        var backdrop = ((SolidColorBrush)window.Resources["CanvasBrush"]).Color;
        var background = ((SolidColorBrush)window.Resources[bg]).Color;
        var foreground = ((SolidColorBrush)window.Resources[fg]).Color;
        double Lum(Color c, Color under)
        {
            double Channel(byte v, byte u)
            {
                var s = (v * (c.A / 255.0) + u * (1 - c.A / 255.0)) / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(c.R,under.R) + 0.7152 * Channel(c.G,under.G) + 0.0722 * Channel(c.B,under.B);
        }
        var a = Lum(foreground, background); var b = Lum(background, backdrop);
        return (Math.Max(a,b) + 0.05) / (Math.Min(a,b) + 0.05);
    }
}
