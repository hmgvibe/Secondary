using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Monitoring;

public static class SensorHelperClient
{
    public static async Task<SensorSnapshot> ReadAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "SensorHelper", "Secondary.SensorHelper.exe");
        if (!File.Exists(executable)) throw new IOException("未找到感测助手，请重新建置完整应用程序。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        info.ArgumentList.Add("--sample");
        using var process = Process.Start(info) ?? throw new IOException("无法启动感测助手。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        using var stopOnCancellation = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        // Only inherited anonymous pipe handles; no listening port or shared named pipe.
        async Task<string> ReadBounded(StreamReader reader)
        {
            var buffer = new char[4096];
            var output = new StringBuilder();
            int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                if (output.Length + length > 512 * 1024) throw new IOException("感测助手输出超出限制。");
                output.Append(buffer, 0, length);
            }
            return output.ToString();
        }
        var stdout = ReadBounded(process.StandardOutput);
        var stderr = ReadBounded(process.StandardError);
        try
        {
            // Observe stream failures promptly, rather than leaving a blocked producer alive.
            var exit = process.WaitForExitAsync(timeout.Token);
            var remaining = new System.Collections.Generic.List<Task> { stdout, stderr, exit };
            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining);
                await completed;
                remaining.Remove(completed);
            }
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"感测助手已停止（代码 {process.ExitCode}）。");
            return SensorSnapshot.Parse(await stdout);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new IOException("感测助手超时，已停止；可能有不相容的硬件或驱动。"); }
        finally
        {
            timeout.Cancel();
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            try { await Task.WhenAll(stdout, stderr); } catch (Exception) { /* Preserve the primary failure. */ }
        }
    }
}
