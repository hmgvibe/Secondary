using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;

internal static class LiveOptimizationChecks
{
    public static void Run(MainWindow window, ShellViewModel model, Action<bool, string> check, string? outputDirectory)
    {
        var root = (FrameworkElement)window.Content;
        root.LayoutTransform = Transform.Identity;
        window.SetAppearance(ShellAppearance.Game);
        window.Width = 560; window.Height = 480; window.Show(); Pump();
        window.ApplyResponsiveLayout(root.ActualWidth, root.ActualHeight); Pump();
        var summary = (FrameworkElement)window.FindName("CompactGameSummary");
        var nav = (FrameworkElement)window.FindName("NavigationBar");
        var bounds = summary.TransformToAncestor(root).TransformBounds(new Rect(summary.RenderSize));
        check(window.CompactGameLayout && bounds.Bottom < nav.TranslatePoint(new Point(), root).Y,
            "Actual minimum window shows all six primary HUD readings above navigation");
        if (outputDirectory is not null) Save(Render(root), Path.Combine(outputDirectory, "game-560x480-NATIVE.png"));
        window.ExitGame(); window.Navigate("home"); window.Width = 1200; window.Height = 820; Pump();

        using var clock = new HomeClock { AutomaticBrush = Brushes.White, FitWidget = true };
        clock.Configure(new HomeSettings(ClockStyle: "Flip", ShowSeconds: true, ShowDate: false));
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(12, 17, 24)) };
        stage.Children.Add(clock);
        var host = new Window { Owner = window, Title = "Secondary 翻頁動畫驗證", Content = stage,
            Width = 860, Height = 340, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        try
        {
            host.Show(); Pump();
            var start = new DateTime(2026, 10, 14, 10, 8, 59);
            clock.UpdateTime(start); Pump();
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            {
                var frames = new List<BitmapSource>(); var progresses = new List<double>();
                clock.UpdateTime(start.AddSeconds(1), true);
                var timeline = Stopwatch.StartNew();
                for (var i = 0; i <= 14; i++)
                {
                    while (timeline.ElapsedMilliseconds < i * 40) { Pump(); Thread.Sleep(3); }
                    Pump(); progresses.Add(clock.FlipProgress); frames.Add(Render(stage));
                }
                check(progresses.Any(p => p is > .05 and < .45) && progresses.Any(p => p is > .55 and < .95),
                    "Visible flip clock renders both hinge phases on a real animation timeline");
                check(!clock.HasAnimatedProperties && clock.FlipProgress == 1,
                    "Real flip animation completes and releases its animation clock");
                var complete = Render(stage); clock.UpdateTime(start.AddSeconds(1)); Pump();
                check(SamePixels(complete, Render(stage)), "Completed live flip agrees with the static time at seconds rollover");
                if (outputDirectory is not null)
                {
                    Directory.CreateDirectory(outputDirectory);
                    var gif = new GifBitmapEncoder();
                    foreach (var bitmapFrame in frames)
                    {
                        var metadata = new BitmapMetadata("gif");
                        metadata.SetQuery("/grctlext/Delay", (ushort)4);
                        metadata.SetQuery("/grctlext/Disposal", (byte)2);
                        gif.Frames.Add(BitmapFrame.Create(bitmapFrame, null, metadata, null));
                    }
                    using var file = File.Create(Path.Combine(outputDirectory, "clock-Flip-LIVE-TIMELINE.gif")); gif.Save(file);
                    foreach (var index in new[] { 0, 3, 6, 9, 14 }) Save(frames[index], Path.Combine(outputDirectory, $"clock-Flip-live-{index * 40:000}ms.png"));
                }
            }
            else check(!clock.HasAnimatedProperties, "Live clock respects reduced motion and high contrast");
        }
        finally { host.Close(); }
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(12, 17, 24)) };
        using var note = new HomeUtilityWidget(new HomeWidgetSettings("live-note", "Note", Text: "已儲存內容"));
        var frame = new HomeWidget(); frame.Attach(canvas, note, "便箋");
        var noteHost = new Window { Owner = window, Title = "Secondary 縮放驗證", Resources = window.Resources,
            Content = canvas, Width = 860, Height = 620, ShowInTaskbar = false };
        try
        {
            noteHost.Show(); Pump(); frame.Configure(new WidgetPlacement(X: 0, Y: 0), new Size(320, 300)); Pump();
            var editor = Descendants(note).OfType<TextBox>().Single();
            editor.Text = "未保存的草稿\n縮放時保留游標"; editor.Select(2, 3);
            check(editor.Focus(), "Native note editor accepts keyboard focus before resize"); Pump();
            var originalHeight = editor.ActualHeight;
            if (outputDirectory is not null) Save(Render(canvas), Path.Combine(outputDirectory, "note-Normal-NATIVE.png"));
            for (var i = 0; i < 20; i++) { frame.ResizeTo(344 + i * 16, 324 + i * 10, new Point()); Pump(); }
            check(ReferenceEquals(Keyboard.FocusedElement, editor) && editor.SelectionStart == 2 && editor.SelectionLength == 3 && editor.Text.StartsWith("未保存"),
                "Repeated native resize preserves editor focus, selection and unsaved text");
            check(editor.ActualHeight > originalHeight + 140, "Native note editor fills added height after repeated resizing");
            if (outputDirectory is not null) Save(Render(canvas), Path.Combine(outputDirectory, "note-Large-NATIVE.png"));
        }
        finally { frame.Detach(); noteHost.Close(); window.Hide(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static BitmapSource Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze(); return bitmap;
    }
    private static bool SamePixels(BitmapSource left, BitmapSource right)
    {
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight) return false;
        var stride = left.PixelWidth * 4; var a = new byte[stride * left.PixelHeight]; var b = new byte[a.Length];
        left.CopyPixels(a, stride, 0); right.CopyPixels(b, stride, 0); return a.SequenceEqual(b);
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
