using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Monitoring;

public static class PresentMonStream
{
    private const string ExpectedHash = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";
    public static async Task RecoverAbandonedSessionsAsync(Action<string> status)
    {
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "Monitoring", "PresentMon-2.6.0-x64.exe");
            if (!File.Exists(executable)) return;
            using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(await SHA256.HashDataAsync(file)) != ExpectedHash) return;
            var budget = Stopwatch.StartNew();
            await PresentMonSessionLease.RecoverAsync(session => budget.Elapsed.TotalSeconds < 6
                ? StopSessionAsync(executable, session) : Task.FromResult(false), status);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { status("自身幀捕獲恢復失敗：" + error.Message); }
    }
    public static async Task RunAsync(int pid, Action<PresentMonFrame> frame, Action<string> status, CancellationToken token)
    {
        var captureStatus = "等待 PresentMon";
        void Report(string message) { captureStatus = message; status(message); }
        var executable = Path.Combine(AppContext.BaseDirectory, "Monitoring", "PresentMon-2.6.0-x64.exe");
        if (!File.Exists(executable)) { status("未安裝已驗證的 PresentMon console"); return; }
        using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != ExpectedHash)
        { status("PresentMon 雜湊不符，拒絕啟動"); return; }
        var session = "Secondary-Game-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--process_id", pid.ToString(), "--session_name", session, "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time_ms", "--no_track_input", "--terminate_on_proc_exit" }) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var job = new CaptureJob();
        using var lease = new PresentMonSessionLease(session);
        var parser = new PresentMonCsv(pid);
        var errors = new StringBuilder();
        using var readers = new CancellationTokenSource();
        Task output = Task.CompletedTask, errorReader = Task.CompletedTask;
        using var process = Process.Start(info) ?? throw new IOException("無法啟動 PresentMon");
        try
        {
            // Attach before readers or callbacks can delay ownership of the newly launched child.
            job.Attach(process);
            output = ReadLines(process.StandardOutput, 16384, line => { if (parser.Read(line) is { } item) frame(item); }, readers.Token);
            errorReader = ReadLines(process.StandardError, 16384, line => { if (errors.Length < 8192) errors.AppendLine(line[..Math.Min(1024, line.Length)]); }, readers.Token);
            Report("等待指定程序呈現幀；需要 ETW 權限");
            var exit = process.WaitForExitAsync(token);
            var remaining = new System.Collections.Generic.List<Task> { output, errorReader, exit };
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).WaitAsync(token);
                await done;
                remaining.Remove(done);
            }
            token.ThrowIfCancellationRequested();
            var details = errors.ToString();
            Report(details.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                ? "ETW 存取被拒；FPS 不可用。未提權或變更帳號群組。"
                : $"PresentMon 已停止（{process.ExitCode}）；" + (details.Length > 0 ? details : "未再接收幀資料，重新選擇程序可重試。"));
        }
        finally
        {
            // Even a terminated child can leave ETW alive. Always stop this exact random lease.
            var cleaned = await StopSessionAsync(executable, session);
            if (cleaned) lease.MarkCleaned();
            else Report(captureStatus + "\n自身 ETW 會話清理未完成，已保留下次啟動的重試紀錄。");
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { status("幀捕獲子程序停止逾時；父程序退出時將由 Job Object 終止。"); }
            readers.Cancel();
            try { await Task.WhenAll(output, errorReader).WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        }
    }

    private static async Task<bool> StopSessionAsync(string executable, string session)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--session_name"); info.ArgumentList.Add(session); info.ArgumentList.Add("--terminate_existing_session");
        using var deadline = new CancellationTokenSource(2500);
        Process? process = null;
        CaptureJob? job = null;
        try
        {
            job = new CaptureJob();
            process = Process.Start(info) ?? throw new IOException("無法啟動自身 ETW 會話清理");
            job.Attach(process);
            var drain = Task.WhenAll(ReadLines(process.StandardOutput, 16384, _ => { }, deadline.Token),
                ReadLines(process.StandardError, 16384, _ => { }, deadline.Token));
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), drain).WaitAsync(deadline.Token);
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return false; }
        finally
        {
            try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            process?.Dispose();
            job?.Dispose();
        }
    }

    internal static async Task ReadLines(StreamReader reader, int maxLine, Action<string> accept, CancellationToken token)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { accept(line.ToString().TrimEnd('\r')); line.Clear(); }
                else { if (line.Length >= maxLine) throw new IOException("監測輸出行超出限制"); line.Append(buffer[i]); }
            }
        if (line.Length > 0) accept(line.ToString());
    }
}
