using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Secondary.Shared;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Connect;

// Reads the main computer's AI usage for a device while one of its widgets set to the main computer is on screen
// ("usage-watch"): at once, then every minute, sending only when something changed. The unofficial Claude online
// source runs here only if the user allowed it on this computer, so the CLI token never leaves it.
internal sealed class UsageService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    internal const string NotAllowed = "主電腦未允許線上用量（在連接助手選單開啟）";
    private readonly Func<bool, bool, bool, CancellationToken, Task<AiUsageSnapshot>> read;
    private readonly string settingsPath;
    private readonly TimeSpan interval;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private CancellationTokenSource? watching;
    private Channel<bool>? wake;
    private bool allowOnline;
    public event Action? Changed;
    public bool Watching { get { lock (gate) return watching is not null; } }
    public string Status => Watching ? "AI 用量：設備正在顯示主電腦的用量" : "AI 用量：未使用";
    public string? SaveError { get; private set; }
    internal string SettingsPath => settingsPath;

    public UsageService(string settingsPath, Func<bool, bool, bool, CancellationToken, Task<AiUsageSnapshot>>? read = null, TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        this.settingsPath = settingsPath;
        this.read = read ?? AiUsageService.Shared.ReadAsync;
        this.interval = interval ?? Interval;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        allowOnline = Load();
    }

    // Off by default; saved in the helper's own folder.
    public bool AllowOnline
    {
        get { lock (gate) return allowOnline; }
        set
        {
            lock (gate) allowOnline = value;
            Save(value);
            Wake();
            Changed?.Invoke();
        }
    }

    public Task Handle(JsonObject message, FrameChannel channel, CancellationToken session)
    {
        if (TypeOf(message) != "usage-watch") return Task.CompletedTask;
        if (RemoteUsageMessages.ReadWatch(message) is { } request) Watch(request, channel, session); else Stop();
        return Task.CompletedTask;
    }

    private void Watch(UsageRequest request, FrameChannel channel, CancellationToken session)
    {
        var owner = CancellationTokenSource.CreateLinkedTokenSource(session);
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        CancellationTokenSource? previous;
        lock (gate) { previous = watching; watching = owner; wake = signal; }
        Cancel(previous);
        _ = Task.Run(() => Report(request, channel, owner, signal));
        Changed?.Invoke();
    }
    public void Stop()
    {
        CancellationTokenSource? owner;
        lock (gate) { owner = watching; watching = null; wake = null; }
        if (owner is null) return;
        Cancel(owner);
        Changed?.Invoke();
    }
    private void Wake() { Channel<bool>? signal; lock (gate) signal = wake; signal?.Writer.TryWrite(true); }
    private static void Cancel(CancellationTokenSource? owner) { try { owner?.Cancel(); } catch (ObjectDisposedException) { } }

    private async Task Report(UsageRequest request, FrameChannel channel, CancellationTokenSource owner, Channel<bool> signal)
    {
        var token = owner.Token;
        string? last = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var online = request.Claude && request.ClaudeOnline && AllowOnline;
                AiUsageSnapshot snapshot;
                var status = "主電腦用量";
                try
                {
                    snapshot = await read(request.Claude, request.Codex, online, token).ConfigureAwait(false);
                    if (request.Claude && request.ClaudeOnline && !online) snapshot = snapshot with { ClaudeOnlineProblem = NotAllowed };
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    AppDiagnostics.Write("connect-usage", error);
                    snapshot = new AiUsageSnapshot(new(null, null, null), new(null, null, null));
                    status = "無法讀取主電腦的用量紀錄";
                }
                // The comparison leaves out the timestamp, so an unchanged reading is not sent again.
                var message = RemoteUsageMessages.State(snapshot, clock(), status);
                var text = message.DeepClone().AsObject() is var copy && copy.Remove("sent") ? copy.ToJsonString() : message.ToJsonString();
                if (text != last) { await channel.SendAsync(message, token).ConfigureAwait(false); last = text; }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(interval);
                try { await signal.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            var ended = false;
            lock (gate) if (watching == owner) { watching = null; wake = null; ended = true; }
            owner.Dispose();
            if (ended) Changed?.Invoke();
        }
    }

    private bool Load()
    {
        try
        {
            var info = new FileInfo(settingsPath);
            if (!info.Exists || info.Length > 4096) return false;
            return JsonNode.Parse(File.ReadAllText(info.FullName)) is JsonObject value && value["allowClaudeOnline"]?.GetValueKind() == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    private void Save(bool value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temp = settingsPath + ".tmp";
            File.WriteAllText(temp, new JsonObject { ["allowClaudeOnline"] = value }.ToJsonString());
            File.Move(temp, settingsPath, true);
            SaveError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveError = "無法儲存線上用量設定：" + error.Message;
            AppDiagnostics.Write("connect-usage", error);
        }
    }
}
