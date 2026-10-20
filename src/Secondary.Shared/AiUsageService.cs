using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Secondary.Shared;

// One rate-limit window. LimitReached means the service rejected requests and gave
// only a reset time, without a percentage.
internal sealed record UsageWindow(double? UsedPercent, DateTimeOffset? ResetsAt, bool LimitReached = false);

internal sealed record ProviderUsage(UsageWindow? FiveHour, UsageWindow? Weekly, DateTimeOffset? ObservedAt)
{
    public bool HasData => FiveHour is not null || Weekly is not null;
}

// ClaudeOnlineProblem is set only when the opt-in online source is enabled and failed.
internal sealed record AiUsageSnapshot(ProviderUsage Claude, ProviderUsage Codex, string? ClaudeOnlineProblem = null);

internal sealed record AiUsagePaths(string CodexHome, string ClaudeHome, string ClaudeStatusLinePath)
{
    // CODEX_HOME and CLAUDE_CONFIG_DIR move each tool's data directory.
    public static AiUsagePaths Current { get; } = ForEnvironment(Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.Current.DataDirectory);

    public static AiUsagePaths ForEnvironment(Func<string, string?> environment, string home, string dataDirectory)
    {
        string Home(string variable, string fallback)
        {
            var value = environment(variable);
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : Path.Combine(home, fallback);
        }
        return new(Home("CODEX_HOME", ".codex"), Home("CLAUDE_CONFIG_DIR", ".claude"), Path.Combine(dataDirectory, "claude-statusline.json"));
    }
}

// Reads only local files that Codex and Claude Code already write; it never uses the network or credentials.
// Codex: token_count events in ~/.codex/sessions carry rate_limits (used_percent, window_minutes, resets_at).
// Claude Code: the desktop app has no local percentage. A statusLine command (CLI only) can save its
// rate_limits JSON, and transcripts record the reset time when a limit is reached.
// Both formats are undocumented or tool-internal, so every field is optional and malformed lines are skipped.
internal sealed class AiUsageService
{
    internal const int CodexFiles = 6;
    internal const int ClaudeFiles = 24;
    internal const long CodexFirstRead = 2 * 1024 * 1024;
    internal const long ClaudeFirstRead = 32 * 1024 * 1024;
    internal const int MaximumLineBytes = 4 * 1024 * 1024;
    internal const int MaximumStatusLineBytes = 256 * 1024;
    internal static readonly TimeSpan ClaudeTranscriptAge = TimeSpan.FromDays(8);
    internal static readonly AiUsageService Shared = new(AiUsagePaths.Current,
        online: new ClaudeOnlineUsage(new HttpClient { Timeout = TimeSpan.FromSeconds(12) }, Path.Combine(AiUsagePaths.Current.ClaudeHome, ".credentials.json")));

    private readonly AiUsagePaths paths;
    private readonly Func<DateTimeOffset> clock;
    private readonly ClaudeOnlineUsage? online;
    private readonly object sync = new();
    private readonly Dictionary<string, ScanState<CodexRecord>> codexFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScanState<ClaudeLimitRecord>> claudeFiles = new(StringComparer.OrdinalIgnoreCase);

    internal AiUsageService(AiUsagePaths paths, Func<DateTimeOffset>? clock = null, ClaudeOnlineUsage? online = null)
    {
        this.paths = paths;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.online = online;
    }

    internal DateTimeOffset UtcNow => clock();

    internal async Task<AiUsageSnapshot> ReadAsync(bool claudeOnline, CancellationToken token)
    {
        var local = await Task.Run(() => Read(token), token).ConfigureAwait(false);
        if (!claudeOnline) return local;
        if (online is null) return local with { ClaudeOnlineProblem = "此版本未提供線上來源" };
        var result = await online.ReadAsync(token).ConfigureAwait(false);
        if (result.Usage is not { } usage) return local with { ClaudeOnlineProblem = result.Problem };
        // Online percentages replace older local ones, but a rejection logged after the fetch still wins.
        var claude = local.Claude;
        UsageWindow? Pick(UsageWindow? fetched, UsageWindow? known) =>
            known is { LimitReached: true } && claude.ObservedAt > usage.ObservedAt ? known : fetched ?? known;
        return local with { Claude = new(Pick(usage.FiveHour, claude.FiveHour), Pick(usage.Weekly, claude.Weekly),
            claude.ObservedAt > usage.ObservedAt ? claude.ObservedAt : usage.ObservedAt) };
    }

