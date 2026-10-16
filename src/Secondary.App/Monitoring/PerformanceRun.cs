using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Secondary.App.Monitoring;

public sealed record PerformanceRequest(string Report, string Scene, int Seconds = 60, int WarmupSeconds = 10, string? MediaPath = null);

// An explicit, isolated measurement window. Includes this process and only its descendants.
public static class PerformanceRun
{
    private static readonly string[] scenes = ["Home", "HomeWidgets", "Shortcuts", "GameLow", "GameHigh", "GameSensors", "Hidden", "Minimized", "Image", "Video", "Gradient", "Aurora"];
    public static PerformanceRequest Parse(string[] args)
    {
        if (args.Length < 3 || args[0] != "--measure" || !scenes.Contains(args[2]))
            throw new ArgumentException("量測方式：--measure <新報告路徑> <Home|HomeWidgets|Shortcuts|GameLow|GameHigh|GameSensors|Hidden|Minimized|Image|Video|Gradient|Aurora> [--seconds=60] [--warmup=10] [--media=本機檔案]");
        var report = Path.GetFullPath(args[1]);
        if (File.Exists(report) || Directory.Exists(report)) throw new ArgumentException("報告已存在；請指定新的檔案名稱。");
        var seconds = 60; var warmup = 10; string? media = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in args.Skip(3))
        {
            var key = arg.Split('=', 2)[0];
            if (!seen.Add(key)) throw new ArgumentException("量測參數重複：" + key);
            if (arg.StartsWith("--seconds=", StringComparison.Ordinal) && int.TryParse(arg[10..], out var duration) && duration is >= 5 and <= 86400) seconds = duration;
            else if (arg.StartsWith("--warmup=", StringComparison.Ordinal) && int.TryParse(arg[9..], out var delay) && delay is >= 0 and <= 300) warmup = delay;
            else if (arg.StartsWith("--media=", StringComparison.Ordinal) && arg.Length > 8) media = Path.GetFullPath(arg[8..]);
            else throw new ArgumentException("無效量測參數：" + arg);
        }
        if (args[2] is "Image" or "Video" && (media is null || !File.Exists(media))) throw new ArgumentException("圖片／影片情境需指定存在的 --media 本機檔案。");
        return new(report, args[2], seconds, warmup, media);
    }

    public static async Task Run(MainWindow window, ShellViewModel model, PerformanceRequest request)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.Report)!);
            window.PrepareMeasurement(request.Scene, request.MediaPath);
            if (request.Scene is "Hidden" or "Minimized")
                await window.GameMonitoringCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(request.WarmupSeconds * 1000);
            var sceneErrors = new HashSet<string>(StringComparer.Ordinal);
            void CheckScene()
            {
                if (window.MeasurementProblem(request.Scene) is { } problem) sceneErrors.Add(problem);
            }
            CheckScene();
            var mode = model.Modes.Mode;
            var initialUpdates = window.GameMonitoringUpdateCount;
            var allocatedAtStart = GC.GetTotalAllocatedBytes();
            var resources = new ResourceTree();
            var initial = resources.Read();
            var previousCpu = initial.TotalCpuSeconds;
            var clock = Stopwatch.StartNew();
            var lastTime = 0d; var valid = initial.FullCoverage;
            var samples = new List<ResourceSample>();
            double maxCpu = 0, maxLag = 0;
            for (var i = 0; i < request.Seconds; i++)
            {
                await Task.Delay(1000);
                var sample = await Task.Run(resources.Read);
                var elapsed = clock.Elapsed.TotalSeconds;
                var seconds = elapsed - lastTime;
                var cpu = Math.Max(0, 100 * (sample.TotalCpuSeconds - previousCpu) / seconds / Environment.ProcessorCount);
                var lag = Math.Max(0, (seconds - 1) * 1000);
                maxCpu = Math.Max(maxCpu, cpu); maxLag = Math.Max(maxLag, lag);
                var expectedVisibility = request.Scene == "Hidden" ? !window.IsVisible : request.Scene == "Minimized"
                    ? window.WindowState == WindowState.Minimized : window.IsVisible && window.WindowState != WindowState.Minimized;
                CheckScene();
                valid &= expectedVisibility && model.Modes.Mode == mode && !window.SettingsOpen && sample.MainProcessAvailable && sample.FullCoverage;
                samples.Add(sample with { Seconds = elapsed, CpuPercent = cpu, SchedulingDelayMs = lag, MonitoringUpdates = window.GameMonitoringUpdateCount });
                previousCpu = sample.TotalCpuSeconds; lastTime = elapsed;
            }
            var monitoring = window.GameMonitoringUpdateCount - initialUpdates;
            if (request.Scene is "GameLow" or "GameHigh" or "GameSensors") valid &= monitoring >= Math.Max(1, request.Seconds / 3);
            if (request.Scene is "Hidden" or "Minimized") valid &= !window.GameMonitoringActive && window.GameMonitoringCompletion.IsCompletedSuccessfully && monitoring == 0 && samples.All(s => s.Processes == 1);
            valid &= sceneErrors.Count == 0;
            var average = 100 * (samples[^1].TotalCpuSeconds - initial.TotalCpuSeconds) / lastTime / Environment.ProcessorCount;
            var result = new
            {
                Schema = 2, Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-lastTime - request.WarmupSeconds), request.Scene, request.WarmupSeconds,
                RequestedSeconds = request.Seconds, MeasuredSeconds = lastTime, ValidRun = valid, SceneErrors = sceneErrors,
                Os = RuntimeInformation.OSDescription, LogicalProcessors = Environment.ProcessorCount,
                WidthDip = window.ActualWidth, HeightDip = window.ActualHeight, Dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX,
                CpuDefinition = "Measured CPU time for this process and its observed descendants / elapsed time / logical processor count * 100",
                SchedulingDefinition = "Measurement continuation scheduling delay, including sampling overhead; not direct input latency",
                ResourceCoverage = "App plus observed descendants; shared working-set pages may be counted by more than one process",
                AverageCpuPercent = Math.Max(0, average), MaxOneSecondCpuPercent = maxCpu, MaxSchedulingDelayMs = maxLag,
                MaxWorkingSetMiB = samples.Max(s => s.WorkingSetBytes) / 1048576.0, MaxPrivateMiB = samples.Max(s => s.PrivateBytes) / 1048576.0,
                MainProcessAllocatedBytes = GC.GetTotalAllocatedBytes() - allocatedAtStart,
                MaxHandles = samples.Max(s => s.Handles), MaxThreads = samples.Max(s => s.Threads),
                MonitoringUpdates = monitoring, Sources = window.MeasurementSourceStatus, Samples = samples
            };
            using var output = new FileStream(request.Report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(output, result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception error)
        {
            AppDiagnostics.Write("measurement", error);
            try
            {
                using var output = new FileStream(request.Report + ".error.txt", FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(output);
                await writer.WriteAsync(error.GetType().Name + ": " + error.Message);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            { AppDiagnostics.Write("measurement-error-report", writeError); }
        }
        finally { window.AllowClose = true; window.Close(); Application.Current.Shutdown(); }
    }

    private sealed record ResourceSample(double TotalCpuSeconds, long WorkingSetBytes, long PrivateBytes, int Handles, int Threads,
        int Processes, bool MainProcessAvailable, double Seconds = 0, double CpuPercent = 0, double SchedulingDelayMs = 0, long MonitoringUpdates = 0,
        bool FullCoverage = true, int FailedProcesses = 0, int IdentityMismatches = 0, string[]? CoverageErrors = null);
    private sealed class ResourceTree
    {
        private readonly Dictionary<(int Id, long Start), double> cpuTimes = new();
        private readonly Dictionary<int, long> owned = new();
        private readonly long rootStarted;
        public ResourceTree()
        {
            using var root = Process.GetCurrentProcess();
            rootStarted = root.StartTime.ToUniversalTime().Ticks;
        }
        private sealed record ProcessMetrics(long Started, double Cpu, long Working, long Private, int Handles, int Threads);
        public ResourceSample Read()
        {
            var errors = new List<string>();
            var failures = 0; var mismatches = 0; var fullCoverage = true;
            void Error(string message)
            { fullCoverage = false; if (errors.Count < 16) errors.Add(message); }
            var children = SnapshotChildren(Error, out var capturedUtcTicks);
            var metrics = new Dictionary<int, ProcessMetrics?>();
            ProcessMetrics? ReadProcess(int id)
            {
                if (metrics.TryGetValue(id, out var existing)) return existing;
                ProcessMetrics? value = null;
                try
                {
                    using var process = Process.GetProcessById(id); process.Refresh();
                    value = new(process.StartTime.ToUniversalTime().Ticks, process.TotalProcessorTime.TotalSeconds,
                        process.WorkingSet64, process.PrivateMemorySize64, process.HandleCount, process.Threads.Count);
                }
                // A process disappearing between enumeration and reading is an expected exit.
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception error)
                { failures++; Error("Process resource query failed for PID " + id + " (" + error.NativeErrorCode + ")"); }
                metrics[id] = value; return value;
            }
            var ids = new HashSet<int>();
            var root = ReadProcess(Environment.ProcessId);
            var mainReadable = root is not null && root.Started == rootStarted;
            if (mainReadable) ids.Add(Environment.ProcessId);
            else Error("Main process identity or resource query is unavailable");
            var queue = new Queue<(int Id, long Started)>();
            if (mainReadable) queue.Enqueue((Environment.ProcessId, rootStarted));
            // Keep counting an already proven child that outlives its intermediate parent.
            // Its unchanged PID/start identity retains the original ownership proof.
            foreach (var identity in owned.ToArray())
            {
                var process = ReadProcess(identity.Key);
                if (process is null) { owned.Remove(identity.Key); continue; }
                if (process.Started != identity.Value)
                { owned.Remove(identity.Key); mismatches++; Error("Previously owned PID was reused: " + identity.Key); continue; }
                ids.Add(identity.Key); queue.Enqueue((identity.Key, identity.Value));
            }
            while (queue.TryDequeue(out var parent))
            {
                if (!children.TryGetValue(parent.Id, out var descendants)) continue;
                foreach (var id in descendants)
                {
                    if (id == Environment.ProcessId || ids.Contains(id)) continue;
                    var child = ReadProcess(id);
                    if (child is null) continue;
                    // A stale parent PID can refer to a new process. A PID reused after the
                    // Toolhelp snapshot also cannot inherit the old snapshot's parent identity.
                    if (child.Started < parent.Started || child.Started > capturedUtcTicks)
                    { mismatches++; Error("Excluded stale parent or reused snapshot PID " + id); continue; }
                    ids.Add(id); owned[id] = child.Started; queue.Enqueue((id, child.Started));
                }
            }
            long working = 0, memory = 0; int handles = 0, threads = 0, count = 0; var main = false;
            foreach (var id in ids)
            {
                var process = metrics[id]!;
                cpuTimes[(id, process.Started)] = process.Cpu;
                working += process.Working; memory += process.Private;
                handles += process.Handles; threads += process.Threads; count++;
                main |= id == Environment.ProcessId;
            }
            return new(cpuTimes.Values.Sum(), working, memory, handles, threads, count, main,
                FullCoverage: fullCoverage, FailedProcesses: failures, IdentityMismatches: mismatches, CoverageErrors: errors.ToArray());
        }
        private static Dictionary<int, List<int>> SnapshotChildren(Action<string> error, out long capturedUtcTicks)
        {
            var children = new Dictionary<int, List<int>>();
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            var nativeError = Marshal.GetLastWin32Error();
            capturedUtcTicks = DateTime.UtcNow.Ticks;
            if (snapshot == new IntPtr(-1)) { error("Process enumeration failed (" + nativeError + ")"); return children; }
            try
            {
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
                if (Process32FirstW(snapshot, ref entry))
                {
                    do
                    {
                        var pid = (int)entry.ProcessId; var owner = (int)entry.ParentProcessId;
                        if (!children.TryGetValue(owner, out var list)) children[owner] = list = [];
                        list.Add(pid);
                    } while (Process32NextW(snapshot, ref entry));
                    nativeError = Marshal.GetLastWin32Error();
                    if (nativeError is not (0 or 18)) error("Process enumeration was incomplete (" + nativeError + ")");
                }
                else error("Process enumeration could not read its first entry (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { CloseHandle(snapshot); }
            return children;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId; public UIntPtr DefaultHeap; public uint ModuleId, Threads, ParentProcessId;
            public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
