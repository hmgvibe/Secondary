using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;

internal static class ClockOptimizationChecks
{
    public static void Run(MainWindow window, ShellViewModel model, Action<bool, string> check, string? outputDirectory)
    {
        using var clock = new HomeClock { FitWidget = true, AutomaticBrush = Brushes.White };
        foreach (var style in new[] { "Digital", "Flip", "Analog" })
        foreach (var seconds in new[] { false, true })
        foreach (var date in new[] { false, true })
        foreach (var use24 in new[] { false, true })
        {
            clock.Configure(new HomeSettings(ClockStyle: style, ShowSeconds: seconds, ShowDate: date, Use24Hours: use24));
            var natural = clock.NaturalSize;
            foreach (var time in new[]
            {
                new DateTime(2026, 1, 9, 1, 11, 11),
                new DateTime(2026, 9, 30, 10, 8, 8),
                new DateTime(2026, 12, 31, 23, 59, 59),
                new DateTime(2027, 1, 1, 0, 0, 0)
            })
            {
                clock.UpdateTime(time);
                check(clock.NaturalSize == natural, "Clock frame remains stable through digit/date changes and midnight");
            }
            var image = Render(clock, natural);
            var bounds = InkBounds(image);
            check(!bounds.IsEmpty && bounds.Left > 0 && bounds.Top > 0 && bounds.Right < image.Width && bounds.Bottom < image.Height,
                "Clock ink fits measured natural bounds for every style and display format");
        }
        clock.Configure(new HomeSettings());
        clock.UpdateTime(new DateTime(2026, 10, 13, 10, 8, 0));
        var digital = Render(clock, clock.NaturalSize);
        check(clock.NaturalSize.Width < 380, "Default digital frame removes the previous 462 DIP content width");
        check(InkBounds(digital).Width / digital.Width > .83, "Digital ink occupies most of its natural content frame");
        Save(digital.Bitmap, outputDirectory, "clock-Digital-compact-FIXTURE.png");

        clock.Configure(new HomeSettings(ShowSeconds: true, ShowDate: false));
        clock.UpdateTime(new DateTime(2026, 10, 13, 12, 34, 11));
        var narrow = Render(clock, clock.NaturalSize);
        clock.UpdateTime(new DateTime(2026, 10, 13, 12, 34, 58));
        var wide = Render(clock, clock.NaturalSize);
        check(!narrow.Data.SequenceEqual(wide.Data), "Digital stability check compares different seconds");
        check(RegionEqual(narrow, wide, 0, 0, narrow.Width / 2, narrow.Height),
            "Measured digital slots keep the hours and minutes fixed while seconds change");

        clock.Configure(new HomeSettings(ClockStyle: "Flip", ShowDate: false));
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            foreach (var (oldTime, newTime) in new[]
            {
                (new DateTime(2026, 10, 13, 10, 8, 0), new DateTime(2026, 10, 13, 10, 9, 0)),
                (new DateTime(2026, 10, 13, 10, 9, 0), new DateTime(2026, 10, 13, 10, 10, 0)),
                (new DateTime(2026, 10, 13, 10, 59, 0), new DateTime(2026, 10, 13, 11, 0, 0)),
                (new DateTime(2026, 10, 13, 23, 59, 0), new DateTime(2026, 10, 14, 0, 0, 0))
            })
            {
                clock.UpdateTime(oldTime);
                var old = Render(clock, clock.DesignSize);
                clock.UpdateTime(newTime);
                var completed = Render(clock, clock.DesignSize);
                clock.FlipProgress = .25;
                var firstHalf = Render(clock, clock.DesignSize);
                check(RegionEqual(old, firstHalf, 0, 110, old.Width, 75),
                    "Outgoing upper leaf retains the complete old lower numeral until the hinge is reached");
                clock.FlipProgress = .75;
                var secondHalf = Render(clock, clock.DesignSize);
                check(RegionEqual(completed, secondHalf, 0, 5, old.Width, 80),
                    "Incoming lower leaf leaves the complete new upper numeral visible");
                if (oldTime.Minute == 8)
                {
                    check(RegionEqual(old, firstHalf, 0, 0, old.Width * 3 / 4 - 6, old.Height),
                        "08 to 09 flips only the changed digit and leaves the first three digits untouched");
                    Save(firstHalf.Bitmap, outputDirectory, "clock-Flip-phase25-FIXTURE.png");
                    Save(secondHalf.Bitmap, outputDirectory, "clock-Flip-phase75-FIXTURE.png");
                }
                clock.FlipProgress = 1;
                var final = Render(clock, clock.DesignSize);
                check(final.Data.SequenceEqual(completed.Data), "Completed flip exactly matches the next static numeral");
            }
            clock.UpdateTime(new DateTime(2026, 10, 13, 10, 8, 0));
            clock.UpdateTime(new DateTime(2026, 10, 13, 10, 9, 0), true);
            check(clock.HasAnimatedProperties, "Flip uses a real bounded WPF animation clock");
            clock.SetActive(false);
            check(!clock.HasAnimatedProperties && clock.FlipProgress == 1, "Suspension cancels the flip even when its wall-clock timer is already inactive");
            clock.UpdateTime(new DateTime(2026, 10, 13, 10, 10, 0), true);
            clock.MotionAllowed = false;
            check(!clock.HasAnimatedProperties && clock.FlipProgress == 1, "Reduced motion immediately settles any in-flight flip");
        }
        clock.MotionAllowed = false;
        clock.UpdateTime(new DateTime(2026, 10, 13, 10, 11, 0), true);
        check(!clock.HasAnimatedProperties && clock.FlipProgress == 1, "Reduced motion updates time with a complete static digit");
        clock.MotionAllowed = true;
        clock.Configure(new HomeSettings(ClockStyle: "Flip", ShowSeconds: true, ShowDate: false));
        clock.UpdateTime(new DateTime(2026, 10, 13, 10, 8, 59));
        var secondsOld = Render(clock, clock.DesignSize);
        clock.UpdateTime(new DateTime(2026, 10, 13, 10, 9, 0));
        clock.FlipProgress = .25;
        var secondsNew = Render(clock, clock.DesignSize);
        check(RegionEqual(secondsOld, secondsNew, 0, 0, secondsOld.Width / 3 - 6, secondsOld.Height),
            "Seconds rollover keeps unchanged hour leaves stable");
        clock.SetActive(false);