    // Serialized: each file keeps an offset so later reads only parse appended bytes.
    internal AiUsageSnapshot Read(CancellationToken token = default)
    {
        lock (sync) return new(ReadClaude(token), ReadCodex(token));
    }

    private sealed record CodexRecord(DateTimeOffset Timestamp, UsageWindow? FiveHour, UsageWindow? Weekly);
    private sealed record ClaudeLimitRecord(DateTimeOffset Timestamp, string Type, DateTimeOffset ResetsAt);

    private sealed class ScanState<T> where T : class
    {
        public long Offset;
        public DateTime LastWrite;
        public T? Latest;
        public readonly List<T> All = [];
    }

    private ProviderUsage ReadCodex(CancellationToken token)
    {
        var files = NewestFiles(Path.Combine(paths.CodexHome, "sessions"), "*.jsonl", CodexFiles, null);
        CodexRecord? latest = null;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var state = Scan(codexFiles, file, CodexFirstRead, "\"rate_limits\"", ParseCodex, keepAll: false);
            if (state?.Latest is { } record && (latest is null || record.Timestamp > latest.Timestamp)) latest = record;
        }
        Forget(codexFiles, files);
        return latest is null ? new(null, null, null) : new(latest.FiveHour, latest.Weekly, latest.Timestamp);
    }

    private static CodexRecord? ParseCodex(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;
        // Other limit ids (for example "premium") carry no windows for this account.
        if (limits.TryGetProperty("limit_id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex") return null;
        UsageWindow? fiveHour = null, weekly = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            var minutes = Number(window, "window_minutes");
            var used = Number(window, "used_percent");
            var resets = Number(window, "resets_at");
            if (minutes is null || used is null) continue;
            var parsed = new UsageWindow(Math.Clamp(used.Value, 0, 100), Epoch(resets));
            // The window length identifies each limit; primary and secondary are positions only.
            if (minutes is >= 240 and <= 360) fiveHour = parsed;
            else if (minutes is >= 9000 and <= 11000) weekly = parsed;
        }
        if (fiveHour is null && weekly is null) return null;
        return new(Timestamp(root) ?? DateTimeOffset.MinValue, fiveHour, weekly);
    }

    private ProviderUsage ReadClaude(CancellationToken token)
    {
        var now = clock();
        UsageWindow? fiveHour = null, weekly = null;
        DateTimeOffset? observed = null;
        if (ReadStatusLine() is { } status) (fiveHour, weekly, observed) = (status.FiveHour, status.Weekly, status.Observed);

        var files = NewestFiles(Path.Combine(paths.ClaudeHome, "projects"), "*.jsonl", ClaudeFiles, now - ClaudeTranscriptAge);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var state = Scan(claudeFiles, file, ClaudeFirstRead, "\"rate_limit_info\"", ParseClaudeLimit, keepAll: true);
            if (state is null) continue;
            foreach (var record in state.All)
            {
                if (record.ResetsAt <= now) continue;
                // A rejection newer than the statusLine snapshot means the window is now exhausted.
                var reached = new UsageWindow(100, record.ResetsAt, true);
                if (record.Type == "five_hour" && (fiveHour is null || observed is null || record.Timestamp > observed)) fiveHour = Later(fiveHour, reached);
                else if (record.Type.StartsWith("seven_day", StringComparison.Ordinal) && (weekly is null || observed is null || record.Timestamp > observed)) weekly = Later(weekly, reached);
                if (observed is null || record.Timestamp > observed) observed = record.Timestamp;
            }
        }
        Forget(claudeFiles, files);
        return new(fiveHour, weekly, observed);
    }

    private static UsageWindow Later(UsageWindow? current, UsageWindow reached) =>
        current is { LimitReached: true, ResetsAt: { } known } && known > reached.ResetsAt ? current : reached;

    private (UsageWindow? FiveHour, UsageWindow? Weekly, DateTimeOffset Observed)? ReadStatusLine()
    {
        try
        {
            var info = new FileInfo(paths.ClaudeStatusLinePath);
            if (!info.Exists || info.Length is 0 or > MaximumStatusLineBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(info.FullName), new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;
            UsageWindow? Window(string name)
            {
                if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return null;
                var used = Number(window, "used_percentage");
                return used is null ? null : new UsageWindow(Math.Clamp(used.Value, 0, 100), Epoch(Number(window, "resets_at")));
            }
            var fiveHour = Window("five_hour");
            var weekly = Window("seven_day");
            return fiveHour is null && weekly is null ? null : (fiveHour, weekly, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static ClaudeLimitRecord? ParseClaudeLimit(JsonElement root)
    {
        if (!root.TryGetProperty("apiErrorParams", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        if (!info.TryGetProperty("status", out var status) || status.GetString() != "rejected") return null;
        if (!info.TryGetProperty("rateLimitType", out var type) || type.ValueKind != JsonValueKind.String) return null;
        if (Epoch(Number(info, "resetsAt")) is not { } resets || Timestamp(root) is not { } timestamp) return null;
        return new(timestamp, type.GetString()!, resets);
    }

    private static List<FileInfo> NewestFiles(string directory, string pattern, int count, DateTimeOffset? since)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 6 };
            return new DirectoryInfo(directory).EnumerateFiles(pattern, options)
                .Where(f => since is null || f.LastWriteTimeUtc > since.Value.UtcDateTime)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(count).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void Forget<T>(Dictionary<string, ScanState<T>> states, List<FileInfo> files) where T : class
    {
        foreach (var stale in states.Keys.Except(files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase).ToArray()) states.Remove(stale);
    }

    // Reads complete lines appended since the previous scan. The first scan of a file starts
    // near its end; a shorter file was rewritten and is scanned again.
    private static ScanState<T>? Scan<T>(Dictionary<string, ScanState<T>> states, FileInfo file, long firstRead, string marker,
        Func<JsonElement, T?> parse, bool keepAll) where T : class
    {
        try
        {
            if (!states.TryGetValue(file.FullName, out var state) || file.Length < state.Offset)
                states[file.FullName] = state = new ScanState<T> { Offset = Math.Max(0, file.Length - firstRead) };
            if (state.Offset == file.Length && state.LastWrite == file.LastWriteTimeUtc) return state;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(state.Offset, SeekOrigin.Begin);
            var skipPartial = state.Offset > 0 && state.LastWrite == default;
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream();
            var markerBytes = Encoding.UTF8.GetBytes(marker);
            var consumed = state.Offset;
            var oversized = false;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    Append(line, buffer, start, i - start, ref oversized);
                    if (!skipPartial && !oversized) Handle(line, markerBytes, parse, state, keepAll);
                    consumed = stream.Position - read + i + 1;
                    skipPartial = false; oversized = false; line.SetLength(0); start = i + 1;
                }
                Append(line, buffer, start, read - start, ref oversized);
            }
            // An unterminated final line is still being written; read it again next time.
            state.Offset = consumed;
            state.LastWrite = file.LastWriteTimeUtc;
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Append(MemoryStream line, byte[] buffer, int start, int count, ref bool oversized)
    {
        if (oversized || count <= 0) return;
        if (line.Length + count > MaximumLineBytes) { oversized = true; line.SetLength(0); return; }
        line.Write(buffer, start, count);
    }

    private static void Handle<T>(MemoryStream line, byte[] marker, Func<JsonElement, T?> parse, ScanState<T> state, bool keepAll) where T : class
    {
        var bytes = new ReadOnlySpan<byte>(line.GetBuffer(), 0, (int)line.Length);
        if (bytes.IndexOf(marker) < 0) return;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (parse(document.RootElement) is not { } record) return;
            state.Latest = record;
            if (keepAll) { state.All.Add(record); if (state.All.Count > 32) state.All.RemoveAt(0); }
        }
        catch (JsonException) { }
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static DateTimeOffset? Epoch(double? seconds) =>
        seconds is > 0 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value) : null;

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}

// Display text shared by the widget shells, kept here so it can be tested without UI.
internal static class AiUsageText
{
    internal sealed record Row(string Value, string Detail, double Fraction);

    internal static Row Describe(UsageWindow? window, DateTimeOffset now)
    {
        if (window is null) return new("—", "沒有資料", 0);
        if (window.ResetsAt is { } resets && resets <= now) return new("—", "已重置，等待下次使用後更新", 0);
        var reset = window.ResetsAt is { } at ? Remaining(at - now) + "後重置" : "重置時間未知";
        if (window.LimitReached) return new("已用完", reset, 1);
        var used = window.UsedPercent ?? 0;
        return new(Math.Round(used).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%", reset, Math.Clamp(used / 100, 0, 1));
    }

    internal static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "1 分鐘內";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小時";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小時 {span.Minutes} 分";
        return $"{(int)span.TotalMinutes} 分鐘";
    }

    internal static string Age(DateTimeOffset? observed, DateTimeOffset now)
    {
        if (observed is null) return "";
        var age = now - observed.Value;
        if (age < TimeSpan.FromMinutes(1)) return "剛剛更新";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} 分鐘前更新";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} 小時前更新";
        return $"{(int)age.TotalDays} 天前更新";
    }
}
