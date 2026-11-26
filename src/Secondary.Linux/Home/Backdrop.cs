using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.Linux.Home;

// A video decoder the backdrop can host; libvlc provides the Linux one.
public interface IVideoBackground : IDisposable
{
    Control View { get; }
    int FramesShown { get; }
    event Action<string>? Failed;
    void Play();
}

// This layer owns its media and animation clocks; every suspension tears them down, as in WPF.
public sealed class Backdrop : Panel, IDisposable
{
    private const long MaxImageBytes = 80L * 1024 * 1024;
    private const long MaxSourcePixels = 100_000_000;
    private const int MaxSourceDimension = 32768;
    private HomeSettings options = new();
    private bool active, motionAllowed = true, highContrast, disposed;
    private CancellationTokenSource? imageLoadCancellation;
    private int imageGeneration;
    private IVideoBackground? video;
    public Func<string, IVideoBackground>? VideoFactory { get; set; }
    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "";
    public bool HasImage => Children.Count > 0 && Children[0] is Image;
    public bool HasVideo => video is not null;
    public int VideoFramesShown => video?.FramesShown ?? 0;
    public bool IsImageLoading => imageLoadCancellation is not null;
    public Task ImageLoadCompletion { get; private set; } = Task.CompletedTask;
    public bool HasVisualBackground => HasImage || HasVideo;

    public Backdrop()
    {
        ClipToBounds = true; IsHitTestVisible = false;
        DetachedFromVisualTree += (_, _) => SetActive(false, motionAllowed, highContrast);
    }
    public void Configure(HomeSettings settings)
    {
        var next = settings.Normalize();
        var changed = next.Background != options.Background || next.MediaPath != options.MediaPath;
        options = next;
        if (!disposed && active && changed) Rebuild();
    }
    public void SetActive(bool enabled, bool allowMotion, bool contrast)
    {
        if (disposed) return;
        if (active == enabled && highContrast == contrast && (motionAllowed == allowMotion || options.Background == "Image")) { motionAllowed = allowMotion; return; }
        active = enabled; motionAllowed = allowMotion; highContrast = contrast; Rebuild();
    }
    private void Report(string text) { Status = text; StatusChanged?.Invoke(text); }
    private void Release()
    {
        imageGeneration++;
        var cancellation = imageLoadCancellation; imageLoadCancellation = null;
        cancellation?.Cancel(); cancellation?.Dispose();
        if (video is not null) { var old = video; video = null; old.Failed -= OnVideoFailed; old.Dispose(); }
        foreach (var child in Children) if (child is Image { Source: Bitmap bitmap } image) { image.Source = null; bitmap.Dispose(); }
        Children.Clear();
    }
    private void Rebuild()
    {
        Release(); Report("");
        if (!active || highContrast) return;
        if (options.Background == "Image")
        {
            var cancellation = new CancellationTokenSource();
            imageLoadCancellation = cancellation;
            ImageLoadCompletion = LoadImageAsync(options.MediaPath, imageGeneration, cancellation.Token);
        }
        else if (options.Background == "Video") StartVideo();
        // Report again once the layer exists, so listeners see the visual background (as WPF does).
        if (Status.Length == 0) Report("");
    }
    private void StartVideo()
    {
        if (!HomeSettings.IsLocalPath(options.MediaPath) || !File.Exists(options.MediaPath)) { Report("背景无法载入，已回退静态背景。背景文件不存在，请重新选择。"); return; }
        if (!motionAllowed) { Report("系统已减少动画，视频背景暂停，使用静态背景。"); return; }
        if (!string.Equals(System.IO.Path.GetExtension(options.MediaPath), ".mp4", StringComparison.OrdinalIgnoreCase)) { Report("背景无法载入，已回退静态背景。视频请使用 MP4（H.264）。"); return; }
        if (VideoFactory is null) { Report("此环境无法播放视频背景，已回退静态背景。"); return; }
        try
        {
            video = VideoFactory(options.MediaPath!);
            video.Failed += OnVideoFailed;
            Children.Add(video.View);
            Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = .25 });
            video.Play();
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or InvalidOperationException or TypeInitializationException or EntryPointNotFoundException)
        {
            AppDiagnostics.Write("background-video", error.InnerException ?? error);
            Release();
            Report("视频无法播放，已回退静态背景。请安装系统的 VLC 播放元件（libvlc），并选择本机 MP4 H.264 视频；Secondary 不会自行安装解码器。");
        }
    }
    private void OnVideoFailed(string reason) => Dispatcher.UIThread.Post(() =>
    {
        if (video is null) return;
        Release();
        Report("视频无法播放，已回退静态背景。" + reason);
    });
    private bool IsCurrentImage(int generation, CancellationToken cancellation) =>
        !disposed && active && !highContrast && !cancellation.IsCancellationRequested && generation == imageGeneration && options.Background == "Image";
    private async Task LoadImageAsync(string? path, int generation, CancellationToken cancellation)
    {
        try
        {
            // File probing and decoding never run on the UI thread.
            var bitmap = await Task.Run(() => DecodeImage(path, cancellation), cancellation).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentImage(generation, cancellation)) { bitmap.Dispose(); return; }
                var finished = imageLoadCancellation; imageLoadCancellation = null; finished?.Dispose();
                Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
                Children.Add(new Rectangle { Fill = Brushes.Black, Opacity = .25 });
                Report("");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            if (cancellation.IsCancellationRequested) return;
            AppDiagnostics.Write("background-image-load", e);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentImage(generation, cancellation)) return;
                Release();
                var detail = e is ImageLimitException ? e.Message : e is FileNotFoundException or DirectoryNotFoundException
                    ? "背景文件不存在，请重新选择。" : "请确认图片格式与文件存取权限。";
                Report("背景无法载入，已回退静态背景。" + detail);
            });
        }
    }
    private static Bitmap DecodeImage(string? path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!HomeSettings.IsLocalPath(path)) throw new ImageLimitException("背景文件不存在，请重新选择。");
        using var file = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > MaxImageBytes) throw new ImageLimitException("图片超过 80 MB，请改用较小的图片。");
        int width, height;
        // Read only the header first, so oversized images are rejected before decoding.
        using (var header = new SkiaSharp.SKManagedStream(file, disposeManagedStream: false))
        using (var codec = SkiaSharp.SKCodec.Create(header) ?? throw new ImageLimitException("请确认图片格式与文件存取权限。"))
        { width = codec.Info.Width; height = codec.Info.Height; }
        if (width <= 0 || height <= 0 || width > MaxSourceDimension || height > MaxSourceDimension || (long)width * height > MaxSourcePixels)
            throw new ImageLimitException("图片像素过大，请先缩小到 1 亿像素以下，单边不超过 32,768 像素。");
        cancellation.ThrowIfCancellationRequested();
        file.Position = 0;
        var bitmap = width / (double)height >= 2560d / 1440
            ? Bitmap.DecodeToWidth(file, Math.Min(width, 2560), BitmapInterpolationMode.HighQuality)
            : Bitmap.DecodeToHeight(file, Math.Min(height, 1440), BitmapInterpolationMode.HighQuality);
        if (cancellation.IsCancellationRequested) { bitmap.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        return bitmap;
    }
    private sealed class ImageLimitException(string message) : IOException(message);
    public void Dispose() { disposed = true; active = false; Release(); }
}
