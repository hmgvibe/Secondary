using System.Text.Json;

namespace Secondary.Core;

public sealed record ShortcutStep(string Kind = "Open", string Target = "", string Arguments = "", string WorkingDirectory = "", int Seconds = 1, string ProcessName = "", string WindowTitle = "");
public sealed record ShortcutEntry(string Id, string Name, string Group = "日常", string Icon = "↗", ShortcutStep[]? Steps = null, string? ApprovedFingerprint = null);
public sealed record ShortcutDocument(int Version = 1, ShortcutEntry[]? Entries = null, string GroupLayout = "Vertical");

public static class ShortcutPolicy
{
    public static string? Validate(ShortcutEntry entry)
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
                if (!HotkeyGesture.TryParse(step.Target,out _) || string.IsNullOrWhiteSpace(step.ProcessName) || string.IsNullOrWhiteSpace(step.WindowTitle)) return "按鍵步驟需填 Ctrl／Alt 組合鍵、程序名稱及完整視窗標題。";
                continue;
            }
            if (step.Kind is not ("Open" or "Script")) return "不支援的步驟。";
            var web = Uri.TryCreate(step.Target,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
            if (!web && !HomeSettings.IsLocalPath(step.Target)) return "目標需為本機絕對路徑或 HTTP／HTTPS 網址。";
            var extension = Path.GetExtension(step.Target).ToLowerInvariant();
            if (step.Kind == "Script" && (web || extension is not (".ps1" or ".bat" or ".cmd"))) return "外部腳本只支援本機 .ps1／.bat／.cmd。";
            if (step.Kind == "Open" && extension is ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".wsf" or ".hta") return "請將受支援的腳本改用「腳本」步驟；不支援其他脚本類型。";
            if (step.WorkingDirectory.Length > 0 && !HomeSettings.IsLocalPath(step.WorkingDirectory)) return "工作目錄需為本機絕對路徑。";
            if (web && (step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0)) return "網站不接受參數或工作目錄。";
            if (step.Kind == "Open" && step.Arguments.Length > 0 && extension != ".exe") return "啟動參數只適用於 .exe 程式。";
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

public sealed class ShortcutLibrary(string path)
{
    public const int MaximumBytes = 4_000_000;
    public string? Error { get; private set; }
    public bool ReadOnly { get; private set; }
    public string GroupLayout { get; private set; } = "Vertical";
    public ShortcutEntry[] Load()
    {
        try
        {
            Error = null; ReadOnly = false; GroupLayout = "Vertical";
            if (!File.Exists(path)) return [];
            var doc = BoundedJson.Read<ShortcutDocument>(path, MaximumBytes, "捷徑資料過大。");
            if (doc?.Version != 1 || doc.Entries is null || doc.Entries.Length > 200 || doc.Entries.Any(e => e is null || ShortcutPolicy.Validate(e) is not null) || doc.Entries.Select(e => e.Id).Distinct().Count() != doc.Entries.Length)
                throw new InvalidDataException("捷徑資料格式不正確；保留原檔，未覆寫。");
            GroupLayout = doc.GroupLayout == "Horizontal" ? "Horizontal" : "Vertical";
            return doc.Entries;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        { Error = e.Message; ReadOnly = true; return []; }
    }
    public bool Save(ShortcutEntry[] entries)
    {
        if (ReadOnly) return false;
        if (entries.Length > 200 || entries.Any(e => ShortcutPolicy.Validate(e) is not null) || entries.Select(e => e.Id).Distinct().Count() != entries.Length) { Error = "捷徑內容無效或超過 200 筆。"; return false; }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = BoundedJson.Serialize(new ShortcutDocument(Entries:entries,GroupLayout:GroupLayout), MaximumBytes,
                "捷徑資料超過 4 MB，請縮減步驟或啟動參數；原檔未覆寫。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp,path,true); Error = null; return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { Error = e.Message; return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public bool SetGroupLayout(ShortcutEntry[] entries, string layout)
    {
        if (layout is not ("Vertical" or "Horizontal")) return false;
        var previous = GroupLayout; GroupLayout = layout;
        if (Save(entries)) return true;
        GroupLayout = previous; return false;
    }
}

public interface IShortcutExecutor { Task Execute(ShortcutStep step, CancellationToken cancellation); }
public sealed class ShortcutRunner(IShortcutExecutor executor)
{
    private int running;
    public bool IsRunning => Volatile.Read(ref running) != 0;
    public async Task Run(ShortcutEntry entry, Action<int> progress, CancellationToken cancellation)
    {
        if (ShortcutPolicy.Validate(entry) is { } error) throw new InvalidOperationException(error);
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
