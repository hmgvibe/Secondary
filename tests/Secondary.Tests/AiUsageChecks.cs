using Secondary.Shared;

internal static class AiUsageChecks
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 10, 0, TimeSpan.Zero);

    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "Secondary.AiUsage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckPaths(check, root);
            CheckCodex(check, root);
            CheckClaude(check, root);
            CheckText(check);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CheckPaths(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "home");
        var defaults = AiUsagePaths.ForEnvironment(_ => null, home, Path.Combine(root, "data"));
        check(defaults.CodexHome == Path.Combine(home, ".codex") && defaults.ClaudeHome == Path.Combine(home, ".claude") &&
            defaults.ClaudeStatusLinePath == Path.Combine(root, "data", "claude-statusline.json"), "AI usage reads the default Codex and Claude homes");
        var custom = Path.Combine(root, "custom");
        var moved = AiUsagePaths.ForEnvironment(name => name == "CODEX_HOME" ? custom : name == "CLAUDE_CONFIG_DIR" ? "relative" : null, home, root);
        check(moved.CodexHome == custom && moved.ClaudeHome == Path.Combine(home, ".claude"), "AI usage honours absolute CODEX_HOME and ignores relative overrides");
    }

    private static string Codex(string timestamp, string limitId, string primary, string secondary) =>
        $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{{\"limit_id\":\"{limitId}\",\"primary\":{primary},\"secondary\":{secondary},\"plan_type\":\"plus\"}}}}}}";

    private static string Window(double used, int minutes, DateTimeOffset resets) =>
        $"{{\"used_percent\":{used},\"window_minutes\":{minutes},\"resets_at\":{resets.ToUnixTimeSeconds()}}}";

    private static void CheckCodex(Action<bool, string> check, string root)
    {
        var paths = new AiUsagePaths(Path.Combine(root, "codex"), Path.Combine(root, "claude-missing"), Path.Combine(root, "missing.json"));
        var source = new AiUsageService(paths, () => Now);
        check(!source.Read().Codex.HasData && !source.Read().Claude.HasData, "Missing Codex and Claude folders report no usage instead of failing");

        var day = Path.Combine(paths.CodexHome, "sessions", "2026", "10", "18");
        Directory.CreateDirectory(day);
        var fiveHourReset = Now.AddHours(3).AddMinutes(12);
        var weeklyReset = Now.AddDays(6).AddHours(4);
        var current = Path.Combine(day, "rollout-current.jsonl");
        File.WriteAllLines(current,
        [
            "{\"timestamp\":\"2026-10-18T15:00:00Z\",\"type\":\"response_item\",\"payload\":{}}",
            Codex("2026-10-18T15:01:00Z", "codex", Window(10, 300, fiveHourReset), Window(2, 10080, weeklyReset)),
            "{ not json \"rate_limits\"",
            Codex("2026-10-18T15:05:27Z", "codex", Window(24, 300, fiveHourReset), Window(6, 10080, weeklyReset)),
            Codex("2026-10-18T15:06:00Z", "premium", "null", "null"),
        ]);
        // A resumed session can be written into an older dated folder; its event time decides.
        var older = Path.Combine(paths.CodexHome, "sessions", "2026", "09", "28");
        Directory.CreateDirectory(older);
        var resumed = Path.Combine(older, "rollout-resumed.jsonl");
        File.WriteAllLines(resumed, [Codex("2026-10-18T14:00:00Z", "codex", Window(90, 300, fiveHourReset), Window(50, 10080, weeklyReset))]);
        File.SetLastWriteTimeUtc(resumed, DateTime.UtcNow.AddMinutes(5));

        var codex = source.Read().Codex;
        check(codex.FiveHour is { UsedPercent: 24, LimitReached: false } && codex.FiveHour.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(fiveHourReset.ToUnixTimeSeconds()),
            "Codex five-hour usage comes from the latest codex rate_limits event");
        check(codex.Weekly is { UsedPercent: 6 } && codex.ObservedAt == DateTimeOffset.Parse("2026-10-18T15:05:27Z"),
            "Codex weekly usage and observation time skip premium events and malformed lines");

        // Window length identifies each limit even if primary and secondary swap.
        File.AppendAllText(current, Codex("2026-10-18T15:08:00Z", "codex", Window(7, 10080, weeklyReset), Window(31, 300, fiveHourReset)) + "\n");
        codex = source.Read().Codex;
        check(codex.FiveHour!.UsedPercent == 31 && codex.Weekly!.UsedPercent == 7, "Codex windows are classified by window_minutes and appended lines are read");

        // A line still being written is not parsed until it is complete.
        var partial = Codex("2026-10-18T15:09:00Z", "codex", Window(40, 300, fiveHourReset), Window(8, 10080, weeklyReset));
        File.AppendAllText(current, partial[..(partial.Length / 2)]);
        check(source.Read().Codex.FiveHour!.UsedPercent == 31, "An unterminated Codex line is ignored until complete");
        File.AppendAllText(current, partial[(partial.Length / 2)..] + "\n");
        check(source.Read().Codex.FiveHour!.UsedPercent == 40, "A completed Codex line is parsed on the next read");

        File.WriteAllText(current, Codex("2026-10-18T15:09:30Z", "codex", Window(1, 300, fiveHourReset), Window(1, 10080, weeklyReset)) + "\n");
        check(source.Read().Codex.FiveHour!.UsedPercent == 1, "A rewritten shorter Codex log is scanned again");
    }

    private static string Rejection(string timestamp, string type, DateTimeOffset resets) =>
        $"{{\"type\":\"assistant\",\"timestamp\":\"{timestamp}\",\"apiError\":\"usage_limit_reached\",\"apiErrorParams\":{{\"rate_limit_info\":{{\"status\":\"rejected\",\"resetsAt\":{resets.ToUnixTimeSeconds()},\"rateLimitType\":\"{type}\"}}}},\"error\":\"rate_limit\"}}";

    private static void CheckClaude(Action<bool, string> check, string root)
    {
        var paths = new AiUsagePaths(Path.Combine(root, "codex-missing"), Path.Combine(root, "claude"), Path.Combine(root, "data", "claude-statusline.json"));
        var source = new AiUsageService(paths, () => Now);
        var project = Path.Combine(paths.ClaudeHome, "projects", "D--Project");
        Directory.CreateDirectory(project);
        var transcript = Path.Combine(project, "session.jsonl");
        File.WriteAllLines(transcript,
        [
            "{\"type\":\"user\",\"timestamp\":\"2026-10-18T10:00:00Z\",\"message\":{\"content\":\"rate_limit_info is just text here\"}}",
            Rejection("2026-10-18T09:00:00Z", "five_hour", Now.AddHours(-1)),
        ]);
        check(!source.Read().Claude.HasData, "Claude without statusLine data or a current rejection has no usage");

        File.AppendAllText(transcript, Rejection("2026-10-18T14:30:00Z", "five_hour", Now.AddMinutes(50)) + "\n");
        var claude = source.Read().Claude;
        check(claude.FiveHour is { LimitReached: true } && claude.FiveHour.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(Now.AddMinutes(50).ToUnixTimeSeconds()) && claude.Weekly is null,
            "A current Claude rejection shows the five-hour limit as reached with its reset time");

        Directory.CreateDirectory(Path.GetDirectoryName(paths.ClaudeStatusLinePath)!);
        File.WriteAllText(paths.ClaudeStatusLinePath,
            $"{{\"session_id\":\"s\",\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":23.5,\"resets_at\":{Now.AddHours(2).ToUnixTimeSeconds()}}},\"seven_day\":{{\"used_percentage\":41.2,\"resets_at\":{Now.AddDays(3).ToUnixTimeSeconds()}}}}}}}");
        File.SetLastWriteTimeUtc(paths.ClaudeStatusLinePath, Now.AddMinutes(-2).UtcDateTime);
        claude = source.Read().Claude;
        check(claude.FiveHour is { UsedPercent: 23.5, LimitReached: false } && claude.Weekly is { UsedPercent: 41.2 } && claude.ObservedAt == Now.AddMinutes(-2),
            "Claude statusLine rate_limits supply percentages newer than an earlier rejection");

        File.WriteAllText(paths.ClaudeStatusLinePath, "{\"rate_limits\":" + new string('[', 80) + new string(']', 80) + "}");
        check(source.Read().Claude.FiveHour is { LimitReached: true }, "Malformed statusLine data falls back to transcript rejections");

        var old = Path.Combine(project, "old.jsonl");
        File.WriteAllLines(old, [Rejection("2026-10-18T14:59:00Z", "seven_day", Now.AddDays(2))]);
        File.SetLastWriteTimeUtc(old, Now.AddDays(-9).UtcDateTime);
        check(source.Read().Claude.Weekly is null, "Claude transcripts older than the weekly window are not scanned");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow);
        check(source.Read().Claude.Weekly is { LimitReached: true }, "A seven-day rejection marks the weekly Claude limit as reached");
    }

    private static void CheckText(Action<bool, string> check)
    {
        check(AiUsageText.Describe(null, Now) is { Value: "—", Detail: "沒有資料", Fraction: 0 }, "Missing usage window reads as no data");
        check(AiUsageText.Describe(new UsageWindow(24.4, Now.AddHours(3).AddMinutes(12)), Now) is { Value: "24%", Detail: "3 小時 12 分後重置" } row && Math.Abs(row.Fraction - .244) < 1e-9,
            "Usage percent and reset countdown are formatted");
        check(AiUsageText.Describe(new UsageWindow(6, Now.AddDays(6).AddHours(4)), Now).Detail == "6 天 4 小時後重置", "Weekly reset countdown uses days");
        check(AiUsageText.Describe(new UsageWindow(100, Now.AddMinutes(9).AddSeconds(30), true), Now) is { Value: "已用完", Detail: "9 分鐘後重置", Fraction: 1 },
            "Reached limit shows exhausted state and its reset time");
        check(AiUsageText.Describe(new UsageWindow(80, Now.AddMinutes(-1)), Now) is { Value: "—", Fraction: 0 } reset && reset.Detail.Contains("已重置"),
            "A passed reset time never shows the stale percentage");
        check(AiUsageText.Describe(new UsageWindow(5, null), Now).Detail == "重置時間未知", "Missing reset time is stated");
        check(AiUsageText.Age(Now.AddSeconds(-20), Now) == "剛剛更新" && AiUsageText.Age(Now.AddMinutes(-12), Now) == "12 分鐘前更新" &&
            AiUsageText.Age(Now.AddHours(-5), Now) == "5 小時前更新" && AiUsageText.Age(Now.AddDays(-2), Now) == "2 天前更新" && AiUsageText.Age(null, Now) == "",
            "Usage age is described in local words");
    }
}
