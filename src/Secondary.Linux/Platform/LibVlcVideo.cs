using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Secondary.Linux.Home;
using Secondary.Shared;

namespace Secondary.Linux.Platform;

// Plays a muted, looping local video through the system's libvlc. Frames are decoded into memory
// and copied into an Avalonia bitmap, so widgets can draw on top (a native video surface could not).
// Secondary never installs libvlc or codecs; a missing library falls back to the static background.
public sealed class LibVlcVideo : IVideoBackground
{
    // A dimmed background needs neither full resolution nor more than 24 updates a second.
    private const int MaxWidth = 1280;
    private static readonly long FrameInterval = System.Diagnostics.Stopwatch.Frequency / 24;
    private static readonly object initialization = new();
    private static LibVLC? library;
    private readonly object frameGate = new();
    private readonly Image view = new() { Stretch = Stretch.UniformToFill };
    private readonly MediaPlayer player;
    private readonly Media media;
    // Callbacks are held in fields so the GC cannot collect them while libvlc calls them.
    private readonly MediaPlayer.LibVLCVideoFormatCb formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb cleanupCallback;
    private readonly MediaPlayer.LibVLCVideoLockCb lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb displayCallback;
    private IntPtr buffer;
    private int width, height, pendingFrame;
    private long lastFrame;
    private WriteableBitmap? bitmap;
    private bool disposed;
    public Control View => view;
    public int FramesShown { get; private set; }
    public event Action<string>? Failed;

    public LibVlcVideo(string path)
    {
        lock (initialization)
        {
            if (library is null)
            {
                RegisterVersionedLibraries();
                LibVLCSharp.Shared.Core.Initialize();
                library = new LibVLC("--no-audio", "--quiet", "--no-video-title-show", "--no-osd", "--no-stats");
            }
        }
        media = new Media(library, new Uri(path));
        media.AddOption(":input-repeat=65535");
        player = new MediaPlayer(media) { Mute = true };
        formatCallback = Format; cleanupCallback = Cleanup; lockCallback = Lock; unlockCallback = Unlock; displayCallback = Display;
        player.SetVideoFormatCallbacks(formatCallback, cleanupCallback);
        player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        player.EncounteredError += (_, _) => Failed?.Invoke("請選擇本機 MP4 H.264 影片；系統的 VLC 播放元件無法解碼此檔案。");
    }
    private static bool resolverRegistered;
    // Distributions ship libvlc.so.5 without the unversioned libvlc.so (that comes with -dev packages),
    // so map LibVLCSharp's names to the runtime sonames before the first call.
    private static void RegisterVersionedLibraries()
    {
        if (resolverRegistered || !OperatingSystem.IsLinux()) return;
        resolverRegistered = true;
        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, assembly, search) =>
        {
            var soname = name switch { "libvlc" or "libvlc.so" => "libvlc.so.5", "libvlccore" or "libvlccore.so" => "libvlccore.so.9", _ => null };
            return soname is not null && NativeLibrary.TryLoad(soname, assembly, search, out var handle) ? handle : IntPtr.Zero;
        });
    }
    public static IVideoBackground Create(string path)
    {
        try { return new LibVlcVideo(path); }
        catch (Exception error) when (error is VLCException or DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        { throw new InvalidOperationException("找不到系統的 libvlc。", error); }
    }
    public void Play() => player.Play();

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint sourceWidth, ref uint sourceHeight, ref uint pitches, ref uint lines)
    {
        // Ask for 32-bit BGRA, scaled down to at most 1280 pixels wide.
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        var scale = Math.Min(1d, MaxWidth / (double)Math.Max(1, sourceWidth));
        lock (frameGate)
        {
            width = Math.Max(2, (int)(sourceWidth * scale) & ~1);
            height = Math.Max(2, (int)(sourceHeight * scale) & ~1);
            sourceWidth = (uint)width; sourceHeight = (uint)height;
            pitches = (uint)(width * 4); lines = (uint)height;
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = Marshal.AllocHGlobal(width * height * 4);
        }
        return 1;
    }
    private void Cleanup(ref IntPtr opaque) { }
    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(frameGate);
        Marshal.WriteIntPtr(planes, buffer);
        return IntPtr.Zero;
    }
    private void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) => Monitor.Exit(frameGate);
    private void Display(IntPtr opaque, IntPtr picture)
    {
        // At most one copy is queued and at most 24 per second; the UI thread always takes the newest frame.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref lastFrame) < FrameInterval || Interlocked.Exchange(ref pendingFrame, 1) == 1) return;
        Interlocked.Exchange(ref lastFrame, now);
        Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }
    private void CopyFrame()
    {
        Interlocked.Exchange(ref pendingFrame, 0);
        if (disposed) return;
        lock (frameGate)
        {
            if (buffer == IntPtr.Zero) return;
            if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                view.Source = bitmap;
            }
            using var target = bitmap.Lock();
            for (var row = 0; row < height; row++)
                unsafe { Buffer.MemoryCopy((byte*)buffer + row * width * 4, (byte*)target.Address + row * target.RowBytes, target.RowBytes, width * 4); }
        }
        FramesShown++;
        view.InvalidateVisual();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        view.Source = null;
        var oldBitmap = bitmap; bitmap = null;
        // Stopping libvlc waits for its decoder threads, so it never runs on the UI thread.
        _ = Task.Run(() =>
        {
            player.Stop();
            player.Dispose(); media.Dispose();
            lock (frameGate) { if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; } }
            Dispatcher.UIThread.Post(() => oldBitmap?.Dispose());
        }).ContinueWith(t => { if (t.Exception is { } error) AppDiagnostics.Write("background-video-stop", error.GetBaseException()); }, TaskScheduler.Default);
    }
}
