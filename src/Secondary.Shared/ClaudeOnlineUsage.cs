using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Secondary.Shared;

internal sealed record ClaudeOnlineResult(ProviderUsage? Usage, string? Problem);

// Opt-in and unofficial. Reads the Claude Code CLI login from ~/.claude/.credentials.json and asks
// the undocumented https://api.anthropic.com/api/oauth/usage endpoint for the subscription windows.
// The token is sent only to that fixed address; it is never stored, logged, shown or refreshed here
// (refreshing would race Claude Code's own token rotation). The desktop app keeps its login
// elsewhere, so desktop-only users get a clear "unavailable" problem instead.
internal sealed class ClaudeOnlineUsage
{
    internal static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    internal const string BetaHeader = "oauth-2025-04-20";
    internal const int MaximumCredentialBytes = 64 * 1024;
    internal const int MaximumResponseBytes = 64 * 1024;
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly HttpClient client;
    private readonly string credentialsPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClaudeOnlineResult? last;
    private DateTimeOffset lastAttempt;

    internal ClaudeOnlineUsage(HttpClient client, string credentialsPath, Func<DateTimeOffset>? clock = null)
    {
        this.client = client;
        this.credentialsPath = credentialsPath;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<ClaudeOnlineResult> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = clock();
            if (last is not null && now - lastAttempt < Interval) return last;
            lastAttempt = now;
            return last = await FetchAsync(now, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<ClaudeOnlineResult> FetchAsync(DateTimeOffset now, CancellationToken token)
    {
        var (accessToken, problem) = ReadCredentials(now);
        if (accessToken is null) return new(null, problem);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("anthropic-beta", BetaHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(null, (int)response.StatusCode is 401 or 403 ? "伺服器拒絕 CLI 登入憑證，請重新登入 Claude Code CLI" : $"伺服器回應 HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) return new(null, "伺服器回應過大");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) return new(null, "伺服器回應過大");
                buffer.Write(chunk, 0, read);
            }
            var usage = Parse(buffer.ToArray(), now);
            return usage is null ? new(null, "回應格式無法辨識，端點可能已變更") : new(usage, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, "連線逾時"); }
        catch (HttpRequestException) { return new(null, "無法連線到 api.anthropic.com"); }
    }

    private (string? Token, string? Problem) ReadCredentials(DateTimeOffset now)
    {
        const string missing = "找不到 Claude Code CLI 登入憑證（桌面版登入不適用）";
        try
        {
            var info = new FileInfo(credentialsPath);
            if (!info.Exists) return (null, missing);
            if (info.Length is 0 or > MaximumCredentialBytes) return (null, "CLI 登入憑證檔案無法讀取");
            using var document = JsonDocument.Parse(File.ReadAllBytes(info.FullName), new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object ||
                !oauth.TryGetProperty("accessToken", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()))
                return (null, missing);
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var milliseconds) &&
                milliseconds > 0 && DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(milliseconds, 253402300799000)) <= now)
                return (null, "CLI 登入憑證已過期；使用一次 Claude Code CLI 後會自動更新");
            return (access.GetString(), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return (null, "CLI 登入憑證檔案無法讀取"); }
    }

    // Expected shape: {"five_hour":{"utilization":12.0,"resets_at":"2026-10-20T15:00:00+00:00"},"seven_day":{...}}.
    internal static ProviderUsage? Parse(byte[] json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var fiveHour = Window(document.RootElement, "five_hour");
            var weekly = Window(document.RootElement, "seven_day");
            return fiveHour is null && weekly is null ? null : new(fiveHour, weekly, now);
        }
        catch (JsonException) { return null; }
    }

    private static UsageWindow? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number ||
            !utilization.TryGetDouble(out var used) || !double.IsFinite(used)) return null;
        DateTimeOffset? resets = null;
        if (window.TryGetProperty("resets_at", out var value))
        {
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) resets = parsed;
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300799) resets = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return new(Math.Clamp(used, 0, 100), resets);
    }
}
