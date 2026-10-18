using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Secondary.App;
using Secondary.App.Monitoring;
using Secondary.Core;
using Secondary.Shared;
using Secondary.Monitoring;

internal static class UiDataOptimizationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.UiDataChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var displays = new DisplayService();
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "blocked settings parent");
            var settingsPath = Path.Combine(blocked, "settings.json");
            using var model = new ShellViewModel(displays, new SettingsStore(settingsPath));
            model.SaveRuntime(theme: "Dark");
            check(model.Settings.Theme == "Dark" && model.HasUnsavedSettings, "Runtime settings apply immediately and retain failed persistence state");
            File.Delete(blocked); Directory.CreateDirectory(blocked);
            model.SaveRuntime(theme: "Dark");
            check(!model.HasUnsavedSettings && new SettingsStore(settingsPath).Load().Theme == "Dark", "Same-value runtime save retries after an IO failure");
            check(model.RetrySaveSettings(), "Explicit retry is safe after settings are already saved");

            var profile = Path.Combine(directory, "single-instance.json");
            var shows = 0;
            using (var owner = SingleInstanceCoordinator.Acquire(profile))
            {
                check(owner is not null, "First instance acquires the current-user profile writer");
                owner!.Listen(() => Interlocked.Increment(ref shows));
                var duplicateIsRejected = Task.Run(() =>
                {
                    using var duplicate = SingleInstanceCoordinator.Acquire(profile);
                    return duplicate is null;
                }).GetAwaiter().GetResult();
                check(duplicateIsRejected, "Second instance cannot become another settings writer");
                var shown = SingleInstanceCoordinator.ShowExistingAsync(profile).GetAwaiter().GetResult();
                check(owner.LastListenerError is null || System.Text.RegularExpressions.Regex.IsMatch(owner.LastListenerError,
                    "^(IOException|UnauthorizedAccessException) / HResult 0x[0-9A-F]{8}$"), "Listener diagnostics contain only the exception type and error code");
                check(shown && shows == 1,
                    "Second instance sends the bounded show request over the current-user pipe; listener diagnostic: " + (owner.LastListenerError ?? "none"));
                check(SingleInstanceCoordinator.ShowExistingAsync(profile).GetAwaiter().GetResult() && shows == 2,
                    "Show listener accepts repeated launches without leaking its previous connection");
                using var independent = SingleInstanceCoordinator.Acquire(Path.Combine(directory, "another-profile.json"));
                check(independent is not null, "Different settings profiles have independent writer identities");
            }
            using (var restarted = SingleInstanceCoordinator.Acquire(profile))
                check(restarted is not null, "Writer ownership is released on normal shutdown");

            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-target" } })!)
            {
                using (var job = new CaptureJob()) job.Attach(child);
                check(child.WaitForExit(1500), "Closing the capture job terminates only its owned child process");
                if (!child.HasExited) child.Kill();
            }

            var resourceType = typeof(PerformanceRun).GetNestedType("ResourceTree", BindingFlags.NonPublic)!;
            var resourceTree = Activator.CreateInstance(resourceType, nonPublic: true)!;
            var readResources = resourceType.GetMethod("Read")!;
            object ReadResources() => readResources.Invoke(resourceTree, null)!;
            T Value<T>(object sample, string name) => (T)sample.GetType().GetProperty(name)!.GetValue(sample)!;
            var baseline = ReadResources();
            check(Value<bool>(baseline, "MainProcessAvailable") && Value<bool>(baseline, "FullCoverage") && Value<int>(baseline, "FailedProcesses") == 0,
                "Native benchmark resource enumeration exposes complete coverage for the readable process tree");
            using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--wait-target" } })!)
            {
                var observed = ReadResources();
                check(Value<int>(observed, "Processes") > Value<int>(baseline, "Processes") && Value<bool>(observed, "FullCoverage"),
                    "Benchmark resources include a verified current child process");
                var identities = (Dictionary<int, long>)resourceType.GetField("owned", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resourceTree)!;
                identities[child.Id] = child.StartTime.ToUniversalTime().Ticks - 1;
                var reused = ReadResources();
                check(!Value<bool>(reused, "FullCoverage") && Value<int>(reused, "IdentityMismatches") > 0 && Value<string[]>(reused, "CoverageErrors").Length > 0,
                    "A reused remembered PID is excluded from its old ownership proof and reports partial coverage");
                check(child.WaitForExit(3000), "Benchmark resource test child exits within its bounded lifetime");
                var ended = ReadResources();
                check(Value<double>(ended, "TotalCpuSeconds") >= Value<double>(observed, "TotalCpuSeconds") && Value<bool>(ended, "FullCoverage"),
                    "Observed child CPU history remains monotonic after the child exits without leaving a coverage error");
            }

            var session = new GameTelemetrySession(true, false, null, () => new FailingSampler());
            var initial = session.Read();
            check(initial.SystemState == TelemetrySourceState.Waiting && initial.SensorState == TelemetrySourceState.Disabled && initial.FrameState == TelemetrySourceState.Disabled,
                "Telemetry begins with independent waiting and disabled source states");
            using var cancellation = new CancellationTokenSource();
            var task = Task.Run(() => session.RunAsync(cancellation.Token));
            var deadline = Stopwatch.StartNew();
            while (session.Read().SystemState != TelemetrySourceState.Failed && deadline.Elapsed.TotalSeconds < 3) Thread.Sleep(10);
            var failed = session.Read();
            check(failed.System is null && failed.SystemState == TelemetrySourceState.Failed && failed.SystemStatus.Contains("fixture system failure"),
                "Native system source failure exposes its own reason and unavailable values");
            check(!failed.GpuStatus.Contains("fixture system failure") && failed.SensorState == TelemetrySourceState.Disabled,
                "System failure never overwrites the independent GPU or hardware source status");
            cancellation.Cancel(); task.GetAwaiter().GetResult();
            var stopped = session.Read();
            check(stopped.SystemState == TelemetrySourceState.Stopped && stopped.GpuState == TelemetrySourceState.Stopped && stopped.System is null && stopped.Gpu is null,
                "Stopping telemetry invalidates old source values and marks their state stopped");

            SystemSample Healthy() => new(50, 1024, 512, 1, 2, null, "", "", "", DateTimeOffset.UtcNow);
            var expiredBasic = Observe(new GameTelemetrySession(true, false, null,
                () => new FixtureSampler(() => Healthy() with { Time = DateTimeOffset.UtcNow.AddMinutes(-5) })),
                snapshot => snapshot.SystemState == TelemetrySourceState.Stale);
            check(expiredBasic.System is null && expiredBasic.CpuState == TelemetrySourceState.Stale && expiredBasic.MemoryState == TelemetrySourceState.Stale && expiredBasic.LastSystemSuccess is null,
                "Repeated expired basic snapshots cannot refresh CPU values, state or success time");
            var baselineBasic = Observe(new GameTelemetrySession(true, false, null,
                () => new FixtureSampler(() => Healthy() with { Cpu = null, Download = null, Upload = null })),
                snapshot => snapshot.System is not null);
            check(baselineBasic.CpuState == TelemetrySourceState.Waiting && baselineBasic.MemoryState == TelemetrySourceState.Available && baselineBasic.NetworkState == TelemetrySourceState.Waiting,
                "CPU and network baseline waiting never becomes failed memory or fabricated zero readings");
            var missingCpu = Observe(new GameTelemetrySession(true, false, null,
                () => new FixtureSampler(() => Healthy() with { Cpu = null, CpuState = TelemetrySourceState.Available })), snapshot => snapshot.System is not null);
            check(missingCpu.CpuState == TelemetrySourceState.Failed && missingCpu.System!.Cpu is null && missingCpu.MemoryState == TelemetrySourceState.Available,
                "An available CPU state with no value becomes failed while other native sources remain available");
            var failedBasic = Observe(new GameTelemetrySession(true, false, null,
                () => new FixtureSampler(() => Healthy() with { CpuState = TelemetrySourceState.Failed, MemoryState = TelemetrySourceState.Failed, NetworkState = TelemetrySourceState.Failed, SourceStatus = "fixture all source failure" })),
                snapshot => snapshot.SystemState == TelemetrySourceState.Failed);
            check(failedBasic.System is { Cpu: null, TotalMemory: 0, Download: null, Upload: null } && failedBasic.CpuState == TelemetrySourceState.Failed && failedBasic.LastSystemSuccess is null,
                "Failed native source states cannot expose leftover numbers or advance a successful sample timestamp");
            var oldReading = new MonitoringReading("GPU temperature", "LibreHardwareMonitor 0.9.6", "/gpu-test/temperature/0",
                DateTimeOffset.UtcNow.AddMinutes(-5), 70, "°C", ReadingState.Available);
            Func<bool, Action<SensorSnapshot>, CancellationToken, Task> Feed(Func<SensorSnapshot> next) => async (_, publish, token) =>
            {
                while (!token.IsCancellationRequested) { publish(next()); await Task.Delay(25, token); }
            };
            var expiredHardware = Observe(new GameTelemetrySession(true, true, null, () => new FixtureSampler(Healthy),
                Feed(() => new(1, [oldReading]))), snapshot => snapshot.SensorState == TelemetrySourceState.Stale && snapshot.System is not null);
            check(expiredHardware.Sensors!.Readings[0] is { Value: null, State: ReadingState.Unavailable } && expiredHardware.LastSensorSuccess is null && expiredHardware.CpuState == TelemetrySourceState.Available,
                "Fresh helper transport does not make an expired reading live or invalidate healthy CPU data");
            var mixedHardware = Observe(new GameTelemetrySession(true, true, null, () => new FixtureSampler(Healthy),
                Feed(() => new(1, [oldReading, oldReading with { Identity = "/gpu-test/temperature/1", Time = DateTimeOffset.UtcNow, Value = 42 }]))),
                snapshot => snapshot.SensorState == TelemetrySourceState.Available && snapshot.Sensors is not null);
            check(mixedHardware.Sensors!.Readings[0].Value is null && mixedHardware.Sensors.Readings[1].Value == 42 && mixedHardware.LastSensorSuccess.HasValue,
                "Only expired hardware metrics are cleared while fresh metrics retain independent success time");
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class FailingSampler : ISystemSampler
    {
        public SystemSample Read(bool game, bool high = false) => throw new IOException("fixture system failure");
        public void Dispose() { }
    }
    private sealed class FixtureSampler(Func<SystemSample> next) : ISystemSampler
    {
        public SystemSample Read(bool game, bool high = false) => next();
        public void Dispose() { }
    }
    private static GameTelemetrySnapshot Observe(GameTelemetrySession session, Func<GameTelemetrySnapshot, bool> ready)
    {
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(() => session.RunAsync(cancellation.Token));
        try
        {
            var deadline = Stopwatch.StartNew();
            GameTelemetrySnapshot snapshot;
            do
            {
                snapshot = session.Read();
                if (ready(snapshot)) return snapshot;
                Thread.Sleep(10);
            } while (deadline.Elapsed.TotalSeconds < 3);
            throw new TimeoutException("Fixture telemetry source did not produce its expected state.");
        }
        finally { cancellation.Cancel(); task.GetAwaiter().GetResult(); }
    }
}
