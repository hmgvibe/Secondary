using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Linux.Platform;

// Runs shortcut steps with the user's normal rights. Websites, folders and documents open through
// the desktop (gio), .desktop launchers start through gio launch, executables run directly, and .sh
// scripts run through /bin/sh from a private copy of exactly the bytes the user confirmed.
// An executor that can also describe what it is about to run, for the confirmation dialog.
public interface IReviewingExecutor : IShortcutExecutor { string Review(ShortcutEntry entry); }

public sealed class LinuxShortcutExecutor : IReviewingExecutor
{
    private const long MaxScriptBytes = 10_000_000;
    private readonly Dictionary<string, string> scriptHashes = new(StringComparer.Ordinal);
    private readonly string scriptDirectory;
    public Func<ProcessStartInfo, Process?> Start { get; init; } = Process.Start;
    public LinuxShortcutExecutor(string? runtimeDirectory = null)
    {
        var runtime = runtimeDirectory ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        scriptDirectory = Path.Combine(!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime) ? runtime : Path.GetTempPath(), "secondary", "scripts");
    }

    public string Review(ShortcutEntry entry)
    {
        scriptHashes.Clear();
        return string.Join("\n\n", entry.Steps!.Select((s, i) =>
        {
            if (s.Kind == "Wait") return $"{i + 1}. 等待 {s.Seconds} 秒";
            var text = $"{i + 1}. {s.Kind}\n{s.Target}";
            if (s.Kind == "Keys") return text + "\nLinux 不支援此步驟，執行到這裡會停止。";
            if (s.Arguments.Length > 0) text += "\n參數：" + s.Arguments;
            if (s.WorkingDirectory.Length > 0) text += "\n工作目錄：" + s.WorkingDirectory;
            if (s.Kind == "Script") { var hash = Hash(s.Target); scriptHashes[s.Target] = hash; text += "\nSHA256：" + hash; }
            // A launcher hides its real command; show it before the user approves.
            else if (s.Target.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && File.Exists(s.Target))
                text += "\n應用程式啟動器會執行：" + (DesktopExec(s.Target) ?? "（未提供 Exec）");
            return text;
        }));
    }
    private static string Hash(string path)
    {
        if (new FileInfo(path).Length > MaxScriptBytes) throw new IOException("腳本超過 10 MB，未執行。");
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    internal static string? DesktopExec(string path)
    {
        if (new FileInfo(path).Length > 65536) return null;
        var section = false;
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) { if (section) break; section = text == "[Desktop Entry]"; continue; }
            if (section && text.StartsWith("Exec=", StringComparison.Ordinal)) return text[5..].Trim();
        }
        return null;
    }

    public async Task Execute(ShortcutStep step, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Linux 版的捷徑只能在 Linux 上執行。");
        if (step.Kind == "Keys") throw new InvalidOperationException(ShortcutRules.Linux.KeysUnsupported);
        if (step.WorkingDirectory.Length > 0 && !Directory.Exists(step.WorkingDirectory)) throw new DirectoryNotFoundException("工作目錄不存在。");
        var web = Uri.TryCreate(step.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        if (!web && !File.Exists(step.Target) && !Directory.Exists(step.Target)) throw new FileNotFoundException("目標已移動或不存在。", step.Target);
        if (step.Kind == "Script") { await RunScript(step, cancellation).ConfigureAwait(false); return; }
        if (web || Directory.Exists(step.Target)) { await Desktop("open", web ? uri!.AbsoluteUri : step.Target, cancellation).ConfigureAwait(false); return; }
        var extension = Path.GetExtension(step.Target).ToLowerInvariant();
        if (extension == ".desktop") { await Desktop("launch", step.Target, cancellation).ConfigureAwait(false); return; }
        var executable = (File.GetUnixFileMode(step.Target) & UnixFileMode.UserExecute) != 0;
        if (extension == ".appimage" && !executable) throw new InvalidOperationException("AppImage 沒有執行權限；請在檔案內容中允許執行後再試。");
        if (!executable) { await Desktop("open", step.Target, cancellation).ConfigureAwait(false); return; }
        var start = new ProcessStartInfo(step.Target)
        {
            UseShellExecute = false, Arguments = step.Arguments,
            WorkingDirectory = step.WorkingDirectory.Length > 0 ? step.WorkingDirectory : Path.GetDirectoryName(step.Target)!
        };
        // The launched program belongs to the user and keeps running; it is never waited on or stopped.
        using var launched = Start(start) ?? throw new IOException("無法啟動程式。");
    }

    // gio hands the target to the desktop's default application or launcher, then exits.
    private async Task Desktop(string verb, string target, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("gio") { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add(verb); start.ArgumentList.Add(target);
        Process? process;
        try { process = Start(start); }
        catch (Win32Exception) when (verb == "open")
        {
            var fallback = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            fallback.ArgumentList.Add(target);
            process = Start(fallback);
        }
        using var opener = process ?? throw new IOException("無法交給桌面開啟。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try { await opener.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("桌面沒有回應開啟要求。"); }
        if (opener.ExitCode != 0) throw new InvalidOperationException(verb == "launch" ? "無法啟動應用程式啟動器；請確認它是有效的 .desktop 檔。" : "桌面無法開啟此目標；請確認有可用的預設程式。");
    }

    private async Task RunScript(ShortcutStep step, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Linux 版的腳本只能在 Linux 上執行。");
        byte[] content;
        using (var file = File.OpenRead(step.Target))
        {
            if (file.Length > MaxScriptBytes) throw new IOException("腳本超過 10 MB，未執行。");
            content = new byte[file.Length];
            await file.ReadExactlyAsync(content, cancellation).ConfigureAwait(false);
        }
        if (!scriptHashes.TryGetValue(step.Target, out var expected) || Convert.ToHexString(SHA256.HashData(content)) != expected)
            throw new InvalidOperationException("腳本自確認後已變更，請重新確認。");
        // The confirmed bytes run from a private copy, so the original cannot change between check and run.
        Directory.CreateDirectory(scriptDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var copy = Path.Combine(scriptDirectory, Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            var output = new FileStream(copy, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            await using (output.ConfigureAwait(false))
                await output.WriteAsync(content, cancellation).ConfigureAwait(false);
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                WorkingDirectory = step.WorkingDirectory.Length > 0 ? step.WorkingDirectory : Path.GetDirectoryName(step.Target)!
            };
            start.ArgumentList.Add(copy);
            using var process = Start(start) ?? throw new IOException("無法啟動腳本。");
            // Cancelling stops only this script process; anything it started keeps running.
            using var stop = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
            try { await process.WaitForExitAsync(cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                throw;
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"腳本失敗，結束碼 {process.ExitCode}；後續步驟已停止。");
        }
        finally { try { File.Delete(copy); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
