using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Secondary.Core;

namespace Secondary.Diagnostics;

internal sealed record ProbeResult(string State, string Reason, int ProcessId, DateTimeOffset Time,
    int Frames, bool HeaderRecognized, int? ExitCode, PresentMonFrame[] Tail);

internal static class PresentMonProbe
{
    private const string Hash = "B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF";
    public static async Task<ProbeResult> Run(string executable, int pid, CancellationToken cancellation)
    {
        var parser = new PresentMonCsv(pid);
        var tail = new Queue<PresentMonFrame>();
        var count = 0;
        ProbeResult Result(string state, string reason, int? code = null) =>
            new(state, reason, pid, DateTimeOffset.UtcNow, count, parser.HasHeader, code, tail.ToArray());
        try
        {
            executable = Path.GetFullPath(executable);
            // Hold the file against replacement between verification and execution.
            using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)) != Hash)
                return Result("Rejected", "Expected official PresentMon 2.6.0 x64 SHA256");
            using var target = Process.GetProcessById(pid);
            if (target.HasExited) return Result("Unavailable", "Selected process has exited");
            var session = "Secondary-Probe-" + Guid.NewGuid().ToString("N");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--process_id", pid.ToString(), "--session_name", session,
                "--output_stdout", "--no_console_stats", "--v2_metrics", "--qpc_time_ms", "--timed", "5",
                "--terminate_after_timed", "--terminate_on_proc_exit", "--no_track_input" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var errors = new StringBuilder();
            async Task DrainErrors()
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                    if (errors.Length < 8192) errors.AppendLine(line[..Math.Min(line.Length, 1024)]);
            }
            async Task ReadFrames()
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                    if (parser.Read(line) is { } frame)
                    {
                        count++;
                        if (tail.Count == 60) tail.Dequeue();
                        tail.Enqueue(frame);
                    }
            }
            var output = ReadFrames();
            var error = DrainErrors();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Stop only our unique session; never use --stop_existing_session or elevation.
                var stopInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "--session_name", session, "--terminate_existing_session" }) stopInfo.ArgumentList.Add(argument);
                using var stop = Process.Start(stopInfo)!;
                using var stopTimeout = new CancellationTokenSource(3000);
                try { await stop.WaitForExitAsync(stopTimeout.Token); }
                catch (OperationCanceledException) { if (!stop.HasExited) stop.Kill(); }
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                await Task.WhenAll(output, error);
                cancellation.ThrowIfCancellationRequested();
                return Result("Unavailable", "Capture timeout; own session cleanup requested", process.ExitCode);
            }
            await Task.WhenAll(output, error);
            cancellation.ThrowIfCancellationRequested();
            return Result(process.ExitCode == 0 && count > 0 ? "Available" : "Unavailable",
                errors.Length > 0 ? errors.ToString() : count > 0 ? "Received selected-PID v2 frames; no FPS aggregation performed" :
                "No frames: check ETW permissions, process activity and supported presentation API; not zero FPS", process.ExitCode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result("Unavailable", e.Message); }
    }
}
