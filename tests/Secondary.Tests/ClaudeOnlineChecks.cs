using System.Net;
using System.Text;
using Secondary.Core;
using Secondary.Shared;

internal static class ClaudeOnlineChecks
{
    private const string Token = "sk-ant-oat01-test-token-value";
    private const string UsageJson = "{\"five_hour\":{\"utilization\":12.5,\"resets_at\":\"2026-10-18T17:00:00.5+00:00\"},\"seven_day\":{\"utilization\":41,\"resets_at\":1792900000},\"seven_day_opus\":null}";

    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Secondary.ClaudeOnline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckSettings(check);
            await CheckSource(check, root);
            await CheckMerge(check, root);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckSettings(Action<bool, string> check)
    {
        check(!new HomeWidgetSettings("u", "AiUsage").Normalize().ClaudeOnline, "Claude online usage is off by default");
        var enabled = System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(System.Text.Json.JsonSerializer.Serialize(new HomeWidgetSettings("u", "AiUsage", ClaudeOnline: true)))!.Normalize();
        check(enabled.ClaudeOnline, "Enabled Claude online usage survives a settings round trip");
        check(!new HomeWidgetSettings("n", "Note", ClaudeOnline: true).Normalize().ClaudeOnline, "Only AI usage widgets keep the Claude online option");
        check(!new HomeWidgetSettings("u", "AiUsage").Normalize().UsagePaused, "AI usage updates automatically by default");
        var paused = System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(System.Text.Json.JsonSerializer.Serialize(new HomeWidgetSettings("u", "AiUsage", UsagePaused: true)))!.Normalize();
        check(paused.UsagePaused && !new HomeWidgetSettings("n", "Note", UsagePaused: true).Normalize().UsagePaused, "A paused AI usage widget stays paused after a settings round trip");
        var codexOnly = System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(System.Text.Json.JsonSerializer.Serialize(new HomeWidgetSettings("u", "AiUsage", UsageProviders: "Codex")))!.Normalize();
        check(new HomeWidgetSettings("u", "AiUsage").Normalize() is { UsageProviders: "Both" } both && both.ShowsClaude() && both.ShowsCodex() &&
            codexOnly is { UsageProviders: "Codex" } && !codexOnly.ShowsClaude() && codexOnly.ShowsCodex(), "AI usage shows both services by default and keeps a single-service choice");
        check(new HomeWidgetSettings("u", "AiUsage", UsageProviders: "Gemini").Normalize().UsageProviders == "Both" &&
            new HomeWidgetSettings("n", "Note", UsageProviders: "Claude").Normalize().UsageProviders == "Both", "Unknown or misplaced service choices fall back to both");
        check(!System.Text.Json.JsonSerializer.Serialize(codexOnly).Contains("Shows", StringComparison.Ordinal), "Service visibility helpers are not written into settings");
    }

    private static string Credentials(long expiresAt) =>
        $"{{\"claudeAiOauth\":{{\"accessToken\":\"{Token}\",\"refreshToken\":\"refresh\",\"expiresAt\":{expiresAt},\"subscriptionType\":\"max\"}}}}";

    private static async Task CheckSource(Action<bool, string> check, string root)
    {
        var now = new DateTimeOffset(2026, 10, 18, 15, 10, 0, TimeSpan.Zero);
        var requests = new List<HttpRequestMessage>();
        var respond = (HttpRequestMessage _) => Json(UsageJson);
        using var client = new HttpClient(new Fake(request => { requests.Add(request); return respond(request); }));
        var path = Path.Combine(root, ".credentials.json");
        var source = new ClaudeOnlineUsage(client, path, () => now);

        var missing = await source.ReadAsync(CancellationToken.None);
        check(missing.Usage is null && missing.Problem!.Contains("找不到") && requests.Count == 0, "Missing CLI credentials report unavailable without a request");

        now += ClaudeOnlineUsage.Interval;
        File.WriteAllText(path, "{\"mcpOAuth\":{\"server\":{\"accessToken\":\"other\"}}}");
        var desktop = await source.ReadAsync(CancellationToken.None);
        check(desktop.Problem!.Contains("桌面版") && requests.Count == 0, "Credentials without a Claude CLI login (desktop only) are never sent");

        now += ClaudeOnlineUsage.Interval;
        File.WriteAllText(path, Credentials(now.AddMinutes(-1).ToUnixTimeMilliseconds()));
        var expired = await source.ReadAsync(CancellationToken.None);
        check(expired.Problem!.Contains("已過期") && requests.Count == 0, "Expired CLI credentials are not sent or refreshed");

        now += ClaudeOnlineUsage.Interval;
        File.WriteAllText(path, Credentials(now.AddHours(2).ToUnixTimeMilliseconds()));
        var fetched = await source.ReadAsync(CancellationToken.None);
        var sent = requests.Single();
        check(sent.RequestUri == ClaudeOnlineUsage.Endpoint && sent.Method == HttpMethod.Get && sent.Headers.Authorization?.Scheme == "Bearer" &&
            sent.Headers.Authorization.Parameter == Token && sent.Headers.GetValues("anthropic-beta").Single() == ClaudeOnlineUsage.BetaHeader,
            "Online usage sends the CLI token only as a bearer header to the fixed Anthropic endpoint");
        check(fetched.Problem is null && fetched.Usage!.FiveHour is { UsedPercent: 12.5 } && fetched.Usage.FiveHour.ResetsAt == new DateTimeOffset(2026, 10, 18, 17, 0, 0, 500, TimeSpan.Zero) &&
            fetched.Usage.Weekly is { UsedPercent: 41 } && fetched.Usage.Weekly.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1792900000) && fetched.Usage.ObservedAt == now,
            "Online usage parses utilization and ISO or epoch reset times");

