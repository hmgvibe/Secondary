using System.Text.Json;

namespace Secondary.Core;

public sealed record ShortcutStep(string Kind = "Open", string Target = "", string Arguments = "", string WorkingDirectory = "", int Seconds = 1, string ProcessName = "", string WindowTitle = "");
public sealed record ShortcutEntry(string Id, string Name, string Group = "日常", string Icon = "↗", ShortcutStep[]? Steps = null, string? ApprovedFingerprint = null);
// Pending: imported shortcuts this system cannot run (another system's paths or steps), kept until the user
// moves them to the main computer, edits them or deletes them. They are never run here.
public sealed record ShortcutDocument(int Version = 1, ShortcutEntry[]? Entries = null, string GroupLayout = "Vertical", ShortcutEntry[]? Pending = null);

// What each platform can run. Windows keeps its original rules; Linux runs .sh scripts through
// /bin/sh, passes arguments to executables, and cannot send keys to other windows under Wayland.
public sealed record ShortcutRules(string[] ScriptExtensions, string[] OpenBlocked, string[] ArgumentExtensions,
    string ScriptMessage, string OpenBlockedMessage, string ArgumentsMessage, string? KeysUnsupported)
{
    public static readonly ShortcutRules Windows = new([".ps1", ".bat", ".cmd"], [".ps1", ".bat", ".cmd", ".vbs", ".js", ".wsf", ".hta"], [".exe"],
        "外部腳本只支援本機 .ps1／.bat／.cmd。", "請將受支援的腳本改用「腳本」步驟；不支援其他脚本類型。", "啟動參數只適用於 .exe 程式。", null);
    public static readonly ShortcutRules Linux = new([".sh"], [".sh", ".bash", ".zsh", ".py", ".pl", ".rb"], ["", ".appimage"],
        "外部腳本只支援本機 .sh（以 /bin/sh 執行）。", "請將 .sh 改用「腳本」步驟；不支援其他腳本類型。", "啟動參數只適用於可執行程式或 AppImage。",
        "Wayland 不允許程式向其他視窗送出按鍵；Linux 版沒有「視窗快捷鍵」步驟。") { UnixPaths = true };
    public static ShortcutRules[] All => [Windows, Linux];
    public bool UnixPaths { get; private init; }
    // Windows runs drive paths; Linux runs absolute non-network paths. Each system's paths are invalid on the
    // other, so an import can tell what this device can run. (Drive paths stay valid for the Linux rules on a
    // Windows host only so the shared Linux test suite can run there; Linux builds never run on Windows.)
    public bool IsLocalPath(string? path)
    {
        var drive = path is { Length: > 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        var unix = path is { Length: > 1 } && path[0] == '/' && path[1] != '/';
        return UnixPaths ? unix || drive && OperatingSystem.IsWindows() : drive;
    }
}

public static class ShortcutPolicy
{
    public static string? Validate(ShortcutEntry entry) => Validate(entry, ShortcutRules.Windows);
    public static bool ValidAnywhere(ShortcutEntry entry) => ShortcutRules.All.Any(r => Validate(entry, r) is null);
    public static string? Validate(ShortcutEntry entry, ShortcutRules rules)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 80 || entry.Group.Length > 40 || entry.Icon.Length > 8) return "請填寫名稱（最多 80 字）、分組（最多 40 字）、圖示（最多 8 字）。";
        if (entry.Steps is not { Length: > 0 and <= 20 }) return "每個捷徑需要 1–20 個步驟。";
        foreach (var step in entry.Steps)
        {
            if (step is null) return "無效步驟。";
            if (step.Target.Length > 4096 || step.Arguments.Length > 4096 || step.WorkingDirectory.Length > 4096) return "路徑或參數過長。";
            if (step.Kind == "Wait") { if (step.Seconds is < 1 or > 3600) return "等待時間需為 1–3600 秒。"; continue; }
            if (step.Kind == "Keys")
            {
                if (rules.KeysUnsupported is { } unsupported) return unsupported;
                if (!HotkeyGesture.TryParse(step.Target,out _) || string.IsNullOrWhiteSpace(step.ProcessName) || string.IsNullOrWhiteSpace(step.WindowTitle)) return "按鍵步驟需填 Ctrl／Alt 組合鍵、程序名稱及完整視窗標題。";
                continue;
            }
            if (step.Kind is not ("Open" or "Script")) return "不支援的步驟。";
            var web = Uri.TryCreate(step.Target,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
            if (!web && !rules.IsLocalPath(step.Target)) return rules.UnixPaths ? "目標需為以 / 開頭的本機路徑或 HTTP／HTTPS 網址。" : "目標需為本機磁碟路徑（例如 C:\\）或 HTTP／HTTPS 網址。";
            var extension = Path.GetExtension(step.Target).ToLowerInvariant();
            if (step.Kind == "Script" && (web || !rules.ScriptExtensions.Contains(extension))) return rules.ScriptMessage;
            if (step.Kind == "Open" && rules.OpenBlocked.Contains(extension)) return rules.OpenBlockedMessage;
            if (step.WorkingDirectory.Length > 0 && !rules.IsLocalPath(step.WorkingDirectory)) return "工作目錄需為本機絕對路徑。";
            if (web && (step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0)) return "網站不接受參數或工作目錄。";
            if (step.Kind == "Open" && step.Arguments.Length > 0 && (web || !rules.ArgumentExtensions.Contains(extension))) return rules.ArgumentsMessage;
            if (step.Target.Contains('"') || step.Target.Contains('\r') || step.Target.Contains('\n')) return "目標包含無效字元。";
            if (step.Kind == "Script" && step.Arguments.Length > 0) return "本階段腳本不接受額外參數；請在腳本內設定。";
        }
        return null;
    }
    public static IEnumerable<ShortcutEntry> Filter(IEnumerable<ShortcutEntry> entries, string search, string? group) => entries.Where(e =>
        (group is null || e.Group == group) && (e.Name.Contains(search,StringComparison.OrdinalIgnoreCase) || e.Group.Contains(search,StringComparison.OrdinalIgnoreCase) || e.Steps!.Any(s => s.Target.Contains(search,StringComparison.OrdinalIgnoreCase))));
    public static ShortcutEntry[] Move(IReadOnlyList<ShortcutEntry> entries, string id, string before)
    {
        var list = entries.ToList(); var source = list.FindIndex(e => e.Id == id);
        if (source < 0 || id == before || !list.Any(e => e.Id == before)) return list.ToArray();
        var item = list[source]; list.RemoveAt(source); list.Insert(list.FindIndex(e => e.Id == before),item); return list.ToArray();
    }
    public static ShortcutEntry[] MoveToGroup(IReadOnlyList<ShortcutEntry> entries, string id, string group, string? before = null)
    {
        var list = entries.ToList(); var source = list.FindIndex(e => e.Id == id);
        if (source < 0 || id == before || string.IsNullOrWhiteSpace(group)) return list.ToArray();
        if (before is not null && !list.Any(e => e.Id == before && e.Group == group)) return list.ToArray();
        var item = list[source] with { Group = group }; list.RemoveAt(source);
        var index = before is not null ? list.FindIndex(e => e.Id == before) : list.FindLastIndex(e => e.Group == group) + 1;
        list.Insert(index,item); return list.ToArray();
    }
}

