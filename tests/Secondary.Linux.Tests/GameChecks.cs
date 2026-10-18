using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Secondary.Core;
using Secondary.Linux;
using Secondary.Linux.Game;
using Secondary.Linux.Monitoring;
using static Program;

// Game mode: /proc and sysfs samplers on fixture trees, the MangoHud log follower, and the dashboard.
internal static class GameChecks
{
    public static void Run(string? output)
    {
        var root = Path.Combine(Path.GetTempPath(), "Secondary.Game-" + Guid.NewGuid().ToString("N"));
        try
        {
            TestProcParsing();
            TestSystemSampler(root);
            TestHardware(root);
            TestDrmUsage(Path.Combine(root, "drm"));
            TestMangoHudCsv();
            TestMangoHudLog(Path.Combine(root, "logs"));
            TestDashboard(root, output);
        }
        finally { Directory.Delete(root, true); }
    }

    // Prints real readings from this machine's /proc and /sys; nothing is asserted.
    public static void RunLive()
    {
        var system = new SystemSampler();
        using var hardware = new HardwareSampler();
        system.Sample(); hardware.Sample();
        Thread.Sleep(2000);
        Console.WriteLine(system.Sample());
        foreach (var reading in hardware.Sample())
            Console.WriteLine($"{reading.Identity,-28} {reading.Metric,-50} {reading.Value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—",8} {reading.Unit,-4} {reading.Reason}");
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
    private static string Stat(ulong user, ulong system, ulong idle, ulong iowait) =>
        $"cpu  {user} 0 {system} {idle} {iowait} 0 0 0 0 0\ncpu0 {user} 0 {system} {idle} {iowait} 0 0 0 0 0\nintr 1\n";
    private static string Net(long received, long sent) =>
        "Inter-|   Receive\n face |bytes    packets\n    lo: 999999 1 0 0 0 0 0 0 999999 1 0 0 0 0 0 0\n" +
        $"  eth0: {received} 10 0 0 0 0 0 0 {sent} 10 0 0 0 0 0 0\n wlan0: 1000 1 0 0 0 0 0 0 500 1 0 0 0 0 0 0\n";
    private const string Meminfo = "MemTotal:       16000000 kB\nMemFree:         1000000 kB\nMemAvailable:    4000000 kB\n";

    private static void TestProcParsing()
    {
        var cpu = SystemSampler.ReadCpu("cpu  100 20 30 400 50 6 7 8 0 0\n");
        Check(cpu == new CpuTimes(450, 30 + 6 + 7 + 8 + 450, 120), "/proc/stat maps to idle+iowait, kernel time including idle, and user+nice");
        Check(SystemSampler.ReadMemory(Meminfo) == (16000000UL * 1024, 4000000UL * 1024), "Memory uses MemTotal and MemAvailable, in bytes");
        Check(SystemSampler.ReadNetwork(Net(5000, 2000)) == (6000, 2500), "Network totals every interface except loopback");
        var failed = false; try { SystemSampler.ReadMemory("MemTotal: 1 kB\n"); } catch (FormatException) { failed = true; }
        Check(failed, "A meminfo without MemAvailable is rejected instead of guessed");
    }

    private static void TestSystemSampler(string root)
    {
        var proc = Path.Combine(root, "machine", "proc");
        Write(Path.Combine(proc, "stat"), Stat(100, 100, 700, 100));
        Write(Path.Combine(proc, "meminfo"), Meminfo);
        Write(Path.Combine(proc, "net", "dev"), Net(0, 0));
        var sampler = new SystemSampler(Path.Combine(root, "machine"));
        var first = sampler.Sample();
        Check(first.Cpu is null && first.Download is null && first.TotalMemory == 16000000UL * 1024, "The first sample has memory but no CPU or network baseline yet");
        Write(Path.Combine(proc, "stat"), Stat(250, 150, 900, 100));
        Write(Path.Combine(proc, "net", "dev"), Net(1048576, 0));
        Thread.Sleep(50);
        var second = sampler.Sample();
        Check(second.Cpu is { } busy && Math.Abs(busy - 50) < .01, $"CPU usage is busy time over all time between samples ({second.Cpu})");
        Check(second.Download > 0 && second.Upload == 0 && second.SourceStatus is null, "Network rates come from byte deltas");
        File.Delete(Path.Combine(proc, "meminfo"));
        Write(Path.Combine(proc, "stat"), Stat(300, 200, 1100, 100));
        var broken = sampler.Sample();
        Check(broken.TotalMemory == 0 && broken.Cpu is not null && broken.SourceStatus?.Contains("記憶體", StringComparison.Ordinal) == true, "A missing source is reported without hiding the others");
    }

    private static void TestHardware(string root)
    {
        var machine = Path.Combine(root, "machine");
        var sys = Path.Combine(machine, "sys");
        Write(Path.Combine(sys, "class", "hwmon", "hwmon0", "name"), "acpitz\n");
        Write(Path.Combine(sys, "class", "hwmon", "hwmon0", "temp1_input"), "99000\n");
        Write(Path.Combine(sys, "class", "hwmon", "hwmon3", "name"), "coretemp\n");
        Write(Path.Combine(sys, "class", "hwmon", "hwmon3", "temp1_input"), "61000\n");
        Write(Path.Combine(sys, "class", "hwmon", "hwmon3", "temp2_input"), "58000\n");
        Write(Path.Combine(sys, "devices", "system", "cpu", "cpu0", "cpufreq", "scaling_cur_freq"), "2400000\n");
        Write(Path.Combine(sys, "devices", "system", "cpu", "cpu1", "cpufreq", "scaling_cur_freq"), "3600000\n");
        Write(Path.Combine(sys, "devices", "system", "cpu", "cpufreq", "policy0", "scaling_cur_freq"), "9900000\n");
        // Windows cannot create the "intel-rapl:0" folder, so the power checks run on Linux only.
        var raplFixture = !OperatingSystem.IsWindows();
        var rapl = Path.Combine(sys, "class", "powercap", "intel-rapl:0");
        if (raplFixture)
        {
            Write(Path.Combine(rapl, "energy_uj"), "1000000\n");
            Write(Path.Combine(rapl, "max_energy_range_uj"), "262143328850\n");
        }
        // An AMD card with hwmon and VRAM, and an Intel iGPU with only frequency and RC6 residency.
        var amd = Path.Combine(sys, "class", "drm", "card0");
        Write(Path.Combine(amd, "device", "vendor"), "0x1002\n");
        Write(Path.Combine(amd, "device", "device"), "0x73ff\n");
        Write(Path.Combine(amd, "device", "uevent"), "DRIVER=amdgpu\nPCI_SLOT_NAME=0000:03:00.0\n");
        Write(Path.Combine(amd, "device", "gpu_busy_percent"), "37\n");
        Write(Path.Combine(amd, "device", "mem_info_vram_used"), (2048L * 1048576).ToString(CultureInfo.InvariantCulture));
        Write(Path.Combine(amd, "device", "mem_info_vram_total"), (8192L * 1048576).ToString(CultureInfo.InvariantCulture));
        Write(Path.Combine(amd, "device", "hwmon", "hwmon7", "temp1_input"), "54000\n");
        Write(Path.Combine(amd, "device", "hwmon", "hwmon7", "temp2_input"), "71000\n");
        Write(Path.Combine(amd, "device", "hwmon", "hwmon7", "freq1_input"), "2450000000\n");
        Write(Path.Combine(amd, "device", "hwmon", "hwmon7", "power1_average"), "132000000\n");
        Write(Path.Combine(sys, "class", "drm", "card0-DP-1", "status"), "connected\n");
        var intel = Path.Combine(sys, "class", "drm", "card1");
        Write(Path.Combine(intel, "device", "vendor"), "0x8086\n");
        Write(Path.Combine(intel, "device", "device"), "0x8a5a\n");
        Write(Path.Combine(intel, "device", "uevent"), "DRIVER=i915\nPCI_SLOT_NAME=0000:00:02.0\n");
        Write(Path.Combine(intel, "gt_act_freq_mhz"), "850\n");
        Write(Path.Combine(intel, "gt", "gt0", "rc6_residency_ms"), "10000\n");
        Write(Path.Combine(machine, "usr", "share", "hwdata", "pci.ids"),
            "# comment\n1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t73ff  Navi 23 [Radeon RX 6600/6600 XT/6600M]\n\t\t1458 2335  subsystem\n" +
            "10de  NVIDIA Corporation\n8086  Intel Corporation\n\t8a52  Iris Plus Graphics G7\n\t8a5a  Iris Plus Graphics G4 (Ice Lake)\n");
        var seconds = 100.0;
        using var sampler = new HardwareSampler(machine, () => seconds);
        var first = sampler.Sample();
        double? Value(MonitoringReading[] r, string id) => r.FirstOrDefault(x => x.Identity == id)?.Value;
        Check(Value(first, "/cpu/0/temperature") == 61 && Value(first, "/cpu/0/clock") == 3600, "CPU temperature uses coretemp, not ACPI zones, and clock is the fastest core");
        Check(Value(first, "/cpu/0/power") is null && first.Single(r => r.Identity == "/cpu/0/power").State == ReadingState.Unavailable, "CPU power waits for a second energy reading");
        Check(first.Count(r => r.Identity.StartsWith("/gpu-", StringComparison.Ordinal) && r.Identity.EndsWith("/load", StringComparison.Ordinal)) == 2, "Connector entries such as card0-DP-1 are not GPUs");
        var amdName = first.First(r => r.Identity == "/gpu-amd/0/load").Metric;
        Check(amdName == "AMD Radeon RX 6600/6600 XT/6600M / GPU Core", "GPU names come from pci.ids, preferring the bracketed product name: " + amdName);
        Check(first.First(r => r.Identity == "/gpu-intel/1/load").Metric == "Intel Iris Plus Graphics G4 (Ice Lake) / GPU Core", "Intel names come from pci.ids too");
        Check(Value(first, "/gpu-amd/0/load") == 37 && Value(first, "/gpu-amd/0/temperature") == 54 && Value(first, "/gpu-amd/0/clock") == 2450 && Value(first, "/gpu-amd/0/power") == 132,
            "amdgpu reports busy percent, edge temperature, core clock and average power");
        Check(Value(first, "/gpu-amd/0/memory-used") == 2048 && Value(first, "/gpu-amd/0/memory-total") == 8192, "amdgpu reports VRAM in MB");
        Check(Value(first, "/gpu-intel/1/clock") == 850 && Value(first, "/gpu-intel/1/load") is null, "i915 reports its actual frequency; load needs a second RC6 reading");
        Check(first.First(r => r.Identity == "/gpu-intel/1/memory-used").Reason?.Contains("共用", StringComparison.Ordinal) == true, "An integrated GPU explains why it has no VRAM reading");
        seconds = 102;
        if (raplFixture) Write(Path.Combine(rapl, "energy_uj"), "31000000\n");
        Write(Path.Combine(intel, "gt", "gt0", "rc6_residency_ms"), "11500\n");
        var second = sampler.Sample();
        if (raplFixture) Check(Value(second, "/cpu/0/power") is { } watts && Math.Abs(watts - 15) < .001, "CPU package power is the RAPL energy change per second");
        Check(Value(second, "/gpu-intel/1/load") is { } busy && Math.Abs(busy - 25) < .001 && second.First(r => r.Identity == "/gpu-intel/1/load").Reason?.Contains("RC6", StringComparison.Ordinal) == true,
            "Intel load is estimated from time outside RC6 and labelled as an estimate");
        seconds = 104;
        if (raplFixture)
        {
            Write(Path.Combine(rapl, "energy_uj"), "1000000\n");
            Check(Value(sampler.Sample(), "/cpu/0/power") is { } wrapped && wrapped > 0, "A wrapped RAPL counter still gives a positive power");
        }
        if (OperatingSystem.IsLinux() && Environment.UserName != "root")
        {
            File.SetUnixFileMode(Path.Combine(rapl, "energy_uj"), UnixFileMode.None);
            var denied = sampler.Sample().Single(r => r.Identity == "/cpu/0/power");
            Check(denied.Value is null && denied.Reason?.Contains("root", StringComparison.Ordinal) == true, "Unreadable RAPL energy is explained as needing root");
            File.SetUnixFileMode(Path.Combine(rapl, "energy_uj"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var empty = new HardwareSampler(Path.Combine(root, "nothing"));
        var none = empty.Sample();
        Check(none.Length == 3 && none.All(r => r.Value is null && r.Reason is not null), "A machine without sensors gives explained empty CPU readings and no GPUs");
    }

    private static string Fdinfo(string driver, int client, string pdev, string engines) =>
        $"pos:\t0\nflags:\t02104002\ndrm-driver:\t{driver}\ndrm-client-id:\t{client}\ndrm-pdev:\t{pdev}\ndrm-total-system0:\t449568 KiB\n{engines}";

    // fdinfo needs symbolic links to /dev/dri, which only the Linux test run can create.
    private static void TestDrmUsage(string root)
    {
        if (!OperatingSystem.IsLinux()) return;
        var proc = Path.Combine(root, "proc");
        void Descriptor(int pid, int fd, string target, string info)
        {
            Directory.CreateDirectory(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "fd"));
            var link = Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "fd", fd.ToString(CultureInfo.InvariantCulture));
            if (!File.Exists(link) && new FileInfo(link).LinkTarget is null) File.CreateSymbolicLink(link, target);
            Write(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "fdinfo", fd.ToString(CultureInfo.InvariantCulture)), info);
        }
        void Clients(long render, long video, long cycles, long total)
        {
            var desktop = Fdinfo("i915", 10, "0000:00:02.0", $"drm-engine-render:\t{render} ns\ndrm-engine-copy:\t0 ns\ndrm-engine-video:\t0 ns\ndrm-engine-capacity-video:\t2\n");
            Descriptor(100, 5, "/dev/dri/card1", desktop);
            Descriptor(100, 6, "/dev/dri/card1", desktop);
            Descriptor(200, 3, "/dev/dri/renderD128", Fdinfo("i915", 11, "0000:00:02.0", $"drm-engine-render:\t0 ns\ndrm-engine-video:\t{video} ns\ndrm-engine-capacity-video:\t2\n"));
            Descriptor(300, 4, "/dev/dri/renderD129", Fdinfo("xe", 3, "0000:03:00.0", $"drm-cycles-rcs:\t{cycles}\ndrm-total-cycles-rcs:\t{total}\n"));
            Descriptor(400, 1, "/dev/null", "pos:\t0\n");
        }
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        Clients(1_000_000_000, 0, 1000, 100000);
        var usage = new DrmUsage(root);
        var first = usage.Sample(100);
        Check(first.Count == 2 && first.Values.All(v => v is null), "DRM clients are grouped by PCI device and need a second sample");
        Clients(2_000_000_000, 3_200_000_000, 41000, 200000);
        var second = usage.Sample(102);
        Check(second["0000:00:02.0"] is { } intel && Math.Abs(intel - 80) < .001, $"The busiest engine wins, a shared client counts once and engine capacity divides the time ({second["0000:00:02.0"]})");
        Check(second["0000:03:00.0"] is { } xe && Math.Abs(xe - 40) < .001, "xe cycle counters are measured against the GPU timestamp");
        var card = Path.Combine(root, "sys", "class", "drm", "card1");
        Write(Path.Combine(card, "device", "vendor"), "0x8086\n");
        Write(Path.Combine(card, "device", "device"), "0x8a5a\n");
        Write(Path.Combine(card, "device", "uevent"), "DRIVER=i915\nPCI_SLOT_NAME=0000:00:02.0\n");
        Write(Path.Combine(card, "gt", "gt0", "rc6_residency_ms"), "10000\n");
        var seconds = 200.0;
        using var sampler = new HardwareSampler(root, () => seconds);
        sampler.Sample();
        seconds = 202;
        Clients(3_000_000_000, 3_200_000_000, 41000, 200000);
        var load = sampler.Sample().Single(r => r.Identity == "/gpu-intel/1/load");
        Check(load.Value is { } busy && Math.Abs(busy - 50) < .001 && load.Reason == "DRM 用戶端最忙引擎", "Intel load uses DRM engine time instead of the RC6 estimate when clients report it");
    }

    private const string Header = "os,cpu,gpu,ram,kernel,driver,cpuscheduler\nUbuntu 26.04,Intel Core i5-1035G4,Iris Plus,8 GB,6.17,Mesa 25,\n" +
        "fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,elapsed\n";
    private static string Rows(int count, double frametime, double startMs, double stepMs) =>
        string.Concat(Enumerable.Range(0, count).Select(i => string.Create(CultureInfo.InvariantCulture,
            $"{1000 / frametime:0.0},{frametime:0.000},12,0,40,55,0,0,0,0,0,3.2,0,0.5,{(long)((startMs + i * stepMs) * 1e6)}\n")));

    private static void TestMangoHudCsv()
    {
        var csv = new MangoHudCsv();
        foreach (var line in Header.Split('\n')) Check(csv.Parse(line, "game") is null, "Header and system-information lines are not frames: " + (line.Length > 20 ? line[..20] : line));
        Check(csv.HasHeader, "The frame header is recognised by column names");
        var frames = Rows(30, 16.667, 0, 16.667).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => csv.Parse(l, "game")).ToArray();
        Check(frames.All(f => f is not null) && frames[1]!.StartMilliseconds == 16.667 && frames[1]!.CpuFrameMilliseconds == 16.667 && frames[0]!.SwapChain == "game",
            "Rows become frames timed by elapsed nanoseconds with their frame time");
        Check(csv.PerFrame == true, "Rows one frame apart are recognised as a per-frame log");
        var sampled = new MangoHudCsv();
        foreach (var line in (Header + Rows(30, 16.667, 0, 100)).Split('\n')) sampled.Parse(line, "game");
        Check(sampled.PerFrame == false, "Rows 100 ms apart are recognised as an interval log");
        Check(csv.Parse("garbage,row", "game") is null && csv.Parse("60,-1,0,0,0,0,0,0,0,0,0,0,0,0,5", "game") is null, "Short or invalid rows are skipped");
        var reordered = new MangoHudCsv();
        reordered.Parse("elapsed,fps,frametime", "x");
        Check(reordered.Parse("2000000,60,8.5", "x") is { StartMilliseconds: 2, CpuFrameMilliseconds: 8.5 }, "Columns are found by name, not position");
    }

