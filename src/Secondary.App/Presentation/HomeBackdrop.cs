using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Secondary.Core;

namespace Secondary.App.Presentation;

// This layer owns its media and animation clocks; every suspension tears them down.
public sealed class HomeBackdrop : Grid, IDisposable
{
    private HomeSettings options = new();
    private bool active, motionAllowed = true, highContrast;
    private MediaElement? media;
    private TranslateTransform? movement;
    public event Action<string>? StatusChanged;
    public bool HasMedia => media is not null;
    public bool MediaReady { get; private set; }
    public int CompletedLoops { get; private set; }
    public TimeSpan PlaybackPosition => media?.Position ?? TimeSpan.Zero;
    public bool HasImage => Children.Count > 0 && Children[0] is Image;
    public bool HasMotion => movement?.HasAnimatedProperties == true;
    public bool HasVisualBackground => Background is not null || HasImage || HasMedia;
    public string Status { get; private set; } = "";
    public HomeBackdrop() { ClipToBounds = true; IsHitTestVisible = false; }
    public void Configure(HomeSettings settings) { options = settings.Normalize(); if (active) Rebuild(); }
    public void SetActive(bool enabled, bool allowMotion)
    {
        if (active == enabled && motionAllowed == allowMotion && highContrast == SystemParameters.HighContrast) return;
        active = enabled; motionAllowed = allowMotion; highContrast = SystemParameters.HighContrast; Rebuild();
    }
    private void Report(string text) { Status = text; StatusChanged?.Invoke(text); }
    private void Release()
    {
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
                if (!HomeSettings.IsLocalPath(options.MediaPath) || !File.Exists(options.MediaPath)) throw new IOException("背景檔案不存在，請重新選擇。");
                if (options.Background == "Image")
                {
                    if (new FileInfo(options.MediaPath!).Length > 80 * 1024 * 1024) throw new IOException("圖片超過 80 MB，請改用較小的圖片。");
                    using var stream = new FileStream(options.MediaPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    var width = frame.PixelWidth; var height = frame.PixelHeight;
                    stream.Position = 0;
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (width / (double)Math.Max(1, height) >= 2560d / 1440) bitmap.DecodePixelWidth = Math.Min(width, 2560);
                    else bitmap.DecodePixelHeight = Math.Min(height, 1440);
                    bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill });
                }
                else if (motionAllowed)
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
        { Release(); Report("背景無法載入，已回退靜態背景。" + e.Message); }
    }
    private void OnOpened(object sender, RoutedEventArgs e) { if (ReferenceEquals(sender, media)) MediaReady = true; }
    private void OnEnded(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(sender, media) && active) { CompletedLoops++; media!.Position = TimeSpan.Zero; media.Play(); } }
    private void OnFailed(object? sender, ExceptionRoutedEventArgs e)
    { if (!ReferenceEquals(sender, media)) return; Release(); Report("影片無法播放，已回退靜態背景。請選擇本機 MP4 H.264 影片；不會安裝編解碼器。"); }
    public void Dispose() { active = false; Release(); }
}
