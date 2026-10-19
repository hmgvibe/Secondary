using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Secondary.Core;

namespace Secondary.App.Presentation;

// Export and import of the whole launchpad: this device's shortcuts, groups and group layout.
// Main-computer shortcuts live on the main computer and are not part of the file.
public sealed partial class LaunchpadView
{
    private const string TransferFilter = "Secondary 快捷導航設定 (*.json)|*.json";
    public Func<string?> PickExportPath { get; set; }
    public Func<string?> PickImportPath { get; set; }
    public Func<string, bool> ConfirmImport { get; set; }

    [MemberNotNull(nameof(PickExportPath), nameof(PickImportPath), nameof(ConfirmImport))]
    private void InitializeTransfer()
    {
        PickExportPath = () =>
        {
            var dialog = new SaveFileDialog { Filter = TransferFilter, DefaultExt = ".json", AddExtension = true, FileName = $"Secondary-快捷導航-{DateTime.Now:yyyyMMdd}.json" };
            return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
        };
        PickImportPath = () =>
        {
            var dialog = new OpenFileDialog { Filter = TransferFilter, CheckFileExists = true };
            return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
        };
        ConfirmImport = details => ShortcutDialogs.Confirm(Owner, "匯入快捷導航設定", details, "匯入");
        ConfirmMove = details => ShortcutDialogs.Confirm(Owner, "移到主電腦", details, "移到主電腦");
    }

    private void Export() { if (PickExportPath() is { } path) ExportTo(path); }
    private void Import() { if (PickImportPath() is { } path) ImportFrom(path); }

    public bool ExportTo(string path)
    {
        try { File.WriteAllBytes(path, ShortcutTransfer.Export(entries, library.GroupLayout, library.Pending)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { status.Text = "匯出失敗：" + e.Message; return false; }
        status.Text = $"已匯出 {entries.Length} 個捷徑" + (library.Pending.Length > 0 ? $"（另有 {library.Pending.Length} 個待處理）" : "") + $"、分組與排列方式到 {Path.GetFileName(path)}。"
            + (RemoteShown ? "主電腦的捷徑存放在主電腦，不包含在內。" : "");
        return true;
    }

    public bool ImportFrom(string path)
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
        if (!ConfirmImport(details)) { status.Text = "已取消匯入；設定沒有變更。"; return false; }
        if (!library.Replace(imported)) { status.Text = "匯入失敗：" + library.Error; return false; }
        entries = imported.Entries; Refresh();
        status.Text = $"已匯入 {imported.Entries.Length} 個捷徑" + (imported.Pending.Length > 0 ? $"，{imported.Pending.Length} 個待處理" : "") + (imported.Skipped > 0 ? $"，略過 {imported.Skipped} 個" : "") + "；沒有執行任何操作。";
        return true;
    }
}
