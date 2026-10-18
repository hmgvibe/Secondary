using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;

internal static class BackgroundOptimizationChecks
{
    public static void Run(MainWindow window, ShellViewModel model, Action<bool, string> check, string? outputDirectory)
    {
        using var backdrop = new HomeBackdrop();
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Secondary.BackgroundTests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var red = System.IO.Path.Combine(directory, "red.png");
            var blue = System.IO.Path.Combine(directory, "blue.png");
            WriteImage(red, 80, 60, Colors.Red); WriteImage(blue, 160, 100, Colors.Blue);
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: red));
            backdrop.SetActive(true, false);
            if (SystemParameters.HighContrast)
            {
                check(!backdrop.IsImageLoading && !backdrop.HasImage && !backdrop.HasVisualBackground,
                    "High contrast skips background file reading and decoding entirely");
                return;
            }
            check(backdrop.IsImageLoading && !backdrop.HasImage, "Image activation schedules decode without synchronously constructing an image");
            var cancelled = backdrop.ImageLoadCompletion;
            backdrop.SetActive(false, false);
            Wait(cancelled);
            check(!backdrop.IsImageLoading && !backdrop.HasImage && backdrop.Children.Count == 0,
                "An image result completed after suspension never reappears");

            backdrop.SetActive(true, false);
            var stale = backdrop.ImageLoadCompletion;
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: blue));
            var current = backdrop.ImageLoadCompletion;
            Wait(stale); Wait(current);
            var source = ((Image)backdrop.Children[0]).Source as BitmapSource;
            check(backdrop.HasImage && !backdrop.IsImageLoading && source is { IsFrozen: true, PixelWidth: 160, PixelHeight: 100 },
                "Only the current frozen worker-decoded image is applied after a settings change");
            check(backdrop.Status.Length == 0 && backdrop.Children.Count == 2 && !backdrop.HasMedia,
                "Image backgrounds retain the readability overlay and work with reduced motion");
            var firstImage = backdrop.Children[0];
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: blue, ShowSeconds: true));
            backdrop.SetActive(true, true); backdrop.SetActive(true, false);
            check(ReferenceEquals(firstImage, backdrop.Children[0]) && ReferenceEquals(current, backdrop.ImageLoadCompletion),
                "Unrelated clock or motion settings reuse the current image instead of repeating decode");
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                Save((BitmapSource)((Image)firstImage).Source, System.IO.Path.Combine(outputDirectory, "background-current-image-FIXTURE.png"));
            }
            backdrop.SetActive(false, false);
            check(((Image)firstImage).Source is null && !backdrop.HasImage, "Suspension drops decoded bitmap references");

            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: red + ".missing"));
            backdrop.SetActive(true, false); Wait(backdrop.ImageLoadCompletion);
            check(backdrop.Status.Contains("回退") && !backdrop.HasImage && !backdrop.IsImageLoading,
                "Missing images report a completed asynchronous fallback");
            var corrupt = System.IO.Path.Combine(directory, "corrupt.png");
            File.WriteAllText(corrupt, "invalid image fixture");
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: corrupt)); Wait(backdrop.ImageLoadCompletion);
            check(backdrop.Status.Contains("回退") && !backdrop.HasVisualBackground,
                "Invalid image data fails on the worker and leaves a usable static background");
            var oversized = System.IO.Path.Combine(directory, "oversized.png");
            using (var file = File.Create(oversized)) file.SetLength(80L * 1024 * 1024 + 1);
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: oversized)); Wait(backdrop.ImageLoadCompletion);
            check(backdrop.Status.Contains("80 MB") && !backdrop.HasImage,
                "The existing 80 MB file limit is enforced before attempting decode");

            var hugeHeader = System.IO.Path.Combine(directory, "huge-header.png");
            var header = File.ReadAllBytes(red);
            WriteBigEndian(header, 16, 20_000); WriteBigEndian(header, 20, 20_000);
            WriteBigEndian(header, 29, Crc(header.AsSpan(12, 17)));
            File.WriteAllBytes(hugeHeader, header);
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: hugeHeader)); Wait(backdrop.ImageLoadCompletion);
            check(backdrop.Status.Contains("像素") && !backdrop.HasImage,
                "Excessive source dimensions are rejected from image headers before allocating their pixel surface");

            var landscape = System.IO.Path.Combine(directory, "landscape.png");
            var portrait = System.IO.Path.Combine(directory, "portrait.png");
            WriteImage(landscape, 4000, 1000, Colors.Teal);
            WriteImage(portrait, 1000, 2500, Colors.Orange);
            foreach (var path in new[] { landscape, portrait })
            {
                backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: path)); Wait(backdrop.ImageLoadCompletion);
                var decoded = ((Image)backdrop.Children[0]).Source as BitmapSource;
                check(decoded is { IsFrozen: true } && decoded.PixelWidth <= 2560 && decoded.PixelHeight <= 1440,
                    "Landscape and portrait images both decode within 2560 by 1440 pixels");
            }
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: red));
            backdrop.SetActive(true, false);
            var disposed = backdrop.ImageLoadCompletion;
            backdrop.Dispose(); Wait(disposed);
            check(!backdrop.HasImage && !backdrop.IsImageLoading && !backdrop.HasVisualBackground,
                "Disposal cancels pending decode and rejects late worker results");
            backdrop.Configure(new HomeSettings(Background: "Image", MediaPath: blue)); backdrop.SetActive(true, true);
            check(!backdrop.IsImageLoading && !backdrop.HasImage, "Disposed backgrounds never restart their resources");
        }
        finally { backdrop.Dispose(); Directory.Delete(directory, true); }
    }
    private static void Wait(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new TimeoutException("Background decode test did not finish");
        task.GetAwaiter().GetResult();
    }
    private static void WriteImage(string path, int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        Save(bitmap, path);
    }
    private static void Save(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void WriteBigEndian(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value;
    }
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        }
        return ~crc;
    }
}
