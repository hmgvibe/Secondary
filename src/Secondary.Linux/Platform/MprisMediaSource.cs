using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Linux.Home;
using Secondary.Shared;
using Tmds.DBus.Protocol;

namespace Secondary.Linux.Platform;

// Media players publish org.mpris.MediaPlayer2.* on the session bus. The playing player wins,
// then a paused one, as Windows' current-session choice does. Signals are only watched while a
// media widget is active.
public sealed class MprisMediaSource : IMediaSource
{
    private const string Prefix = "org.mpris.MediaPlayer2.";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    public static IMediaSource Shared { get; } = OperatingSystem.IsLinux() && DBusAddress.Session is not null ? new MprisMediaSource() : new UnavailableMediaSource();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Action> subscribers = new();
    private readonly List<IDisposable> watches = new();
    private DBusConnection? connection;
    private string? currentPlayer;

    private async Task<DBusConnection> ConnectionAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (connection is not null) return connection;
            var next = new DBusConnection(DBusAddress.Session!);
            try { await next.ConnectAsync().ConfigureAwait(false); }
            catch { next.Dispose(); throw; }
            return connection = next;
        }
        finally { gate.Release(); }
    }

    private delegate void Body(ref MessageWriter writer);
    private static MessageBuffer Call(DBusConnection bus, string destination, string path, string @interface, string member, string? signature, Body? body)
    {
        var writer = bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature, MessageFlags.None);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    public async Task<MediaSnapshot?> ReadAsync(CancellationToken token)
    {
        var bus = await ConnectionAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var names = await bus.CallMethodAsync(Call(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames", null, null),
            (m, _) => m.GetBodyReader().ReadArrayOfString(), null).ConfigureAwait(false);
        var players = new List<(string Name, Dictionary<string, VariantValue> Properties)>();
        foreach (var name in names.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var properties = await bus.CallMethodAsync(Call(bus, name, ObjectPath, "org.freedesktop.DBus.Properties", "GetAll", "s", (ref MessageWriter w) => w.WriteString(PlayerInterface)),
                    (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(), null).WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                players.Add((name, properties));
            }
            // A player that disappears or hangs mid-query is skipped, not fatal.
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException) { }
        }
        static string Status(Dictionary<string, VariantValue> p) => p.TryGetValue("PlaybackStatus", out var v) && v.Type == VariantValueType.String ? v.GetString() : "";
        var chosen = players.OrderBy(p => Status(p.Properties) switch { "Playing" => 0, "Paused" => 1, _ => 2 }).FirstOrDefault();
        if (chosen.Name is null) { currentPlayer = null; return null; }
        currentPlayer = chosen.Name;
        return Snapshot(chosen.Name, chosen.Properties);
    }

    internal static MediaSnapshot Snapshot(string player, IReadOnlyDictionary<string, VariantValue> properties)
    {
        bool Flag(string key) => properties.TryGetValue(key, out var v) && v.Type == VariantValueType.Bool && v.GetBool();
        var status = properties.TryGetValue("PlaybackStatus", out var s) && s.Type == VariantValueType.String ? s.GetString() : "";
        string title = "", artist = ""; TimeSpan? duration = null; string? art = null;
        if (properties.TryGetValue("Metadata", out var metadata) && metadata.Type == VariantValueType.Dictionary)
        {
            for (var i = 0; i < metadata.Count; i++)
            {
                var entry = metadata.GetDictionaryEntry(i);
                var value = Unwrap(entry.Value);
                switch (entry.Key.GetString())
                {
                    case "xesam:title" when value.Type == VariantValueType.String: title = value.GetString(); break;
                    case "xesam:artist" when value.Type == VariantValueType.Array:
                        artist = string.Join("、", Enumerable.Range(0, value.Count).Select(n => Unwrap(value.GetItem(n))).Where(v => v.Type == VariantValueType.String).Select(v => v.GetString()));
                        break;
                    case "xesam:artist" when value.Type == VariantValueType.String: artist = value.GetString(); break;
                    case "mpris:length": duration = Microseconds(value); break;
                    case "mpris:artUrl" when value.Type == VariantValueType.String: art = LocalArtwork(value.GetString()); break;
                }
            }
        }
        var position = properties.TryGetValue("Position", out var p) ? Microseconds(Unwrap(p)) : null;
        var canControl = !properties.ContainsKey("CanControl") || Flag("CanControl");
        return new MediaSnapshot(player, title, artist, status == "Playing",
            canControl && Flag("CanGoPrevious"), canControl && Flag("CanGoNext"), canControl && Flag("CanPlay"), canControl && Flag("CanPause"),
            position, duration, art);
    }
    private static VariantValue Unwrap(VariantValue value) { while (value.Type == VariantValueType.Variant) value = value.GetVariantValue(); return value; }
    private static TimeSpan? Microseconds(VariantValue value) => value.Type switch
    {
        VariantValueType.Int64 when value.GetInt64() > 0 => TimeSpan.FromTicks(value.GetInt64() * 10),
        VariantValueType.UInt64 when value.GetUInt64() is > 0 and < long.MaxValue / 10 => TimeSpan.FromTicks((long)value.GetUInt64() * 10),
        VariantValueType.Int32 when value.GetInt32() > 0 => TimeSpan.FromTicks(value.GetInt32() * 10L),
        _ => null
    };
    // Only file:// artwork is shown; remote artwork would mean another network request.
    internal static string? LocalArtwork(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile && uri.LocalPath.StartsWith('/') ? uri.LocalPath : null;

    public async Task<bool> ControlAsync(MediaCommand command, CancellationToken token)
    {
        var snapshot = await ReadAsync(token).ConfigureAwait(false);
        if (snapshot is null || currentPlayer is not { } player) return false;
        var (member, allowed) = command switch
        {
            MediaCommand.Previous => ("Previous", snapshot.CanPrevious),
            MediaCommand.Next => ("Next", snapshot.CanNext),
            _ when snapshot.CanPlay && snapshot.CanPause => ("PlayPause", true),
            _ when snapshot.Playing => ("Pause", snapshot.CanPause),
            _ => ("Play", snapshot.CanPlay)
        };
        if (!allowed) return false;
        var bus = await ConnectionAsync().ConfigureAwait(false);
        await bus.CallMethodAsync(Call(bus, player, ObjectPath, PlayerInterface, member, null, null)).WaitAsync(token).ConfigureAwait(false);
        return true;
    }

    public IDisposable Subscribe(Action changed)
    {
        bool first;
        lock (subscribers) { subscribers.Add(changed); first = subscribers.Count == 1; }
        if (first) _ = WatchAsync();
        return new Subscription(this, changed);
    }
    private async Task WatchAsync()
    {
        try
        {
            var bus = await ConnectionAsync().ConfigureAwait(false);
            var properties = await bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.DBus.Properties", Member = "PropertiesChanged", Path = ObjectPath, Arg0 = PlayerInterface },
                (_, _) => true, (Notification<bool> n) => { if (n.HasValue) Notify(); }, false, ObserverFlags.None, null).ConfigureAwait(false);
            // Tmds.DBus writes arg0namespace in a casing dbus-daemon rejects, so players are filtered here.
            var owners = await bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged" },
                (m, _) => m.GetBodyReader().ReadString().StartsWith(Prefix, StringComparison.Ordinal),
                (Notification<bool> n) => { if (n.HasValue && n.Value) Notify(); }, false, ObserverFlags.None, null).ConfigureAwait(false);
            lock (subscribers)
            {
                // The last widget may have left while the match rules were being added.
                if (subscribers.Count == 0) { properties.Dispose(); owners.Dispose(); return; }
                watches.Add(properties); watches.Add(owners);
            }
        }
        catch (Exception error) when (error is DBusExceptionBase or System.IO.IOException) { AppDiagnostics.Write("mpris-watch", error); }
    }
    private void Notify()
    {
        Action[] targets;
        lock (subscribers) targets = subscribers.ToArray();
        foreach (var target in targets) target();
    }
    private void Unsubscribe(Action changed)
    {
        lock (subscribers)
        {
            subscribers.Remove(changed);
            if (subscribers.Count > 0) return;
            foreach (var watch in watches) watch.Dispose();
            watches.Clear();
        }
    }
    private sealed class Subscription(MprisMediaSource source, Action callback) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; source.Unsubscribe(callback); } }
    }
}

public sealed class UnavailableMediaSource : IMediaSource
{
    public Task<MediaSnapshot?> ReadAsync(CancellationToken token) => Task.FromResult<MediaSnapshot?>(null);
    public Task<bool> ControlAsync(MediaCommand command, CancellationToken token) => Task.FromResult(false);
    public IDisposable Subscribe(Action changed) => new Empty();
    private sealed class Empty : IDisposable { public void Dispose() { } }
}
