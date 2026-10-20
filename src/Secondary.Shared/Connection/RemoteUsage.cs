using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// What an AI usage widget set to the main computer asks for, combined across widgets.
internal readonly record struct UsageRequest(bool Claude, bool Codex, bool ClaudeOnline)
{
    public UsageRequest Or(UsageRequest other) => new(Claude || other.Claude, Codex || other.Codex, ClaudeOnline || other.ClaudeOnline);
}

// "usage-watch" (device → helper) starts or stops the helper's reading; "usage-state" (helper → device) carries
// the main computer's usage. Unknown to helpers before this stage, which simply never answer.
internal static class RemoteUsageMessages
{
    private const long MaxEpochSeconds = 253402300799;

    public static JsonObject Watch(UsageRequest? request) => request is { } r
        ? Message("usage-watch", ("on", true), ("claude", r.Claude), ("codex", r.Codex), ("claude-online", r.ClaudeOnline))
        : Message("usage-watch", ("on", false));

    public static UsageRequest? ReadWatch(JsonObject message) =>
        message["on"]?.GetValueKind() == JsonValueKind.True
            ? new UsageRequest(Flag(message, "claude"), Flag(message, "codex"), Flag(message, "claude-online")) : null;

    public static JsonObject State(AiUsageSnapshot snapshot, DateTimeOffset sent, string status) =>
        Message("usage-state", ("sent", sent.ToUnixTimeMilliseconds()), ("status", status),
            ("claude", Provider(snapshot.Claude)), ("codex", Provider(snapshot.Codex)), ("claude-online-problem", snapshot.ClaudeOnlineProblem));

    public static (AiUsageSnapshot Snapshot, DateTimeOffset Sent, string Status)? ReadState(JsonObject message)
    {
        var status = Text(message, "status");
        var problem = message["claude-online-problem"] is null ? null : Text(message, "claude-online-problem");
        if (status is null || status.Length > 300 || problem is { Length: > 300 } || message["claude-online-problem"] is not null && problem is null) return null;
        if (Number(message, "sent") is not { } sent || sent <= 0 || sent > MaxEpochSeconds * 1000) return null;
        if (ReadProvider(message["claude"]) is not { } claude || ReadProvider(message["codex"]) is not { } codex) return null;
        return (new AiUsageSnapshot(claude, codex, problem), DateTimeOffset.FromUnixTimeMilliseconds(sent), status);
    }

    private static bool Flag(JsonObject message, string name) => message[name]?.GetValueKind() == JsonValueKind.True;

    private static JsonObject Provider(ProviderUsage usage) => new()
    {
        ["five-hour"] = Window(usage.FiveHour), ["weekly"] = Window(usage.Weekly),
        ["observed"] = usage.ObservedAt is { } observed ? observed.ToUnixTimeSeconds() : null
    };
    private static JsonObject? Window(UsageWindow? window) => window is null ? null : new JsonObject
    {
        ["used"] = window.UsedPercent, ["resets"] = window.ResetsAt is { } resets ? resets.ToUnixTimeSeconds() : null, ["reached"] = window.LimitReached
    };

    // Every field is checked so a faulty or hostile helper cannot put out-of-range values on screen.
    private static ProviderUsage? ReadProvider(JsonNode? node)
    {
        if (node is not JsonObject provider) return null;
        if (!ReadWindow(provider["five-hour"], out var fiveHour) || !ReadWindow(provider["weekly"], out var weekly)) return null;
        if (!Epoch(provider["observed"], out var observed)) return null;
        return new ProviderUsage(fiveHour, weekly, observed);
    }
    private static bool ReadWindow(JsonNode? node, out UsageWindow? window)
    {
        window = null;
        if (node is null) return true;
        if (node is not JsonObject value) return false;
        double? used = null;
        if (value["used"] is { } usedNode)
        {
            if (usedNode.GetValueKind() != JsonValueKind.Number || !usedNode.AsValue().TryGetValue<double>(out var number) || !double.IsFinite(number) || number is < 0 or > 100) return false;
            used = number;
        }
        if (!Epoch(value["resets"], out var resets)) return false;
        var reached = value["reached"]?.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False or null => false, _ => (bool?)null };
        if (reached is null) return false;
        window = new UsageWindow(used, resets, reached.Value);
        return true;
    }
    private static bool Epoch(JsonNode? node, out DateTimeOffset? value)
    {
        value = null;
        if (node is null) return true;
        if (node.GetValueKind() != JsonValueKind.Number || !node.AsValue().TryGetValue<long>(out var seconds) || seconds is <= 0 or > MaxEpochSeconds) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}

