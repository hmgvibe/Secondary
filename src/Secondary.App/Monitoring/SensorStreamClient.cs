using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.App.Monitoring;

public static class SensorStreamClient
{
    public static async Task RunAsync(bool high, Action<SensorSnapshot> sample, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = Path.Combine(AppContext.BaseDirectory, "SensorHelper", "Secondary.SensorHelper.exe");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        info.ArgumentList.Add(high ? "--stream-high" : "--stream-low");
        using var process = Process.Start(info) ?? throw new IOException("無法啟動感測助手");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Heartbeat()
        {
            while (!cancel.IsCancellationRequested)
            {
                await process.StandardInput.WriteLineAsync("ping".AsMemory(), cancel.Token);
                await process.StandardInput.FlushAsync(cancel.Token);
                await Task.Delay(5000, cancel.Token);
            }
        }
        var heartbeat = Heartbeat();
        var output = PresentMonStream.ReadLines(process.StandardOutput, 512 * 1024, line => sample(SensorSnapshot.Parse(line)), cancel.Token);
        var errors = PresentMonStream.ReadLines(process.StandardError, 16384, _ => { }, cancel.Token);
        try
        {
            var completed = await Task.WhenAny(output, errors, heartbeat, process.WaitForExitAsync(cancel.Token));
            await completed;
            token.ThrowIfCancellationRequested();
            throw new IOException("感測助手意外停止；離開再返回遊戲模式可重試。");
        }
        finally
        {
            cancel.Cancel();
            process.StandardInput.Close();
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            try { await Task.WhenAll(output, errors, heartbeat); } catch (Exception) { }
        }
    }
}
