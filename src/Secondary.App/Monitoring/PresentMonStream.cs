using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.App.Monitoring;

public static class PresentMonStream
{
    public static async Task RunAsync(int pid, Action<PresentMonFrame> frame, Action<string> status, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Monitoring", "PresentMon-2.6.0-x64.exe");
        if (!File.Exists(executable)) { status("未安裝已驗證的 PresentMon console"); return; }
        using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF")
        { status("PresentMon 雜湊不符，拒絕啟動"); return; }
        var session = "Secondary-Game-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--process_id", pid.ToString(), "--session_name", session, "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time_ms", "--no_track_input", "--terminate_on_proc_exit" }) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new IOException("無法啟動 PresentMon");
        var parser = new PresentMonCsv(pid);
        var errors = new StringBuilder();
        using var readers = new CancellationTokenSource();
        var output = ReadLines(process.StandardOutput, 16384, line => { if (parser.Read(line) is { } item) frame(item); }, readers.Token);
        var error = ReadLines(process.StandardError, 16384, line => { if (errors.Length < 8192) errors.AppendLine(line[..Math.Min(1024, line.Length)]); }, readers.Token);
        status("等待指定程序呈現幀；需要 ETW 權限");
        try
        {
            var exit = process.WaitForExitAsync(token);
            var remaining = new System.Collections.Generic.List<Task> { output, error, exit };
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).WaitAsync(token);
                await done;
                remaining.Remove(done);
            }
            token.ThrowIfCancellationRequested();
            var details = errors.ToString();
            status(details.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                ? "ETW 存取被拒；FPS 不可用。未提權或變更帳號群組。"
                : $"PresentMon 已停止（{process.ExitCode}）；" + (details.Length > 0 ? details : "未再接收幀資料，重新選擇程序可重試。"));
        }
        finally
        {
            if (!process.HasExited)
            {
                // Only this exact random session is stopped, never another application's capture.
                var stopInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                stopInfo.ArgumentList.Add("--session_name"); stopInfo.ArgumentList.Add(session); stopInfo.ArgumentList.Add("--terminate_existing_session");
                try
                {
                    using var stop = Process.Start(stopInfo)!;
                    var drain = Task.WhenAll(ReadLines(stop.StandardOutput, 16384, _ => { }, CancellationToken.None), ReadLines(stop.StandardError, 16384, _ => { }, CancellationToken.None));
                    using var deadline = new CancellationTokenSource(3000);
                    try { await stop.WaitForExitAsync(deadline.Token); }
                    catch (OperationCanceledException) { if (!stop.HasExited) stop.Kill(); }
                    await stop.WaitForExitAsync();
                    await drain;
                }
                catch (Exception e) { status("自身 ETW 會話清理失敗：" + e.Message); }
                if (!process.HasExited) process.Kill();
            }
            await process.WaitForExitAsync();
            readers.Cancel();
            try { await Task.WhenAll(output, error); } catch (Exception) { }
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
