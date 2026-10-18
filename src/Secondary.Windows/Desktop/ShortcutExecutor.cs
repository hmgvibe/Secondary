using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Desktop;

public sealed class ShortcutExecutor : IShortcutExecutor
{
    private readonly Dictionary<string,string> scriptHashes = new(StringComparer.OrdinalIgnoreCase);
    public string Review(ShortcutEntry entry)
    {
        scriptHashes.Clear();
        return string.Join("\n\n",entry.Steps!.Select((s,i) =>
        {
            var text = $"{i+1}. {s.Kind}\n{s.Target}";
            if (s.Kind == "Wait") return $"{i+1}. 等待 {s.Seconds} 秒";
            if (s.Kind == "Keys") return text + $"\n程序：{s.ProcessName}\n完整視窗標題：{s.WindowTitle}";
            if (s.Arguments.Length > 0) text += "\n參數：" + s.Arguments;
            if (s.WorkingDirectory.Length > 0) text += "\n工作目錄：" + s.WorkingDirectory;
            if (s.Kind == "Script") { var hash = Hash(s.Target); scriptHashes[s.Target] = hash; text += "\nSHA256：" + hash; }
            return text;
        }));
    }
    private static string Hash(string path)
    {
        if (new FileInfo(path).Length > 10_000_000) throw new IOException("腳本超過 10 MB，未執行。");
        using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file));
    }
    public async Task Execute(ShortcutStep step, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (step.Kind == "Keys") { await SendKeys(step,cancellation); return; }
        if (step.WorkingDirectory.Length > 0 && !Directory.Exists(step.WorkingDirectory)) throw new DirectoryNotFoundException("工作目錄不存在。");
        var web = Uri.TryCreate(step.Target,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https";
        if (!web && !File.Exists(step.Target) && !Directory.Exists(step.Target)) throw new FileNotFoundException("目標已移動或不存在。",step.Target);
        if (step.Kind == "Open")
        {
            using var launched = Process.Start(new ProcessStartInfo(step.Target) { Arguments = step.Arguments, WorkingDirectory = step.WorkingDirectory, UseShellExecute = true, ErrorDialog = false });
            return;
        }
        // Deny writes/deletion while validating and executing the confirmed file.
        using var script = File.Open(step.Target,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (script.Length > 10_000_000 || !scriptHashes.TryGetValue(step.Target,out var expected) || Convert.ToHexString(SHA256.HashData(script)) != expected) throw new InvalidOperationException("腳本自確認後已變更，請重新確認。");
        var ps = Path.GetExtension(step.Target).Equals(".ps1",StringComparison.OrdinalIgnoreCase);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo(ps ? Path.Combine(system,"WindowsPowerShell","v1.0","powershell.exe") : Path.Combine(system,"cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = step.WorkingDirectory.Length > 0 ? step.WorkingDirectory : Path.GetDirectoryName(step.Target)! };
        if (ps) { foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", step.Target }) start.ArgumentList.Add(arg); }
        else
        {
            if (step.Target.IndexOfAny("%!&|<>^()".ToCharArray()) >= 0) throw new InvalidOperationException("批次腳本路徑不可含 shell 特殊字元，請移至簡單路徑。");
            start.Arguments = "/d /s /c \"\"" + step.Target + "\"\"";
        }
        using var process = Process.Start(start) ?? throw new IOException("無法啟動腳本。");
        // Synchronous cancellation also works during Dispatcher/application shutdown.
        using var stop = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"腳本失敗，結束碼 {process.ExitCode}；後續步驟已停止。");
    }
    private static async Task SendKeys(ShortcutStep step, CancellationToken cancellation)
    {
        if (!HotkeyGesture.TryParse(step.Target,out var gesture)) throw new InvalidOperationException("無效快捷鍵。");
        var matches = new List<(int Id,IntPtr Window)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                try { if (process.ProcessName.Equals(step.ProcessName,StringComparison.OrdinalIgnoreCase) && process.MainWindowTitle == step.WindowTitle && process.MainWindowHandle != IntPtr.Zero) matches.Add((process.Id,process.MainWindowHandle)); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (matches.Count != 1) throw new InvalidOperationException("找不到唯一符合程序名稱與完整標題的視窗，未傳送按鍵。");
        var target = matches[0];
        if (!SetForegroundWindow(target.Window)) throw new InvalidOperationException("Windows 拒絕切換焦點，未傳送按鍵。");
        await Task.Delay(150,cancellation);
        GetWindowThreadProcessId(target.Window,out var pid);
        if (GetForegroundWindow() != target.Window || pid != target.Id || new[] { 0x10,0x11,0x12,0x5B,0x5C }.Any(k => GetAsyncKeyState(k) < 0)) throw new InvalidOperationException("焦點已改變或修飾鍵正被按住，未傳送按鍵。");
        using var verified = Process.GetProcessById(target.Id);
        if (verified.MainWindowHandle != target.Window || verified.MainWindowTitle != step.WindowTitle || !verified.ProcessName.Equals(step.ProcessName,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目標視窗已變更，未傳送按鍵。");
        var keys = new List<ushort>();
        if ((gesture!.Modifiers & 2) != 0) keys.Add(0x11);
        if ((gesture.Modifiers & 1) != 0) keys.Add(0x12);
        if ((gesture.Modifiers & 4) != 0) keys.Add(0x10);
        keys.Add((ushort)gesture.VirtualKey);
        var inputs = keys.Select(k => new KeyInput { Type = 1, Key = k }).Concat(keys.AsEnumerable().Reverse().Select(k => new KeyInput { Type = 1, Key = k, Flags = 2 })).ToArray();
        cancellation.ThrowIfCancellationRequested();
        if (SendInput((uint)inputs.Length,inputs,40) != inputs.Length)
        {
            var releases = keys.AsEnumerable().Reverse().Select(k => new KeyInput { Type = 1, Key = k, Flags = 2 }).ToArray();
            SendInput((uint)releases.Length,releases,40);
            throw new InvalidOperationException("Windows 未接受完整按鍵序列，巨集停止；不嘗試提權。");
        }
    }
    [StructLayout(LayoutKind.Explicit,Size=40)] private struct KeyInput
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public ushort Key; [FieldOffset(12)] public uint Flags; }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,KeyInput[] inputs,int size);
}
