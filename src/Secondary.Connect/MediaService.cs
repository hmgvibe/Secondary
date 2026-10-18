using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Secondary.Desktop;
using Secondary.Shared;
using Secondary.Shared.Connection;
using Windows.Media.Control;
using Windows.Storage.Streams;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Connect;

// The main computer's player, read and controlled for a device. Tests substitute a fake.
public interface IMediaProvider
{
    IDisposable Subscribe(Action changed);
    Task<(RemoteMedia? Media, string Status)> ReadAsync(CancellationToken token);
    Task<bool> ControlAsync(string command, CancellationToken token);
    Task<byte[]?> ArtworkAsync(string key, CancellationToken token);
}

// Uses the same Windows media session source as the WPF media widget.
public sealed class WindowsMediaProvider : IMediaProvider
{
    private readonly object gate = new();
    private (string Key, IRandomAccessStreamReference Thumbnail)? cover;
    public IDisposable Subscribe(Action changed) => MediaWidgetSource.Shared.Subscribe(changed);

    public async Task<(RemoteMedia? Media, string Status)> ReadAsync(CancellationToken token)
    {
        var snapshot = await MediaWidgetSource.Shared.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (snapshot.Session is not { } session || snapshot.Properties is not { } properties || snapshot.Playback is not { } playback)
            return (null, "主電腦目前沒有播放中的媒體");
        var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double? position = null, duration = null;
        if (snapshot.Timeline is { } timeline && (timeline.EndTime - timeline.StartTime) is { TotalSeconds: > 0 } length)
        { duration = length.TotalSeconds; position = Math.Clamp((timeline.Position - timeline.StartTime).TotalSeconds, 0, length.TotalSeconds); }
        var key = properties.Thumbnail is null ? "" : $"{session.SourceAppUserModelId}|{properties.Title}|{properties.Artist}|{snapshot.ArtworkRevision}";
        lock (gate) cover = properties.Thumbnail is null ? null : (key, properties.Thumbnail);
        static string Clip(string? text) => (text ?? "").Length > 300 ? text![..300] : text ?? "";
        return (new RemoteMedia(Clip(session.SourceAppUserModelId), Clip(properties.Title), Clip(properties.Artist), playing,
            playback.Controls.IsPreviousEnabled, playback.Controls.IsNextEnabled,
            playback.Controls.IsPlayPauseToggleEnabled || (playing ? playback.Controls.IsPauseEnabled : playback.Controls.IsPlayEnabled),
            position, duration, key.Length > 1000 ? key[..1000] : key), playing ? "播放中" : "已暫停");
    }

