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
        "外部脚本只支持本机 .ps1／.bat／.cmd。", "请将受支持的脚本改用「脚本」步骤；不支持其他脚本类型。", "启动参数只适用于 .exe 程序。", null);
    public static readonly ShortcutRules Linux = new([".sh"], [".sh", ".bash", ".zsh", ".py", ".pl", ".rb"], ["", ".appimage"],
        "外部脚本只支持本机 .sh（以 /bin/sh 执行）。", "请将 .sh 改用「脚本」步骤；不支持其他脚本类型。", "启动参数只适用于可执行程序或 AppImage。",
        "Wayland 不允许程序向其他窗口送出按键；Linux 版没有「窗口快捷键」步骤。") { UnixPaths = true };
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
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 80 || entry.Group.Length > 40 || entry.Icon.Length > 8) return "请填写名称（最多 80 字）、分组（最多 40 字）、图标（最多 8 字）。";
        if (entry.Steps is not { Length: > 0 and <= 20 }) return "每个快捷方式需要 1–20 个步骤。";
        foreach (var step in entry.Steps)
        {
            if (step is null) return "无效步骤。";
            if (step.Target.Length > 4096 || step.Arguments.Length > 4096 || step.WorkingDirectory.Length > 4096) return "路径或参数过长。";
            if (step.Kind == "Wait") { if (step.Seconds is < 1 or > 3600) return "等待时间需为 1–3600 秒。"; continue; }
            if (step.Kind == "Keys")
            {
                if (rules.KeysUnsupported is { } unsupported) return unsupported;
                if (!HotkeyGesture.TryParse(step.Target,out _) || string.IsNullOrWhiteSpace(step.ProcessName) || string.IsNullOrWhiteSpace(step.WindowTitle)) return "按键步骤需填 Ctrl／Alt 组合键、程序名称及完整窗口标题。";
                continue;
            }
            if (step.Kind is not ("Open" or "Script")) return "不支持的步骤。";
            var web = Uri.TryCreate(step.Target,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
            if (!web && !rules.IsLocalPath(step.Target)) return rules.UnixPaths ? "目标需为以 / 开头的本机路径或 HTTP／HTTPS 网址。" : "目标需为本机磁碟路径（例如 C:\\）或 HTTP／HTTPS 网址。";
            var extension = Path.GetExtension(step.Target).ToLowerInvariant();
            if (step.Kind == "Script" && (web || !rules.ScriptExtensions.Contains(extension))) return rules.ScriptMessage;
            if (step.Kind == "Open" && rules.OpenBlocked.Contains(extension)) return rules.OpenBlockedMessage;
            if (step.WorkingDirectory.Length > 0 && !rules.IsLocalPath(step.WorkingDirectory)) return "工作目录需为本机绝对路径。";
            if (web && (step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0)) return "网站不接受参数或工作目录。";
            if (step.Kind == "Open" && step.Arguments.Length > 0 && (web || !rules.ArgumentExtensions.Contains(extension))) return rules.ArgumentsMessage;
            if (step.Target.Contains('"') || step.Target.Contains('\r') || step.Target.Contains('\n')) return "目标包含无效字元。";
            if (step.Kind == "Script" && step.Arguments.Length > 0) return "本阶段脚本不接受额外参数；请在脚本内设置。";
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
            var doc = BoundedJson.Read<ShortcutDocument>(path, MaximumBytes, "快捷方式数据过大。");
            if (doc?.Version != 1 || doc.Entries is null || doc.Entries.Length > 200 || doc.Entries.Any(e => e is null || !ShortcutPolicy.ValidAnywhere(e)) || doc.Entries.Select(e => e.Id).Distinct().Count() != doc.Entries.Length)
                throw new InvalidDataException("快捷方式数据格式不正确；保留原档，未覆写。");
            // Shortcuts saved by an earlier version that only another system can run become pending
            // instead of making the whole file unreadable; the next save stores them that way.
            var runnable = doc.Entries.Where(e => ShortcutPolicy.Validate(e, rules) is null).ToArray();
            var pending = (doc.Pending ?? []).Concat(doc.Entries.Where(e => ShortcutPolicy.Validate(e, rules) is not null)).ToArray();
            if (pending.Length > 200 || pending.Any(e => e is null || !ShortcutPolicy.ValidAnywhere(e)) || pending.Select(e => e.Id).Concat(runnable.Select(e => e.Id)).Distinct().Count() != pending.Length + runnable.Length)
                throw new InvalidDataException("快捷方式数据格式不正确；保留原档，未覆写。");
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
        if (entries.Length > 200 || entries.Any(e => ShortcutPolicy.Validate(e, rules) is not null) || entries.Select(e => e.Id).Distinct().Count() != entries.Length) { Error = "快捷方式内容无效或超过 200 笔。"; return false; }
        if (pending.Length > 200 || pending.Any(e => !ShortcutPolicy.ValidAnywhere(e)) || pending.Select(e => e.Id).Concat(entries.Select(e => e.Id)).Distinct().Count() != pending.Length + entries.Length) { Error = "待处理的快捷方式内容无效。"; return false; }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = BoundedJson.Serialize(new ShortcutDocument(Entries:entries,GroupLayout:GroupLayout,Pending:pending.Length > 0 ? pending : null), MaximumBytes,
                "快捷方式数据超过 4 MB，请缩减步骤或启动参数；原档未覆写。");
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
        if (ReadOnly) { Error ??= "目前的快捷方式数据无法读取，未导入。"; return false; }
        try { if (File.Exists(path)) File.Copy(path, BackupPath, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error = "无法备份目前设置，未导入：" + e.Message; return false; }
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
            GroupLayout: groupLayout == "Horizontal" ? "Horizontal" : "Vertical", Pending: waiting.Length > 0 ? waiting : null), ShortcutLibrary.MaximumBytes, "快捷方式数据超过 4 MB，无法导出。");
    }

    // Entries this platform cannot run but another system can (its paths or step kinds) become pending;
    // entries no system can run are skipped and counted. A pending entry this platform can run comes back.
    public static ShortcutImportResult Read(string path, ShortcutRules rules)
    {
        ShortcutDocument? doc;
        try { doc = BoundedJson.Read<ShortcutDocument>(path, ShortcutLibrary.MaximumBytes, "文件超过 4 MB，不是 Secondary 的快捷方式设置。"); }
        catch (JsonException) { throw new InvalidDataException("文件不是 Secondary 的快捷方式设置。"); }
        if (doc is not { Version: 1, Entries: not null }) throw new InvalidDataException("文件不是 Secondary 的快捷方式设置，或版本不支持。");
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
        if (kept.Count == 0 && pending.Count == 0 && all.Length > 0) throw new InvalidDataException("文件中没有可以使用的快捷方式；未变更目前设置。");
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
        if (Interlocked.CompareExchange(ref running,1,0) != 0) throw new InvalidOperationException("已有巨集正在执行。");
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
