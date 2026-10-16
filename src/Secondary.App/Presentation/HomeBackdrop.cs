using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Secondary.Core;

namespace Secondary.App.Presentation;

// This layer owns its media and animation clocks; every suspension tears them down.
public sealed class HomeBackdrop : Grid, IDisposable
{
    private HomeSettings options = new();
    private bool active, motionAllowed = true, highContrast;
    private MediaElement? media;
    private TranslateTransform? movement;
    private CancellationTokenSource? imageLoadCancellation;
    private int imageGeneration;
    private bool disposed;
    private const long MaxImageBytes = 80L * 1024 * 1024;
    private const long MaxSourcePixels = 100_000_000;
    private const int MaxSourceDimension = 32768;
    public event Action<string>? StatusChanged;
    public bool HasMedia => media is not null;
    public bool MediaReady { get; private set; }
    public int CompletedLoops { get; private set; }
    public TimeSpan PlaybackPosition => media?.Position ?? TimeSpan.Zero;
    public bool HasImage => Children.Count > 0 && Children[0] is Image;
    public bool IsImageLoading => imageLoadCancellation is not null;
    public Task ImageLoadCompletion { get; private set; } = Task.CompletedTask;
    public bool HasMotion => movement?.HasAnimatedProperties == true;
    public bool HasVisualBackground => Background is not null || HasImage || HasMedia;
    public string Status { get; private set; } = "";
    public HomeBackdrop()
    {
        ClipToBounds = true; IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => { if (!IsVisible && active) SetActive(false, motionAllowed); };
        Unloaded += (_, _) => SetActive(false, motionAllowed);
    }
    public void Configure(HomeSettings settings)
    {
        var next = settings.Normalize();
        var changed = next.Background != options.Background || next.MediaPath != options.MediaPath;
        options = next;
        if (!disposed && active && changed) Rebuild();
    }
    public void SetActive(bool enabled, bool allowMotion)
    {
        if (disposed) return;
        if (active == enabled && highContrast == SystemParameters.HighContrast &&
            (motionAllowed == allowMotion || options.Background == "Image")) { motionAllowed = allowMotion; return; }
        active = enabled; motionAllowed = allowMotion; highContrast = SystemParameters.HighContrast; Rebuild();
    }
    private void Report(string text) { Status = text; StatusChanged?.Invoke(text); }
    private void Release()
    {
        imageGeneration++;
        var cancellation = imageLoadCancellation; imageLoadCancellation = null;
        cancellation?.Cancel(); cancellation?.Dispose();
        MediaReady = false;
        movement?.BeginAnimation(TranslateTransform.XProperty, null); movement = null;
        if (media is not null)
        {
            var old = media; media = null;
            old.MediaEnded -= OnEnded; old.MediaFailed -= OnFailed; old.MediaOpened -= OnOpened;
            old.Stop(); old.Close(); old.Source = null;
        }
        foreach (UIElement child in Children) if (child is Image image) image.Source = null;
        Children.Clear(); Background = null;
    }
    private void Rebuild()
    {
        Release(); Report("");
        if (!active || SystemParameters.HighContrast) return;
        try
        {
            if (options.Background is "Image" or "Video")
            {
                if (options.Background == "Image")
                {
                    var cancellation = new CancellationTokenSource();
                    imageLoadCancellation = cancellation;
                    ImageLoadCompletion = LoadImageAsync(options.MediaPath, imageGeneration, cancellation.Token);
                    return;
                }
                if (!HomeSettings.IsLocalPath(options.MediaPath) || !File.Exists(options.MediaPath)) throw new IOException("背景檔案不存在，請重新選擇。");
                if (motionAllowed)
                {
                    if (!string.Equals(System.IO.Path.GetExtension(options.MediaPath), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new IOException("影片請使用 MP4（H.264）。");
                    media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, IsMuted = true, Volume = 0, Stretch = Stretch.UniformToFill, ScrubbingEnabled = false };
                    CompletedLoops = 0;
                    media.MediaEnded += OnEnded; media.MediaFailed += OnFailed; media.MediaOpened += OnOpened;
                    Children.Add(media); media.Source = new Uri(options.MediaPath!, UriKind.Absolute); media.Play();
                }
                else { Report("系統已減少動畫，影片背景暫停，使用靜態背景。"); return; }
                Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = .25 });
            }
            else if (options.Background is "Gradient" or "Aurora")
            {
                Background = new SolidColorBrush(Color.FromRgb(12, 22, 34));
                var glow = new Ellipse { Width = 1800, Height = 1200, Opacity = .8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                glow.Fill = options.Background == "Gradient"
                    ? new LinearGradientBrush(Color.FromRgb(44, 87, 117), Color.FromRgb(83, 51, 91), 35)
                    : new RadialGradientBrush(Color.FromRgb(42, 139, 135), Color.FromArgb(0, 18, 29, 51));
                movement = new TranslateTransform(-180, 0); glow.RenderTransform = movement; Children.Add(glow);
                if (motionAllowed)
                {
                    var drift = new DoubleAnimation(-180, 180, TimeSpan.FromSeconds(options.Background == "Gradient" ? 36 : 24)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                    Timeline.SetDesiredFrameRate(drift, 24);
                    movement.BeginAnimation(TranslateTransform.XProperty, drift);
                }
            }
            Report("");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        { AppDiagnostics.Write("background-load", e); Release(); Report("背景無法載入，已回退靜態背景。" + e.Message); }
    }
    private bool IsCurrentImage(int generation, CancellationToken cancellation) =>
        !disposed && active && !SystemParameters.HighContrast && !cancellation.IsCancellationRequested && generation == imageGeneration && options.Background == "Image";
    private async Task LoadImageAsync(string? path, int generation, CancellationToken cancellation)
    {
        try
        {
            // No file probing or WIC header/decode work executes on the dispatcher.
            // WIC objects stay on this worker until the resulting bitmap is frozen.
            var bitmap = await Task.Run(() => DecodeImage(path, cancellation), cancellation).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentImage(generation, cancellation)) return;
                CompleteImageLoad();
                Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
                Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = .25 });
                Report("");
            }, DispatcherPriority.Normal, cancellation).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            if (cancellation.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            AppDiagnostics.Write("background-image-load", e);
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentImage(generation, cancellation)) return;
                    Release();
                    var detail = e is ImageLimitException ? e.Message : e is FileNotFoundException or DirectoryNotFoundException
                        ? "背景檔案不存在，請重新選擇。" : "請確認圖片格式與檔案存取權限。";
                    Report("背景無法載入，已回退靜態背景。" + detail);
                }, DispatcherPriority.Normal, cancellation).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }
    private void CompleteImageLoad()
    {
        var cancellation = imageLoadCancellation; imageLoadCancellation = null; cancellation?.Dispose();
    }
    private static BitmapImage DecodeImage(string? path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!HomeSettings.IsLocalPath(path)) throw new ImageLimitException("背景檔案不存在，請重新選擇。");
        using var file = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxImageBytes) throw new ImageLimitException("圖片超過 80 MB，請改用較小的圖片。");
        using var stream = new CancellableReadStream(file, cancellation);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new ImageLimitException("圖片沒有可顯示的畫面。");
        var frame = decoder.Frames[0]; var width = frame.PixelWidth; var height = frame.PixelHeight;
        if (width <= 0 || height <= 0 || width > MaxSourceDimension || height > MaxSourceDimension || (long)width * height > MaxSourcePixels)
            throw new ImageLimitException("圖片像素過大，請先縮小到 1 億像素以下，單邊不超過 32,768 像素。");
        cancellation.ThrowIfCancellationRequested(); stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width / (double)height >= 2560d / 1440) bitmap.DecodePixelWidth = Math.Min(width, 2560);
        else bitmap.DecodePixelHeight = Math.Min(height, 1440);
        bitmap.StreamSource = stream; bitmap.EndInit();
        cancellation.ThrowIfCancellationRequested(); bitmap.Freeze(); return bitmap;
    }
    private sealed class ImageLimitException(string message) : IOException(message);
    // WIC's synchronous codec API reads through this bounded, cancellation-aware
    // stream. A native codec operation already running may finish before observing
    // cancellation; the generation check also prevents its result being displayed.
    private sealed class CancellableReadStream(Stream source, CancellationToken cancellation) : Stream
    {
        private readonly long readableLength = Math.Min(source.Length, MaxImageBytes);
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length { get { cancellation.ThrowIfCancellationRequested(); return readableLength; } }
        public override long Position
        {
            get { cancellation.ThrowIfCancellationRequested(); return source.Position; }
            set { cancellation.ThrowIfCancellationRequested(); source.Position = value; }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            return source.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, readableLength - source.Position)));
        }
        public override int Read(Span<byte> buffer)
        {
            cancellation.ThrowIfCancellationRequested();
            return source.Read(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, readableLength - source.Position))]);
        }
        public override int ReadByte() { cancellation.ThrowIfCancellationRequested(); return source.Position >= readableLength ? -1 : source.ReadByte(); }
        public override long Seek(long offset, SeekOrigin origin) { cancellation.ThrowIfCancellationRequested(); return source.Seek(offset, origin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
    private void OnOpened(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, media)) MediaReady = true; }
    private void OnEnded(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(sender, media) && active) { CompletedLoops++; media!.Position = TimeSpan.Zero; media.Play(); } }
    private void OnFailed(object? sender, ExceptionRoutedEventArgs e)
    { if (!ReferenceEquals(sender, media)) return; AppDiagnostics.Write("background-video-playback", e.ErrorException); Release(); Report("影片無法播放，已回退靜態背景。請選擇本機 MP4 H.264 影片；不會安裝編解碼器。"); }
    public void Dispose() { disposed = true; active = false; Release(); }
}