    private static void TestMangoHudLog(string folder)
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        Check(MangoHudLog.GameName("/x/Cyberpunk2077_2026-10-18_12-00-01.csv") == "Cyberpunk2077" && MangoHudLog.GameName("vkcube.csv") == "vkcube", "Game names drop MangoHud's timestamp suffix");
        var data = OperatingSystem.IsWindows() ? @"C:\data" : "/data";
        Check(MangoHudLog.DefaultFolder(v => v == "XDG_DATA_HOME" ? data : null, "/home/u") == Path.Combine(data, "secondary", "mangohud")
            && MangoHudLog.DefaultFolder(_ => null, "/home/u") == Path.Combine("/home/u", ".local", "share", "secondary", "mangohud"), "The log folder follows XDG_DATA_HOME");
        Check(MangoHudLog.LaunchOption("/home/u/.local/share/secondary/mangohud") == "MANGOHUD_CONFIG=\"output_folder=/home/u/.local/share/secondary/mangohud,log_interval=0,autostart_log=1,no_display\" mangohud %command%",
            "The launch option logs every frame into Secondary's folder without an in-game overlay");
        var log = new MangoHudLog(folder, () => now);
        Check(log.Poll(TimeSpan.FromSeconds(30)) == 0 && log.Source is null, "A missing folder simply waits");
        var old = Write(Path.Combine(folder, "oldgame_2026-10-17_10-00-00.csv"), Header + Rows(50, 10, 0, 10));
        File.SetLastWriteTimeUtc(old, now.AddHours(-20));
        Write(Path.Combine(folder, "game_2026-10-18_11-59-00_summary.csv"), "summary\n");
        Check(log.Poll(TimeSpan.FromSeconds(30)) == 0 && log.Source is null, "Finished logs and summaries are not followed");
        var current = Path.Combine(folder, "vkcube_2026-10-18_11-59-58.csv");
        Write(current, Header + Rows(120, 16.667, 0, 16.667) + "60.0,16.6");
        File.SetLastWriteTimeUtc(current, now);
        Check(log.Poll(TimeSpan.FromSeconds(30)) == 120 && log.Source == "vkcube" && log.Generation == 1, "The newest active log is followed; a half-written last line waits");
        File.AppendAllText(current, "67,0,0,0,0,0,0,0,0,0,0,0,0," + ((long)(120 * 16.667 * 1e6)).ToString(CultureInfo.InvariantCulture) + "\n" + Rows(60, 16.667, 121 * 16.667, 16.667));
        Check(log.Poll(TimeSpan.FromSeconds(30)) == 61, "Appended rows are read, completing the partial line");
        var statistics = log.Window.Read();
        Check(statistics.Fps is { } fps && Math.Abs(fps - 60) < .5 && statistics.LowOnePercent is { } low && Math.Abs(low - 60) < .5 && statistics.SwapChain == "vkcube",
            $"FPS and 1% low come from the shared frame window ({statistics.Fps}, {statistics.LowOnePercent})");
        File.WriteAllText(current, Header + Rows(5, 8, 0, 8));
        // Keep file times on the test clock; the real clock must not decide which log is newest.
        File.SetLastWriteTimeUtc(current, now);
        Check(log.Poll(TimeSpan.FromSeconds(30)) == 5 && log.Generation == 2, "A truncated log restarts from its header");
        // A long log is read from its tail, not from the first frame.
        var big = Path.Combine(folder, "long_2026-10-18_12-00-00.csv");
        using (var writer = new StreamWriter(big))
        {
            writer.Write(Header);
            for (var block = 0; block < 100; block++) writer.Write(Rows(1000, 16.667, block * 1000 * 16.667, 16.667));
        }
        Check(new FileInfo(big).Length > MangoHudLog.TailBytes, "The long fixture is larger than the tail window");
        File.SetLastWriteTimeUtc(big, now.AddSeconds(1));
        now = now.AddSeconds(3);
        var added = log.Poll(TimeSpan.FromSeconds(30));
        Check(log.Source == "long" && added > 1000 && added < 100000, $"A long log is read from its recent tail ({added} rows)");
    }

    private static void TestDashboard(string root, string? output)
    {
        var machine = Path.Combine(root, "machine");
        var proc = Path.Combine(machine, "proc");
        Write(Path.Combine(proc, "meminfo"), Meminfo);
        Write(Path.Combine(proc, "stat"), Stat(100, 100, 700, 100));
        Write(Path.Combine(proc, "net", "dev"), Net(0, 0));
        var logs = Path.Combine(root, "dashboard-logs");
        var store = new SettingsStore(Path.Combine(root, "game-settings.json"));
        var model = new ShellModel(store);
        model.SaveRuntime(highSampling: true);
        var window = new MainWindow(model) { Width = 1400, Height = 1000, TelemetryRoot = machine, MangoHudFolder = logs };
        window.Show(); Pump();
        window.SetAccessibility(false, false);
        window.ToggleGame(); Pump();
        Check(!window.TransitionRunning, "The entrance animation is skipped when the system turns animations off");
        Check(window.GameMonitoringActive && window.FindControl<ComboBox>("SamplingPicker")!.SelectedIndex == 1, "Entering game mode starts sampling with the saved high rate");
        Check(Directory.Exists(logs) && (window.FindControl<TextBox>("MangoHudOption")!.Text ?? "").Contains("output_folder=" + logs, StringComparison.Ordinal), "The MangoHud folder is created and the launch option points at it");
        Check(!window.CompactGameLayout, "A tall window shows the full dashboard");
        var game = Path.Combine(logs, "vkcube_2026-10-18_12-00-00.csv");
        Write(game, Header + Rows(240, 16.667, 0, 16.667));
        Write(Path.Combine(proc, "stat"), Stat(200, 200, 1400, 200));
        Write(Path.Combine(proc, "net", "dev"), Net(2097152, 1024));
        string Text(string name) => window.FindControl<TextBlock>(name)!.Text ?? "";
        for (var i = 0; i < 400 && !(Text("CpuValue").EndsWith('%') && Text("FpsValue") != "—" && Text("GpuDetails").Contains("Radeon", StringComparison.Ordinal)); i++) { Thread.Sleep(10); Pump(); window.TickGame(); }
        Check(Text("CpuValue").EndsWith('%') && Text("MemoryValue") == "75%" && Text("MemoryDetails").Contains("/", StringComparison.Ordinal), $"CPU and memory gauges show live values ({Text("CpuValue")}, {Text("MemoryValue")})");
        Check(Text("FpsValue") == "60" && Text("LowValue") == "60" && Text("FrameValue").StartsWith("16.67", StringComparison.Ordinal), $"FPS, 1% low and frame time come from the MangoHud log ({Text("FpsValue")}, {Text("LowValue")}, {Text("FrameValue")})");
        Check(Text("GpuValue") == "37%" && Text("GpuDetails").Contains("54.0 °C", StringComparison.Ordinal) && Text("VramValue") == "25%", "The GPU gauge, details and VRAM show the selected card");
        var picker = window.FindControl<ComboBox>("GameGpuPicker")!;
        Check(picker.IsVisible && picker.ItemCount == 2, "With two GPUs a picker chooses which one the gauges show");
        picker.SelectedIndex = 1; Pump();
        for (var i = 0; i < 150 && !Text("GpuDetails").Contains("Iris", StringComparison.Ordinal); i++) { Thread.Sleep(10); Pump(); window.TickGame(); }
        Check(Text("GpuDetails").Contains("Iris", StringComparison.Ordinal) && Text("VramValue") == "—" && Text("VramCaption").Contains("共用", StringComparison.Ordinal), "Switching GPU shows the integrated card and explains its missing VRAM");
        Check(Text("GameNotice").Contains("MangoHud · vkcube", StringComparison.Ordinal) && Text("GameSourceState") == "LIVE / HIGH", "The notice names the game and the state reads LIVE");
        Check(window.FindControl<TelemetryTrace>("FrameTrace")!.Points.Any(p => p.Value is not null), "The frame pacing curve receives points");
        if (output is not null) { for (var i = 0; i < 20; i++) Pump(); window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-game.png"), PngBitmapEncoderOptions.Default); }
        // Without new rows the frame rate goes stale instead of freezing on the last value.
        for (var i = 0; i < 400 && Text("FpsValue") != "—"; i++) { Thread.Sleep(10); Pump(); window.TickGame(); }
        Check(Text("FpsValue") == "—" && window.LastGameSnapshot?.FrameState == TelemetrySourceState.Stale && Text("GameNotice").Contains("停止更新", StringComparison.Ordinal), "A log that stops growing shows — and says the game may have ended");
        window.Navigate("settings"); Pump();
        Check(!window.GameMonitoringActive && Text("GameSourceState") == "PAUSED", "Opening settings stops sampling");
        window.Navigate("game"); Pump();
        Check(window.GameMonitoringActive && Text("CpuValue") == "—", "Returning starts a new baseline instead of showing old values");
        window.SetSessionLocked(true); Pump();
        Check(!window.GameMonitoringActive, "Locking the session stops sampling");
        window.SetSessionLocked(false); window.SetSuspended(true); Pump();
        Check(!window.GameMonitoringActive, "Sleeping stops sampling");
        window.SetSuspended(false); Pump();
        Check(window.GameMonitoringActive, "Waking on the game page resumes sampling");
        window.Height = 820; Pump();
        Check(window.CompactGameLayout && window.FindControl<Expander>("CompactGameDetails")!.Content is Grid, "A short window moves details into an expander, as on Windows");
        if (output is not null) { for (var i = 0; i < 20; i++) Pump(); window.CaptureRenderedFrame()?.Save(Path.Combine(output, "linux-game-compact.png"), PngBitmapEncoderOptions.Default); }
        window.Height = 1000; Pump();
        Check(!window.CompactGameLayout && window.FindControl<Grid>("TelemetryLayout")!.Parent == window.FindControl<StackPanel>("GamePage"), "Growing the window restores the full layout");
        // The entrance animation plays only with animations on, and leaving game mode cancels it.
        window.ToggleGame(); Pump();
        window.SetAccessibility(false, true);
        window.ToggleGame(); Pump();
        Check(window.TransitionRunning, "With animations on, entering game mode plays the entrance animation");
        window.ToggleGame(); Pump();
        Check(!window.TransitionRunning && !window.GameMonitoringActive, "Leaving game mode cancels the animation and sampling");
        window.ToggleGame(); Pump();
        for (var i = 0; i < 300 && window.TransitionRunning; i++) { Thread.Sleep(10); Pump(); }
        Check(!window.TransitionRunning, "The entrance animation ends on its own");
        var completion = window.GameMonitoringCompletion;
        window.AllowClose = true; window.Close(); Pump();
        Check(completion.Wait(TimeSpan.FromSeconds(5)), "Closing the window stops the sampling workers");
        model.Dispose();
    }
}