public sealed class ShortcutLibrary(string path, ShortcutRules? rules = null)
{
    private readonly ShortcutRules rules = rules ?? ShortcutRules.Windows;
    public ShortcutRules Rules => rules;
    public const int MaximumBytes = 4_000_000;
    public string? Error { get; private set; }
    public bool ReadOnly { get; private set; }
    public string GroupLayout { get; private set; } = "Vertical";
    public ShortcutEntry[] Pending { get; private set; } = [];
    public ShortcutEntry[] Load()
    {
        try
        {
            Error = null; ReadOnly = false; GroupLayout = "Vertical"; Pending = [];
            if (!File.Exists(path)) return [];
            var doc = BoundedJson.Read<ShortcutDocument>(path, MaximumBytes, "捷徑資料過大。");
            if (doc?.Version != 1 || doc.Entries is null || doc.Entries.Length > 200 || doc.Entries.Any(e => e is null || !ShortcutPolicy.ValidAnywhere(e)) || doc.Entries.Select(e => e.Id).Distinct().Count() != doc.Entries.Length)
                throw new InvalidDataException("捷徑資料格式不正確；保留原檔，未覆寫。");
            // Shortcuts saved by an earlier version that only another system can run become pending
            // instead of making the whole file unreadable; the next save stores them that way.
            var runnable = doc.Entries.Where(e => ShortcutPolicy.Validate(e, rules) is null).ToArray();
            var pending = (doc.Pending ?? []).Concat(doc.Entries.Where(e => ShortcutPolicy.Validate(e, rules) is not null)).ToArray();
            if (pending.Length > 200 || pending.Any(e => e is null || !ShortcutPolicy.ValidAnywhere(e)) || pending.Select(e => e.Id).Concat(runnable.Select(e => e.Id)).Distinct().Count() != pending.Length + runnable.Length)
                throw new InvalidDataException("捷徑資料格式不正確；保留原檔，未覆寫。");
            Pending = pending;
            GroupLayout = doc.GroupLayout == "Horizontal" ? "Horizontal" : "Vertical";
            return runnable;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        { Error = e.Message; ReadOnly = true; return []; }
    }
    public bool Save(ShortcutEntry[] entries) => Save(entries, Pending);
    public bool Save(ShortcutEntry[] entries, ShortcutEntry[] pending)
    {
        if (ReadOnly) return false;
        if (entries.Length > 200 || entries.Any(e => ShortcutPolicy.Validate(e, rules) is not null) || entries.Select(e => e.Id).Distinct().Count() != entries.Length) { Error = "捷徑內容無效或超過 200 筆。"; return false; }
        if (pending.Length > 200 || pending.Any(e => !ShortcutPolicy.ValidAnywhere(e)) || pending.Select(e => e.Id).Concat(entries.Select(e => e.Id)).Distinct().Count() != pending.Length + entries.Length) { Error = "待處理的捷徑內容無效。"; return false; }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = BoundedJson.Serialize(new ShortcutDocument(Entries:entries,GroupLayout:GroupLayout,Pending:pending.Length > 0 ? pending : null), MaximumBytes,
                "捷徑資料超過 4 MB，請縮減步驟或啟動參數；原檔未覆寫。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp,path,true); Error = null; Pending = pending; return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Error = e.Message; return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public string BackupPath => Path.ChangeExtension(Path.GetFullPath(path), null) + ".before-import.json";
    // Replaces everything with an import. The current file is copied aside first so the import can be undone by hand.
    public bool Replace(ShortcutImportResult imported)
    {
        if (ReadOnly) { Error ??= "目前的捷徑資料無法讀取，未匯入。"; return false; }
        try { if (File.Exists(path)) File.Copy(path, BackupPath, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "無法備份目前設定，未匯入：" + e.Message; return false; }
        var previous = GroupLayout; GroupLayout = imported.GroupLayout;
        if (Save(imported.Entries, imported.Pending)) return true;
        GroupLayout = previous; return false;
    }
    public bool SetGroupLayout(ShortcutEntry[] entries, string layout)
    {
        if (layout is not ("Vertical" or "Horizontal")) return false;
        var previous = GroupLayout; GroupLayout = layout;
        if (Save(entries)) return true;
        GroupLayout = previous; return false;
    }
}

// Export and import of the launchpad's whole configuration: this device's shortcuts, their groups and the
// group layout. Approvals never travel: an imported macro is confirmed again before its first run.
public sealed record ShortcutImportResult(ShortcutEntry[] Entries, string GroupLayout, int Skipped, ShortcutEntry[] Pending);
public static class ShortcutTransfer
{
    public static byte[] Export(IEnumerable<ShortcutEntry> entries, string groupLayout, IEnumerable<ShortcutEntry>? pending = null)
    {
        var waiting = (pending ?? []).Select(e => e with { ApprovedFingerprint = null }).ToArray();
        return BoundedJson.Serialize(new ShortcutDocument(Entries: entries.Select(e => e with { ApprovedFingerprint = null }).ToArray(),
            GroupLayout: groupLayout == "Horizontal" ? "Horizontal" : "Vertical", Pending: waiting.Length > 0 ? waiting : null), ShortcutLibrary.MaximumBytes, "捷徑資料超過 4 MB，無法匯出。");
    }

    // Entries this platform cannot run but another system can (its paths or step kinds) become pending;
    // entries no system can run are skipped and counted. A pending entry this platform can run comes back.
    public static ShortcutImportResult Read(string path, ShortcutRules rules)
    {
        ShortcutDocument? doc;
        try { doc = BoundedJson.Read<ShortcutDocument>(path, ShortcutLibrary.MaximumBytes, "檔案超過 4 MB，不是 Secondary 的捷徑設定。"); }
        catch (JsonException) { throw new InvalidDataException("檔案不是 Secondary 的捷徑設定。"); }
        if (doc is not { Version: 1, Entries: not null }) throw new InvalidDataException("檔案不是 Secondary 的捷徑設定，或版本不支援。");
        var kept = new List<ShortcutEntry>(); var pending = new List<ShortcutEntry>(); var ids = new HashSet<string>(); var skipped = 0;
        var all = doc.Entries.Concat(doc.Pending ?? []).ToArray();
        foreach (var raw in all)
        {
            if (raw is null) { skipped++; continue; }
            var entry = raw with { ApprovedFingerprint = null };
            var target = ShortcutPolicy.Validate(entry, rules) is null ? kept : ShortcutPolicy.ValidAnywhere(entry) ? pending : null;
            if (target is null || target.Count >= 200) { skipped++; continue; }
            if (!ids.Add(entry.Id)) { entry = entry with { Id = Guid.NewGuid().ToString("N") }; ids.Add(entry.Id); }
            target.Add(entry);
        }
        if (kept.Count == 0 && pending.Count == 0 && all.Length > 0) throw new InvalidDataException("檔案中沒有可以使用的捷徑；未變更目前設定。");
        return new(kept.ToArray(), doc.GroupLayout == "Horizontal" ? "Horizontal" : "Vertical", skipped, pending.ToArray());
    }
}

public interface IShortcutExecutor { Task Execute(ShortcutStep step, CancellationToken cancellation); }
public sealed class ShortcutRunner(IShortcutExecutor executor)
{
    private int running;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public ShortcutRules Rules { get; init; } = ShortcutRules.Windows;
    public async Task Run(ShortcutEntry entry, Action<int> progress, CancellationToken cancellation)
    {
        if (ShortcutPolicy.Validate(entry, Rules) is { } error) throw new InvalidOperationException(error);
        if (Interlocked.CompareExchange(ref running,1,0) != 0) throw new InvalidOperationException("已有巨集正在執行。");
        try
        {
            for (var i = 0; i < entry.Steps!.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested(); progress(i+1);
                var step = entry.Steps[i];
                if (step.Kind == "Wait") await Task.Delay(TimeSpan.FromSeconds(step.Seconds),cancellation);
                else await executor.Execute(step,cancellation);
            }
        }
        finally { Interlocked.Exchange(ref running,0); }
    }
}
