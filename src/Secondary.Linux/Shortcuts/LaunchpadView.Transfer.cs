using System;
using System.IO;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Linux.Shortcuts;

// Export and import of the whole launchpad: this device's shortcuts, groups and group layout.
// Main-computer shortcuts live on the main computer and are not part of the file.
public sealed partial class LaunchpadView
{
    private async Task Export() { if (await host.PickExportPathAsync($"Secondary-快捷导航-{DateTime.Now:yyyyMMdd}.json") is { } path) ExportTo(path); }
    private async Task Import() { if (await host.PickImportPathAsync() is { } path) await ImportFrom(path); }

    public bool ExportTo(string path)
    {
        try { File.WriteAllBytes(path, ShortcutTransfer.Export(entries, library.GroupLayout, library.Pending)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { status.Text = "导出失败：" + e.Message; return false; }
        status.Text = $"已导出 {entries.Length} 个快捷方式" + (library.Pending.Length > 0 ? $"（另有 {library.Pending.Length} 个待处理）" : "") + $"、分组与排列方式到 {Path.GetFileName(path)}。"
            + (RemoteShown ? "主机的快捷方式存放在主机，不包含在内。" : "");
        return true;
    }

    public async Task<bool> ImportFrom(string path)
    {
        if (execution is not null) { status.Text = "请先取消正在执行的巨集，再导入。"; return false; }
        ShortcutImportResult imported;
        try { imported = ShortcutTransfer.Read(path, library.Rules); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { status.Text = "导入失败：" + e.Message; return false; }
        var details = $"将以文件中的 {imported.Entries.Length + imported.Pending.Length} 个快捷方式取代目前的 {entries.Length + library.Pending.Length} 个快捷方式、分组与排列方式。"
            + (imported.Pending.Length > 0 ? $"\n\n其中 {imported.Pending.Length} 个无法在这台设备执行，会列为「待处理」：可以移到主机执行、编辑或删除。" : "")
            + (imported.Skipped > 0 ? $"\n\n有 {imported.Skipped} 个快捷方式内容无效，会略过。" : "")
            + $"\n\n导入的快捷方式第一次执行时都需要重新确认。目前的设置会先备份为 {Path.GetFileName(library.BackupPath)}。";
        if (!await host.ConfirmAsync("导入快捷导航设置", details, "导入")) { status.Text = "已取消导入；设置没有变更。"; return false; }
        if (disposed || execution is not null) { status.Text = "状态已变更，这次没有导入。"; return false; }
        if (!library.Replace(imported)) { status.Text = "导入失败：" + library.Error; return false; }
        entries = imported.Entries; Refresh();
        status.Text = $"已导入 {imported.Entries.Length} 个快捷方式" + (imported.Pending.Length > 0 ? $"，{imported.Pending.Length} 个待处理" : "") + (imported.Skipped > 0 ? $"，略过 {imported.Skipped} 个" : "") + "；没有执行任何操作。";
        return true;
    }
}
