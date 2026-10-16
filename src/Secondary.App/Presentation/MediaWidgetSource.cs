using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Secondary.App.Presentation;

internal sealed record MediaWidgetSnapshot(GlobalSystemMediaTransportControlsSession? Session,
    GlobalSystemMediaTransportControlsSessionMediaProperties? Properties,
    GlobalSystemMediaTransportControlsSessionPlaybackInfo? Playback,
    GlobalSystemMediaTransportControlsSessionTimelineProperties? Timeline, long ArtworkRevision = 0);

internal sealed class MediaWidgetSource
{
    internal static readonly MediaWidgetSource Shared = new();
    private readonly object sync = new();
    private readonly HashSet<Action> subscribers = new();
    private Task<GlobalSystemMediaTransportControlsSessionManager>? managerTask;
    private GlobalSystemMediaTransportControlsSessionManager? subscribedManager;
    private GlobalSystemMediaTransportControlsSession? subscribedSession;
    private Task<MediaWidgetSnapshot>? snapshotTask;
    private DateTimeOffset snapshotAt;
    private MediaWidgetSnapshot? snapshot;
    private long version, requestVersion;
    private long artworkRevision;
    internal IDisposable Subscribe(Action callback)
    {
        lock (sync) subscribers.Add(callback);
        _ = StartAsync();
        return new Subscription(this, callback);
    }
    internal async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync()
    {
        Task<GlobalSystemMediaTransportControlsSessionManager> request;
        lock (sync) request = managerTask ??= RequestManagerAsync();
        try { return await request.ConfigureAwait(false); }
        catch { lock (sync) { if (ReferenceEquals(managerTask, request)) managerTask = null; } throw; }
    }
    private static async Task<GlobalSystemMediaTransportControlsSessionManager> RequestManagerAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
    }
    internal async Task<MediaWidgetSnapshot> ReadAsync()
    {
        Task<MediaWidgetSnapshot> request;
        long generation;
        lock (sync)
        {
            if (snapshot is not null && DateTimeOffset.UtcNow - snapshotAt < TimeSpan.FromSeconds(1)) return snapshot;
            if (snapshotTask is null) { requestVersion = version; snapshotTask = ReadCoreAsync(); }
            request = snapshotTask;
            generation = requestVersion;
        }
        try
        {
            var result = await request.ConfigureAwait(false);
            lock (sync) { if (generation == version) { snapshot = result; snapshotAt = DateTimeOffset.UtcNow; } }
            return result;
        }
        finally { lock (sync) { if (ReferenceEquals(snapshotTask, request)) snapshotTask = null; } }
    }
    private async Task<MediaWidgetSnapshot> ReadCoreAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var manager = await ManagerAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
        var session = manager.GetCurrentSession();
        if (session is null) return new(null, null, null, null);
        long revision;
        lock (sync) revision = artworkRevision;
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline;
        try { timeline = session.GetTimelineProperties(); } catch { timeline = null; }
        return new(session, properties, session.GetPlaybackInfo(), timeline, revision);
    }
    private async Task StartAsync()
    {
        try
        {
            var manager = await ManagerAsync().ConfigureAwait(false);
            lock (sync)
            {
                if (subscribers.Count == 0) return;
                if (subscribedManager is null)
                {
                    subscribedManager = manager;
                    manager.CurrentSessionChanged += OnCurrentSessionChanged;
                    manager.SessionsChanged += OnSessionsChanged;
                }
                AttachSession(manager.GetCurrentSession());
            }
            NotifyChanged();
        }
        catch { NotifyChanged(); }
    }
    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, subscribedSession)) return;
        artworkRevision++;
        if (subscribedSession is not null)
        {
            subscribedSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            subscribedSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            subscribedSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        subscribedSession = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager manager, CurrentSessionChangedEventArgs args)
    {
        lock (sync) { if (subscribers.Count > 0) AttachSession(manager.GetCurrentSession()); }
        NotifyChanged();
    }
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager manager, SessionsChangedEventArgs args) => OnCurrentSessionChanged(manager, null!);
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession session, MediaPropertiesChangedEventArgs args)
    {
        lock (sync) artworkRevision++;
        NotifyChanged();
    }
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession session, PlaybackInfoChangedEventArgs args) => NotifyChanged();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession session, TimelinePropertiesChangedEventArgs args) => NotifyChanged();
    private void NotifyChanged()
    {
        Action[] listeners;
        lock (sync) { version++; snapshot = null; listeners = subscribers.ToArray(); }
        foreach (var listener in listeners) listener();
    }
    private void Unsubscribe(Action callback)
    {
        lock (sync)
        {
            subscribers.Remove(callback);
            if (subscribers.Count != 0) return;
            AttachSession(null);
            if (subscribedManager is not null)
            {
                subscribedManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                subscribedManager.SessionsChanged -= OnSessionsChanged;
                subscribedManager = null;
            }
            snapshot = null;
        }
    }
    private sealed class Subscription(MediaWidgetSource source, Action callback) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; source.Unsubscribe(callback); } }
    }
}
