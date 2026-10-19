using System;
using System.IO;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Linux.Shortcuts;

// Export and import of the whole launchpad: this device's shortcuts, groups and group layout.
// Main-computer shortcuts live on the main computer and are not part of the file.
public sealed partial class LaunchpadView
{
    private async Task Export() { if (await host.PickExportPathAsync($"Secondary-快捷導航-{DateTime.Now:yyyyMMdd}.json") is { } path) ExportTo(path); }
    private async Task Import() { if (await host.PickImportPathAsync() is { } path) await ImportFrom(path); }

    public bool ExportTo(string path)
    {
        try { File.WriteAllBytes(path, ShortcutTransfer.Export(entries, library.GroupLayout, library.Pending)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { status.Text = "匯出失敗：" + e.Message; return false; }
        status.Text = $"已匯出 {entries.Length} 個捷徑" + (library.Pending.Length > 0 ? $"（另有 {library.Pending.Length} 個待處理）" : "") + $"、分組與排列方式到 {Path.GetFileName(path)}。"
            + (RemoteShown ? "主電腦的捷徑存放在主電腦，不包含在內。" : "");
        return true;
    }

    public async Task<bool> ImportFrom(string path)
    {
        if (execution is not null) { status.Text = "請先取消正在執行的巨集，再匯入。"; return false; }
        ShortcutImportResult imported;
        try { imported = ShortcutTransfer.Read(path, library.Rules); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { status.Text = "匯入失敗：" + e.Message; return false; }
        var details = $"將以檔案中的 {imported.Entries.Length + imported.Pending.Length} 個捷徑取代目前的 {entries.Length + library.Pending.Length} 個捷徑、分組與排列方式。"
            + (imported.Pending.Length > 0 ? $"\n\n其中 {imported.Pending.Length} 個無法在這臺設備執行，會列為「待處理」：可以移到主電腦執行、編輯或刪除。" : "")
            + (imported.Skipped > 0 ? $"\n\n有 {imported.Skipped} 個捷徑內容無效，會略過。" : "")
            + $"\n\n匯入的捷徑第一次執行時都需要重新確認。目前的設定會先備份為 {Path.GetFileName(library.BackupPath)}。";
        if (!await host.ConfirmAsync("匯入快捷導航設定", details, "匯入")) { status.Text = "已取消匯入；設定沒有變更。"; return false; }
        if (disposed || execution is not null) { status.Text = "狀態已變更，這次沒有匯入。"; return false; }
        if (!library.Replace(imported)) { status.Text = "匯入失敗：" + library.Error; return false; }
        entries = imported.Entries; Refresh();
        status.Text = $"已匯入 {imported.Entries.Length} 個捷徑" + (imported.Pending.Length > 0 ? $"，{imported.Pending.Length} 個待處理" : "") + (imported.Skipped > 0 ? $"，略過 {imported.Skipped} 個" : "") + "；沒有執行任何操作。";
        return true;
    }
}
