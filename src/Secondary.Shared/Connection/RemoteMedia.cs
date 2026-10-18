using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// What the main computer is playing, as the media widget shows it. ArtworkKey changes when the cover changes;
// it is empty when there is no cover. Position and Duration are in seconds.
public sealed record RemoteMedia(string App, string Title, string Artist, bool Playing, bool CanPrevious, bool CanNext, bool CanToggle,
    double? Position, double? Duration, string ArtworkKey);

public static class RemoteMediaMessages
{
    public const int MaxArtworkBytes = 700_000;
    public static readonly string[] Commands = ["previous", "toggle", "next"];

    public static JsonObject State(RemoteMedia? media, string status) =>
        Message("media-state", ("media", media is null ? null : JsonSerializer.SerializeToNode(media)), ("status", status));
    public static (RemoteMedia? Media, string Status)? ReadState(JsonObject message)
    {
        var status = Text(message, "status");
        if (status is null || status.Length > 300) return null;
        if (message["media"] is not JsonObject node) return (null, status);
        RemoteMedia? media;
        try { media = node.Deserialize<RemoteMedia>(); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException) { return null; }
        static bool Short(string? text, int limit = 300) => text is not null && text.Length <= limit;
        static bool Seconds(double? value) => value is null || (double.IsFinite(value.Value) && value >= 0 && value < 1e7);
        return media is not null && Short(media.App) && Short(media.Title) && Short(media.Artist) && Short(media.ArtworkKey, 1000) && Seconds(media.Position) && Seconds(media.Duration)
            ? (media, status) : null;
    }
}

// The device side of main-computer media: the helper reports the current player only while at least one
// widget is watching, and controls and artwork are requests. Widgets read Media and listen to Changed.
public sealed class RemoteMediaClient : IDisposable
{
    private readonly ConnectionController controller;
    private readonly object gate = new();
    private int watchers;
    private bool connected;
    private string? artworkKey;
    private byte[]? artwork;
    public event Action? Changed;
    public RemoteMedia? Media { get; private set; }
    public string Status { get; private set; } = "主電腦未連線";
    public bool Connected => controller.State == LinkState.Connected;
    public string HelperName => controller.ConnectedName ?? "主電腦";
    // The app's client, set when connection mode starts; widgets created before or after use it.
    private static RemoteMediaClient? current;
    public static event Action? CurrentChanged;
    public static RemoteMediaClient? Current
    {
        get => current;
        set { current = value; CurrentChanged?.Invoke(); }
    }

    public RemoteMediaClient(ConnectionController controller)
    {
        this.controller = controller;
        controller.MessageReceived += OnMessage;
        controller.Changed += OnLinkChanged;
        OnLinkChanged();
    }

    // Watching starts the helper's subscription to its media session; the last watcher stops it.
    public IDisposable Watch(Action changed)
    {
        bool first;
        lock (gate) { Changed += changed; first = ++watchers == 1; }
        if (first && Connected) _ = Send(Message("media-watch", ("on", true)));
        return new Watcher(this, changed);
    }
    private void Unwatch(Action changed)
    {
        bool last;
        lock (gate) { Changed -= changed; last = --watchers == 0; }
        if (last && Connected) _ = Send(Message("media-watch", ("on", false)));
    }
    private sealed class Watcher(RemoteMediaClient client, Action changed) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) client.Unwatch(changed); }
    }

    public async Task<bool> ControlAsync(string command, CancellationToken token = default)
    {
        if (Array.IndexOf(RemoteMediaMessages.Commands, command) < 0) throw new ArgumentException("未知的媒體指令", nameof(command));
        var reply = await controller.RequestAsync(Message("media-control", ("command", command)), TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        return reply["ok"]?.GetValueKind() == JsonValueKind.True;
    }

    // The cover for a key, fetched once and kept until the key changes.
    public async Task<byte[]?> ArtworkAsync(string key, CancellationToken token = default)
    {
        lock (gate) if (key == artworkKey) return artwork;
        var reply = await controller.RequestAsync(Message("media-artwork", ("key", key)), TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        byte[]? data = null;
        if (Text(reply, "data") is { Length: <= RemoteMediaMessages.MaxArtworkBytes / 3 * 4 + 4 } encoded)
            try { data = Convert.FromBase64String(encoded); } catch (FormatException) { }
        if (data is { Length: 0 or > RemoteMediaMessages.MaxArtworkBytes }) data = null;
        lock (gate) { artworkKey = key; artwork = data; }
        return data;
    }

    private void OnMessage(JsonObject message)
    {
        if (TypeOf(message) != "media-state" || RemoteMediaMessages.ReadState(message) is not { } state) return;
        Media = state.Media; Status = state.Status;
        Changed?.Invoke();
    }
    private void OnLinkChanged()
    {
        var now = Connected;
        bool resubscribe, lost;
        lock (gate) { resubscribe = now && !connected && watchers > 0; lost = !now && connected; connected = now; }
        if (resubscribe) _ = Send(Message("media-watch", ("on", true)));
        if (lost || (!now && Media is not null)) { Media = null; }
        var status = now ? Media is null && Status == "主電腦未連線" ? "等待主電腦回報媒體" : Status : "主電腦未連線";
        if (status != Status || lost) { Status = status; Changed?.Invoke(); }
    }
    private async Task Send(JsonObject message)
    {
        try { await controller.SendAsync(message).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        controller.MessageReceived -= OnMessage;
        controller.Changed -= OnLinkChanged;
    }
}
