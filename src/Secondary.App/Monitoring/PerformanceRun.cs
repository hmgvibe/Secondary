using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Secondary.App.Monitoring;

public static class PerformanceRun
{
    public static async Task Run(MainWindow window, ShellViewModel model, string output, bool afterFull = false)
    {
        try
        {
            if (afterFull) { await Task.Delay(10000); window.SetAppearance(Presentation.ShellAppearance.Game); }
            await Task.Delay(15000);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var startCpu = process.TotalProcessorTime.TotalSeconds;
            var started = Stopwatch.GetTimestamp();
            var initialUpdates = model.SystemDashboard.UpdateCount;
            var initialMode = model.Modes.Mode;
            var valid = true;
            var samples = new List<object>();
            long maxWorking = 0, maxPrivate = 0;
            var lastCpu = startCpu;
            var lastTime = started;
            double maxCpu = 0;
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(5000);
                process.Refresh();
                var now = Stopwatch.GetTimestamp();
                var cpu = process.TotalProcessorTime.TotalSeconds;
                valid &= window.IsVisible && window.WindowState != WindowState.Minimized && model.Modes.Mode == initialMode && model.Modes.Current == "system";
                var percent = 100 * (cpu - lastCpu) / Stopwatch.GetElapsedTime(lastTime, now).TotalSeconds / Environment.ProcessorCount;
                maxCpu = Math.Max(maxCpu, percent);
                maxWorking = Math.Max(maxWorking, process.WorkingSet64);
                maxPrivate = Math.Max(maxPrivate, process.PrivateMemorySize64);
                samples.Add(new { Seconds = Stopwatch.GetElapsedTime(started, now).TotalSeconds, CpuPercent = percent, WorkingSetMiB = process.WorkingSet64 / 1048576.0, PrivateMiB = process.PrivateMemorySize64 / 1048576.0, Updates = model.SystemDashboard.UpdateCount });
                lastCpu = cpu; lastTime = now;
            }
            var elapsed = Stopwatch.GetElapsedTime(started, lastTime).TotalSeconds;
            var average = 100 * (lastCpu - startCpu) / elapsed / Environment.ProcessorCount;
            valid &= model.SystemDashboard.UpdateCount - initialUpdates >= 10;
            var result = new { Mode = model.Modes.Mode.ToString(), AfterFullMode = afterFull, ValidRun = valid, MonitoringUpdates = model.SystemDashboard.UpdateCount - initialUpdates, LogicalProcessors = Environment.ProcessorCount, WarmupSeconds = 15, MeasuredSeconds = elapsed,
                WidthDip = window.ActualWidth, HeightDip = window.ActualHeight,
                AverageCpuPercent = average, MaxFiveSecondCpuPercent = maxCpu, MaxWorkingSetMiB = maxWorking / 1048576.0, MaxPrivateMiB = maxPrivate / 1048576.0,
                CpuBudgetPassed = valid && average < 0.5, MemoryBudgetPassed = valid && maxWorking < 100 * 1024 * 1024,
                Metrics = model.SystemDashboard.Cards.Select(c => new { c.Title, c.Value, c.Detail }).ToArray(), model.SystemDashboard.Status, Samples = samples };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { await File.WriteAllTextAsync(output + ".error.txt", e.ToString()); }
        finally { window.AllowClose = true; window.Close(); Application.Current.Shutdown(); }
    }
}