        now += TimeSpan.FromMinutes(4);
        check(await source.ReadAsync(CancellationToken.None) == fetched && requests.Count == 1, "Online usage is fetched at most once per five minutes");

        async Task<ClaudeOnlineResult> Next(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            respond = response; now += ClaudeOnlineUsage.Interval;
            return await source.ReadAsync(CancellationToken.None);
        }
        var rejected = await Next(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"" + Token + "\"}") });
        check(rejected.Problem!.Contains("拒絕") && !rejected.Problem.Contains(Token), "A rejected token is reported without exposing it");
        check((await Next(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).Problem == "伺服器回應 HTTP 500", "Server errors report their status");
        check((await Next(_ => Json("{\"five_hour\":{\"percent\":3}}"))).Problem!.Contains("格式無法辨識"), "An unrecognised response shape is reported as a possible endpoint change");
        check((await Next(_ => Json("{\"x\":\"" + new string('a', ClaudeOnlineUsage.MaximumResponseBytes) + "\"}"))).Problem!.Contains("過大"), "Oversized online responses are rejected");
        check((await Next(_ => throw new HttpRequestException("offline"))).Problem!.Contains("無法連線"), "Network failures report the endpoint as unreachable");
    }

    private static async Task CheckMerge(Action<bool, string> check, string root)
    {
        var now = DateTimeOffset.UtcNow;
        var paths = new AiUsagePaths(Path.Combine(root, "codex"), Path.Combine(root, "claude"), Path.Combine(root, "statusline.json"));
        var credentials = Path.Combine(paths.ClaudeHome, ".credentials.json");
        Directory.CreateDirectory(paths.ClaudeHome);
        File.WriteAllText(credentials, Credentials(now.AddHours(1).ToUnixTimeMilliseconds()));
        var requests = 0;
        using var client = new HttpClient(new Fake(_ => { requests++; return Json(UsageJson.Replace("2026-10-18T17:00:00.5+00:00", now.AddHours(2).ToString("O"))); }));
        var service = new AiUsageService(paths, () => now, new ClaudeOnlineUsage(client, credentials, () => now));

        var off = await service.ReadAsync(false, CancellationToken.None);
        check(requests == 0 && off.ClaudeOnlineProblem is null && !off.Claude.HasData, "Disabled online usage never contacts the endpoint");
        var on = await service.ReadAsync(true, CancellationToken.None);
        check(requests == 1 && on.ClaudeOnlineProblem is null && on.Claude.FiveHour is { UsedPercent: 12.5 } && on.Claude.Weekly is { UsedPercent: 41 },
            "Enabled online usage supplies Claude percentages");

        var project = Path.Combine(paths.ClaudeHome, "projects", "p");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "s.jsonl"),
            $"{{\"type\":\"assistant\",\"timestamp\":\"{now.AddSeconds(30):O}\",\"apiErrorParams\":{{\"rate_limit_info\":{{\"status\":\"rejected\",\"resetsAt\":{now.AddMinutes(40).ToUnixTimeSeconds()},\"rateLimitType\":\"five_hour\"}}}}}}\n");
        var merged = await service.ReadAsync(true, CancellationToken.None);
        check(merged.Claude.FiveHour is { LimitReached: true } && merged.Claude.Weekly is { UsedPercent: 41 }, "A rejection logged after the online fetch marks the window as reached");

        var requestsBefore = requests;
        var codexOnly = await service.ReadAsync(false, true, true, CancellationToken.None);
        check(requests == requestsBefore && !codexOnly.Claude.HasData && codexOnly.ClaudeOnlineProblem is null, "A hidden Claude section is neither scanned nor fetched online");
        var claudeOnly = service.Read(true, false);
        check(claudeOnly.Claude.FiveHour is { LimitReached: true } && !claudeOnly.Codex.HasData, "A hidden Codex section is not read");

        var noCredentials = new AiUsageService(paths, () => now, new ClaudeOnlineUsage(client, Path.Combine(root, "none.json"), () => now));
        var unavailable = await noCredentials.ReadAsync(true, CancellationToken.None);
        check(unavailable.ClaudeOnlineProblem!.Contains("找不到") && unavailable.Claude.FiveHour is { LimitReached: true }, "Unavailable online usage keeps local Claude data and states the problem");
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