        var actual = (HomeClock)window.FindName("ClockView");
        var original = actual.Options; var active = actual.IsRunning;
        try
        {
            var configured = (model.Settings.Home ?? new HomeSettings()) with
                { ClockStyle = "Digital", ShowSeconds = false, ShowDate = true, ClockWidthScale = 1.4, ClockHeightScale = .8 };
            actual.Configure(configured);
            check(window.ClockWidget.NaturalSize == actual.NaturalSize, "Interaction frame and clock share the measured natural size");
            check(window.ClockWidget.Placement.WidthScale == 1.4 && window.ClockWidget.Placement.HeightScale == .8,
                "Clock fitting preserves existing independently saved width and height scales");
        }
        finally { actual.Configure(original); actual.SetActive(active); }
    }

    private sealed record Pixels(RenderTargetBitmap Bitmap, byte[] Data, int Width, int Height);
    private static Pixels Render(HomeClock clock, Size size)
    {
        var width = (int)Math.Ceiling(size.Width); var height = (int)Math.Ceiling(size.Height);
        clock.Measure(size); clock.Arrange(new Rect(new Point(), size)); clock.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(clock); var data = new byte[width * height * 4]; bitmap.CopyPixels(data, width * 4, 0);
        return new Pixels(bitmap, data, width, height);
    }
    private static Rect InkBounds(Pixels image)
    {
        var left = image.Width; var top = image.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            if (image.Data[(y * image.Width + x) * 4 + 3] == 0) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < 0 ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }
    private static bool RegionEqual(Pixels a, Pixels b, int x, int y, int width, int height)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (var row = y; row < y + height; row++)
            if (!a.Data.AsSpan((row * a.Width + x) * 4, width * 4).SequenceEqual(b.Data.AsSpan((row * b.Width + x) * 4, width * 4))) return false;
        return true;
    }
    private static void Save(BitmapSource bitmap, string? directory, string name)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