    public async Task<bool> ControlAsync(string command, CancellationToken token)
    {
        var session = (await MediaWidgetSource.Shared.ManagerAsync().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false)).GetCurrentSession();
        if (session is null) return false;
        var info = session.GetPlaybackInfo();
        return command switch
        {
            "previous" => await session.TrySkipPreviousAsync().AsTask(token).ConfigureAwait(false),
            "next" => await session.TrySkipNextAsync().AsTask(token).ConfigureAwait(false),
            _ when info.Controls.IsPlayPauseToggleEnabled => await session.TryTogglePlayPauseAsync().AsTask(token).ConfigureAwait(false),
            _ when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => await session.TryPauseAsync().AsTask(token).ConfigureAwait(false),
            _ => await session.TryPlayAsync().AsTask(token).ConfigureAwait(false)
        };
    }

    // Only the cover of the current track is served, and only up to the size a message can carry.
    public async Task<byte[]?> ArtworkAsync(string key, CancellationToken token)
    {
        IRandomAccessStreamReference? thumbnail;
        lock (gate) thumbnail = cover is { } current && current.Key == key ? current.Thumbnail : null;
        if (thumbnail is null) return null;
        using var stream = await thumbnail.OpenReadAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (stream.Size is 0 or > RemoteMediaMessages.MaxArtworkBytes) return null;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = (uint)stream.Size;
        if (await reader.LoadAsync(size).AsTask(token).ConfigureAwait(false) != size) return null;
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

// Reports the player to the device only while one of its media widgets is on screen ("media-watch"),
// on every change and every few seconds while playing so the position stays current.
public sealed class MediaService(IMediaProvider? provider = null)
{
    private readonly IMediaProvider provider = provider ?? new WindowsMediaProvider();
    private readonly object gate = new();
    private CancellationTokenSource? watching;
    public event Action? Changed;
    public bool Watching { get { lock (gate) return watching is not null; } }
    public string Status => Watching ? "媒體：設備正在顯示主電腦的播放內容" : "媒體：未使用";

    public Task Handle(JsonObject message, FrameChannel channel, CancellationToken session)
    {
        switch (TypeOf(message))
        {
            case "media-watch" when message["on"]?.GetValueKind() == System.Text.Json.JsonValueKind.True: Watch(channel, session); break;
            case "media-watch": Stop(); break;
            case "media-control" when Text(message, "command") is { } command && Array.IndexOf(RemoteMediaMessages.Commands, command) >= 0:
                _ = Answer(message, channel, session, async token => Message("media-reply", ("ok", await provider.ControlAsync(command, token).ConfigureAwait(false))));
                break;
            case "media-artwork" when Text(message, "key") is { } key:
                _ = Answer(message, channel, session, async token =>
                {
                    var data = await provider.ArtworkAsync(key, token).ConfigureAwait(false);
                    return Message("media-reply", ("key", key), ("data", data is null ? null : Convert.ToBase64String(data)));
                });
                break;
        }
        return Task.CompletedTask;
    }
    // A watch that ended on its own may already have disposed its source.
    private static void Cancel(CancellationTokenSource? owner)
    {
        try { owner?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private static async Task Answer(JsonObject request, FrameChannel channel, CancellationToken session, Func<CancellationToken, Task<JsonObject>> answer)
    {
        JsonObject reply;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(session))
        {
            limit.CancelAfter(TimeSpan.FromSeconds(6));
            try { reply = await answer(limit.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !session.IsCancellationRequested)
            {
                AppDiagnostics.Write("connect-media", error);
                reply = Message("media-reply", ("ok", false));
            }
            catch (OperationCanceledException) { return; }
        }
        try { await channel.SendAsync(Reply(request, reply), session).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private void Watch(FrameChannel channel, CancellationToken session)
    {
        var owner = CancellationTokenSource.CreateLinkedTokenSource(session);
        CancellationTokenSource? previous;
        lock (gate) { previous = watching; watching = owner; }
        Cancel(previous);
        _ = Task.Run(() => Report(channel, owner));
        Changed?.Invoke();
    }
    public void Stop()
    {
        CancellationTokenSource? owner;
        lock (gate) { owner = watching; watching = null; }
        if (owner is null) return;
        Cancel(owner);
        Changed?.Invoke();
    }

    private async Task Report(FrameChannel channel, CancellationTokenSource owner)
    {
        var token = owner.Token;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        string? last = null;
        try
        {
            using var subscription = provider.Subscribe(() => signal.Writer.TryWrite(true));
            while (!token.IsCancellationRequested)
            {
                (RemoteMedia? Media, string Status) state;
                try { state = await provider.ReadAsync(token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { state = (null, "無法讀取主電腦的媒體工作階段"); }
                var message = RemoteMediaMessages.State(state.Media, state.Status);
                var text = message.ToJsonString();
                if (text != last) { await channel.SendAsync(message, token).ConfigureAwait(false); last = text; }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(state.Media?.Playing == true ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));
                try { await signal.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            var ended = false;
            lock (gate) if (watching == owner) { watching = null; ended = true; }
            owner.Dispose();
            if (ended) Changed?.Invoke();
        }
    }
}