// The device side of main-computer AI usage: widgets watch with what they show, the client asks the helper for
// the union and stops it when the last widget stops. Times are shown on the main computer's clock.
internal sealed class RemoteUsageClient : IDisposable
{
    internal static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(10);
    private readonly ConnectionController controller;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan answerTimeout;
    private readonly object gate = new();
    private readonly System.Collections.Generic.List<Watcher> watchers = [];
    private UsageRequest? sentRequest;
    private bool connected;
    private int generation;
    private TimeSpan offset;
    public event Action? Changed;
    public AiUsageSnapshot? Snapshot { get; private set; }
    public string Status { get; private set; } = "主電腦未連線";
    public bool Connected => controller.State == LinkState.Connected;
    // Connection mode counts as set up once this device has paired with a main computer.
    public bool Configured => controller.Helpers.Any(h => h.Paired);
    // The main computer's clock, from the time it stamped on its last report.
    public DateTimeOffset MainNow => clock() + offset;

    private static RemoteUsageClient? current;
    public static event Action? CurrentChanged;
    public static RemoteUsageClient? Current
    {
        get => current;
        set { current = value; CurrentChanged?.Invoke(); }
    }

    public RemoteUsageClient(ConnectionController controller, Func<DateTimeOffset>? clock = null, TimeSpan? answerTimeout = null)
    {
        this.controller = controller;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.answerTimeout = answerTimeout ?? DefaultAnswerTimeout;
        controller.MessageReceived += OnMessage;
        controller.Changed += OnLinkChanged;
        OnLinkChanged();
    }

    public IDisposable Watch(UsageRequest request, Action changed)
    {
        var watcher = new Watcher(this, request, changed);
        lock (gate) { watchers.Add(watcher); Changed += changed; }
        Update();
        return watcher;
    }
    private void Unwatch(Watcher watcher)
    {
        lock (gate) { if (!watchers.Remove(watcher)) return; Changed -= watcher.Changed; }
        Update();
    }
    private sealed class Watcher(RemoteUsageClient client, UsageRequest request, Action changed) : IDisposable
    {
        private int disposed;
        public UsageRequest Request => request;
        public Action Changed => changed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) client.Unwatch(this); }
    }

    // Sends the combined request when it changes; a new request also restarts the wait for an answer.
    private void Update(bool force = false)
    {
        UsageRequest? request;
        lock (gate)
        {
            request = watchers.Count == 0 ? null : watchers.Select(w => w.Request).Aggregate((a, b) => a.Or(b));
            if (!Connected) { sentRequest = null; return; }
            // After a reconnect the helper has forgotten the request, so it is sent again; nothing to stop is never sent.
            if (request == sentRequest && !(force && request is not null)) return;
            sentRequest = request;
        }
        _ = Send(RemoteUsageMessages.Watch(request));
        if (request is not null) _ = AwaitAnswer(Interlocked.Increment(ref generation));
    }

    // A helper from before this stage ignores the request; say so instead of waiting forever.
    private async Task AwaitAnswer(int expected)
    {
        try { await Task.Delay(answerTimeout).ConfigureAwait(false); } catch (ObjectDisposedException) { return; }
        bool stale;
        lock (gate) stale = generation == expected && Snapshot is null && Connected && sentRequest is not null;
        if (!stale) return;
        Status = "主電腦的連接助手版本較舊，請更新後再試";
        Changed?.Invoke();
    }

    private void OnMessage(JsonObject message)
    {
        if (TypeOf(message) != "usage-state" || RemoteUsageMessages.ReadState(message) is not { } state) return;
        lock (gate) { offset = state.Sent - clock(); Snapshot = state.Snapshot; Status = state.Status; Interlocked.Increment(ref generation); }
        Changed?.Invoke();
    }
    private void OnLinkChanged()
    {
        var now = Connected;
        bool came, lost;
        lock (gate) { came = now && !connected; lost = !now && connected; connected = now; }
        if (came) { Status = "等待主電腦回報用量"; Update(force: true); Changed?.Invoke(); }
        if (lost || (!now && Snapshot is not null))
        {
            lock (gate) { Snapshot = null; sentRequest = null; }
            Status = "主電腦未連線";
            Changed?.Invoke();
        }
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
