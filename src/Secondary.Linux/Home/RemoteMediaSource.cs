using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Shared.Connection;

namespace Secondary.Linux.Home;

// The main computer's player for media widgets set to "主電腦", over the connection. Covers are written to a
// private temporary file because the widget only loads local artwork files.
public sealed class RemoteMediaSource : IMediaSource
{
    public static readonly RemoteMediaSource Shared = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "secondary-remote-artwork-" + Environment.UserName);
    private readonly object gate = new();
    private string? coverKey, coverPath;

    public static string Status => RemoteMediaClient.Current is not { } client ? "連線模式無法使用；可切回本機"
        : !client.Connected ? "主電腦未連線；連上後自動顯示，或切回本機" : client.Status;

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken token)
    {
        if (RemoteMediaClient.Current is not { Connected: true, Media: { } media } client) return null;
        var artwork = media.ArtworkKey.Length == 0 ? null : await CoverAsync(client, media.ArtworkKey, token).ConfigureAwait(false);
        return new MediaSnapshot("主電腦 · " + media.App, media.Title, media.Artist, media.Playing, media.CanPrevious, media.CanNext, media.CanToggle, media.CanToggle,
            media.Position is { } position ? TimeSpan.FromSeconds(position) : null, media.Duration is { } duration ? TimeSpan.FromSeconds(duration) : null, artwork);
    }

    private async Task<string?> CoverAsync(RemoteMediaClient client, string key, CancellationToken token)
    {
        lock (gate) if (key == coverKey) return coverPath;
        var bytes = await client.ArtworkAsync(key, token).ConfigureAwait(false);
        string? path = null;
        if (bytes is not null)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".img");
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
        }
        lock (gate)
        {
            if (coverPath is not null && coverPath != path) try { File.Delete(coverPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            coverKey = key; coverPath = path;
        }
        return path;
    }

    public async Task<bool> ControlAsync(MediaCommand command, CancellationToken token) =>
        RemoteMediaClient.Current is { Connected: true } client && await client.ControlAsync(command switch
        {
            MediaCommand.Previous => "previous", MediaCommand.Next => "next", _ => "toggle"
        }, token).ConfigureAwait(false);

    // Follows the app's client even when connection mode starts after the widget subscribed.
    public IDisposable Subscribe(Action changed) => new Subscription(changed);
    private sealed class Subscription : IDisposable
    {
        private readonly Action changed;
        private readonly object gate = new();
        private IDisposable? watch;
        private bool disposed;
        public Subscription(Action changed)
        {
            this.changed = changed;
            RemoteMediaClient.CurrentChanged += Attach;
            Attach();
        }
        private void Attach()
        {
            lock (gate)
            {
                if (disposed) return;
                watch?.Dispose();
                watch = RemoteMediaClient.Current?.Watch(changed);
            }
            changed();
        }
        public void Dispose()
        {
            RemoteMediaClient.CurrentChanged -= Attach;
            lock (gate) { disposed = true; watch?.Dispose(); watch = null; }
        }
    }
}
