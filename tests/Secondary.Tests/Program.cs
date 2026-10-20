using Secondary.Core;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
var primary = new DisplayInfo("a", "Primary", true, 0, 0, 1920, 1080);
var separatePlacement = new WidgetPlacement(.2,.3,1,1.8,.6);
Check(System.Text.Json.JsonSerializer.Deserialize<WidgetPlacement>(System.Text.Json.JsonSerializer.Serialize(separatePlacement))!.Normalize() == separatePlacement, "Independent widget dimensions persist");
Check(new WidgetPlacement().Normalize() is { WidthScale: null, HeightScale: null }, "Legacy widgets preserve default proportional layout");
Check(new WidgetPlacement(WidthScale: double.NaN, HeightScale: 100).Normalize() is { WidthScale: 1, HeightScale: 3 }, "Independent dimensions reject invalid scales");
Check(new[] { "Todo", "Counter", "TimeProgress" }.All(HomeWidgetSettings.Supported), "Additional home widget kinds accepted");
var extendedWidget = new HomeWidgetSettings("todo", "Todo", Tasks: [new("  task  ", true)], Count: 12);
var extendedRoundTrip = System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(System.Text.Json.JsonSerializer.Serialize(extendedWidget))!.Normalize();
Check(extendedRoundTrip.Tasks![0] == new TodoItem("task", true) && extendedRoundTrip.Count == 12, "Tasks, completion and counter persist");
Check(new HomeWidgetSettings("bounded", "Todo", Tasks: Enumerable.Repeat(new TodoItem(new string('a', 200)), 30).ToArray(), Count: int.MaxValue).Normalize() is { Count: 999999, Tasks.Length: 20 } bounded && bounded.Tasks.All(t => t.Text.Length == 120), "Task and counter bounds enforced");
Check(new[] { "Media", "Weather", "EventCountdown" }.All(HomeWidgetSettings.Supported), "Media weather and event countdown kinds accepted");
var liveWidget = new HomeWidgetSettings("event", "EventCountdown", City: "  Taipei  ", EventTitle: "  旅行  ", EventDate: "2026-12-31");
Check(System.Text.Json.JsonSerializer.Deserialize<HomeWidgetSettings>(System.Text.Json.JsonSerializer.Serialize(liveWidget))!.Normalize() is { City: "Taipei", EventTitle: "旅行", EventDate: "2026-12-31" }, "Weather city and event date survive settings round trip");
Check(new HomeWidgetSettings("bad", "EventCountdown", EventDate: "2026-02-30").Normalize().EventDate == "", "Invalid event date is rejected");
var groupedShortcuts = new[] { new ShortcutEntry("a", "A", "工作", Steps: [new("Open", "https://example.com/a")]), new ShortcutEntry("b", "B", "遊戲", Steps: [new("Open", "https://example.com/b")]) };
Check(ShortcutPolicy.MoveToGroup(groupedShortcuts, "a", "遊戲", "b") is [{ Id: "a", Group: "遊戲" }, { Id: "b", Group: "遊戲" }], "Dragging a shortcut into another group updates its group");
var sensorReading = new MonitoringReading("GPU temperature", "LibreHardwareMonitor 0.9.6", "/gpu/temperature/0", DateTimeOffset.UtcNow, 42, "°C", ReadingState.Available);
var sensorJson = System.Text.Json.JsonSerializer.Serialize(new SensorSnapshot(1, [sensorReading]));
Check(SensorSnapshot.Parse(sensorJson).Readings[0].Value == 42, "Sensor snapshot round trip with identity");
Check(SensorSnapshot.Parse(System.Text.Json.JsonSerializer.Serialize(new SensorSnapshot(1, [sensorReading with { Value = 0 }]))).Readings[0].Value == 0, "Valid sensor zero remains available");
Check(SensorSnapshot.Parse(System.Text.Json.JsonSerializer.Serialize(new SensorSnapshot(1, [sensorReading with { Value = null, State = ReadingState.Unavailable, Reason = "no permission" }]))).Readings[0].Value is null, "Unavailable sensor remains null");
foreach (var invalid in new[] { sensorJson.Replace("\"Schema\":1", "\"Schema\":99"), "{\"Schema\":1,\"Readings\":null}", sensorJson.Replace("\"Value\":42", "\"Value\":null"), sensorJson.Replace("\"State\":0", "\"State\":2"), sensorJson.Replace("\"State\":0", "\"State\":999"), sensorJson.Replace("LibreHardwareMonitor 0.9.6", "unknown source"), new string('x', 512 * 1024 + 1), System.Text.Json.JsonSerializer.Serialize(new SensorSnapshot(1, Enumerable.Repeat(sensorReading, 513).ToArray())) })
{
    var rejected = false;
    try { SensorSnapshot.Parse(invalid); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Invalid or oversized sensor snapshot rejected");
}
var csv = new PresentMonCsv(42);
Check(csv.Read("42,0x1,1,10,6") is null, "PresentMon requires a header");
Check(csv.Read("ProcessID,SwapChainAddress,CPUStartTime,MsCPUBusy,MsCPUWait,Application") is null && csv.HasHeader, "PresentMon v2 header recognized");
Check(csv.Read("42,0x1,1,10,6,\"app,with comma.exe\"") is { CpuFrameMilliseconds: 16, ProcessId: 42 }, "PresentMon quoted CSV and CPU interval parsed");
Check(csv.Read("43,0x1,1,10,6,app") is null, "PresentMon rejects different PID");
foreach (var invalidFrame in new[] { "42,0x1,1,NA,6,app", "42,0x1,1,NaN,6,app", "42,0x1,1,-1,6,app", "42,0x1,1,0,0,app", "42,0x1,1,Infinity,6,app", "42,0x1,1,10,6,\"broken", "42,0x1,1,10", "42,,1,10,6,app" })
    Check(csv.Read(invalidFrame) is null, "PresentMon rejects malformed or unavailable frame: " + invalidFrame);
var reordered = new PresentMonCsv(42);
reordered.Read("MsCPUWait,CPUStartTime,ProcessID,SwapChainAddress,MsCPUBusy");
Check(reordered.Read("6,1.5,42,0x2,10") is { CpuFrameMilliseconds: 16, StartMilliseconds: 1500 }, "PresentMon default seconds converted to milliseconds");
var pinnedCsv = new PresentMonCsv(42);
pinnedCsv.Read("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,CPUStartQPCTime,FrameTime,CPUBusy,CPUWait");
Check(pinnedCsv.Read("game.exe,42,0x1,DXGI,0,0,1000,16.5,10,6.5") is { StartMilliseconds: 1000, CpuFrameMilliseconds: 16.5 }, "Actual pinned 2.6.0 v2 QPC schema parsed");
Check(pinnedCsv.Read("game.exe,42,0x1,DXGI,0,0,1000,NA,10,6.5") is null, "Unavailable v2 FrameTime does not fall back to fabricated interval");
var frameWindow = new FrameWindow(42);
Check(frameWindow.Read().Fps is null, "No frames is not zero FPS");
for (var i = 0; i < 99; i++) frameWindow.Add(new(42, "main", i * 10, 10));
Check(frameWindow.Read().Fps == 100 && frameWindow.Read().LowOnePercent is null, "One percent low waits for 100 frames");
frameWindow.Add(new(42, "main", 990, 100));
Check(frameWindow.Read().LowOnePercent == 10, "One percent low inverts the slowest one percent mean");
frameWindow.Add(new(42, "other", 991, 1));
Check(frameWindow.Read().SwapChain == "main" && frameWindow.Read().Count == 100, "Most active swapchain is not mixed with others");
var countBefore = frameWindow.Count;
frameWindow.Add(new(43, "main", 992, 10)); frameWindow.Add(new(42, "main", 100, 10)); frameWindow.Add(new(42, "main", 993, double.NaN));
Check(frameWindow.Count == countBefore + 1, "Valid out of order frames retained while foreign and invalid frames are rejected");
frameWindow.Add(new(42, "new", 62000, 20));
Check(frameWindow.Count == 1 && frameWindow.Read().LowOnePercent is null, "Rolling frame window expires older than 60 seconds");
var boundedFrames = new FrameWindow(42);
for (var i = 0; i <= 60000; i++) boundedFrames.Add(new(42, "a", i * .01, 1));
Check(boundedFrames.Count == 60000 && boundedFrames.Read().Truncated && boundedFrames.Read().LowOnePercent is null, "Frame memory is bounded and truncated low is unavailable");
var history = new TelemetryHistory();
for (var i = 0; i < 500; i++) history.Add(i, i == 499 ? null : i);
Check(history.Snapshot().Length == 61 && history.Snapshot()[^1].Value is null, "Trace window retains real timestamps and unavailable gaps");
Check(csv.Read(new string('x', 17000)) is null, "PresentMon rejects oversized row");
Check(new DiskUsage("C:", "NTFS", 1000, 250).Percent == 75, "Disk bar represents used rather than free capacity");
Check(new DiskUsage("C:", "NTFS", 1000, 1000).Percent == 0 && new DiskUsage("C:", "NTFS", 1000, 0).Percent == 100, "Empty and full disks");
Check(!new DiskUsage("C:", "", 0, 0).IsValid && !new DiskUsage("C:", "", 100, 200).IsValid, "Invalid disk capacity is not valid zero usage");
Check(SamplingPolicy.IntervalMs(true, false) == 3000 && SamplingPolicy.GpuIntervalSeconds(true, false) == 10, "Low game sampling intervals");
Check(SamplingPolicy.IntervalMs(true, true) == 1000 && SamplingPolicy.GpuIntervalSeconds(true, true) == 2, "High game sampling intervals");
Check(SamplingPolicy.IntervalMs(false, false) == 1000 && !new AppSettings().GameHighSampling, "Regular sampling unchanged and game defaults low");
Check(TelemetryMath.CpuPercent(null, new(20, 40, 60)) is null, "CPU requires baseline");
Check(TelemetryMath.CpuPercent(new(0, 0, 0), new(20, 40, 60)) == 80, "CPU kernel includes idle time");
Check(TelemetryMath.CpuPercent(new(20, 40, 60), new(20, 40, 60)) is null, "Zero interval unavailable");
Check(TelemetryMath.CpuPercent(new(20, 40, 60), new(0, 0, 0)) is null, "Counter reset rejected");
Check(TelemetryMath.CpuPercent(new(0, 0, 0), new(100, 100, 0)) == 0, "Idle CPU is a genuine zero");
Check(TelemetryMath.Rate(100, 300, 2) == 100, "Network uses elapsed monotonic interval");
Check(TelemetryMath.Rate(300, 100, 2) is null && TelemetryMath.Rate(0, 100, 0) is null, "Network reset and invalid interval rejected");
var secondary = new DisplayInfo("b", "Secondary", false, -2560, 0, 2560, 1440);
DisplayInfo[] displays = [primary, secondary];
Check(DisplayPolicy.FindSaved(displays, "b") == secondary, "Saved display resolves independent of position");
Check(DisplayPolicy.FindSaved([primary], "b") is null, "Disconnected saved display is not silently replaced");
Check(DisplayPolicy.Fallback([secondary, primary]) == primary, "Recovery chooses primary");
Check(DisplayPolicy.Fallback([]) is null, "No displays is safe");
var directory = Path.Combine(Path.GetTempPath(), "Secondary.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "settings.json");
    var store = new SettingsStore(path);
    Check(store.Load().DisplayId is null, "First run has no saved selection");
    Check(store.Save(new(DisplayId: "b")), "Selection saved: " + store.LastError);
    Check(new SettingsStore(path).Load().DisplayId == "b", "Selection persists across instances");
    Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Atomic save leaves no temporary file");
    File.WriteAllText(path, "invalid JSON");
    Check(store.Load().DisplayId is null && store.LastError is not null, "Corrupt settings recover with error");
    Check(File.ReadAllText(path) == "invalid JSON", "Load preserves corrupt original");
    File.WriteAllText(path, "{\"Version\":99}");
    Check(store.Load().DisplayId is null && store.LastError is not null, "Unsupported version reported");
    var invalid = new SettingsStore(directory);
    Check(!invalid.Save(new()), "Write failure reported without crashing");
    File.WriteAllText(path, "{\"Version\":1,\"DisplayId\":\"b\"}");
    var migrated = store.Load();
    Check(migrated.Version == 3 && migrated.DisplayId == "b", "Version 1 migration preserves display");
    Check(store.Save(migrated) && Directory.GetFiles(directory, "*.bak").Length == 1, "Migration backs up original before save");
    File.WriteAllText(path, "{\"Version\":2,\"Mode\":99,\"PreviousMode\":2,\"LightSection\":\"automation\",\"Theme\":null,\"Hotkey\":null}");
    var sanitized = store.Load();
    Check(sanitized.Mode == AppMode.Normal && sanitized.NormalSection == "home" && sanitized.Theme == "System" && sanitized.Hotkey == "Ctrl+Alt+G", "Invalid settings sanitized");
    foreach (var valid in new[] { "Ctrl+Alt+G", "alt+shift+f11", "Ctrl+0" })
        Check(HotkeyGesture.TryParse(valid, out _), "Valid hotkey " + valid);
    foreach (var rejected in new[] { "G", "Shift+G", "Win+G", "Ctrl+Ctrl+G", "Alt+F12", "Ctrl+", "Ctrl+F0" })
        Check(!HotkeyGesture.TryParse(rejected, out _), "Invalid hotkey " + rejected);
    var created = new Dictionary<string, TrackingModule>();
    var released = new List<int>();
    var conflicts = false;
    using (var binding = new HotkeyBinding((_, _) => !conflicts, id => released.Add(id)))
    {
        HotkeyGesture.TryParse("Ctrl+Alt+G", out var first);
        HotkeyGesture.TryParse("Ctrl+Alt+H", out var next);
        Check(binding.Set(first!) && binding.ActiveId != 0, "Hotkey registration succeeds");
        var oldId = binding.ActiveId;
        conflicts = true;
        Check(!binding.Set(next!) && binding.ActiveId == oldId && released.Count == 0, "Conflict preserves old registration");
        Check(binding.Set(first!), "Unchanged hotkey is idempotent");
        conflicts = false;
        Check(binding.Set(next!) && released.SequenceEqual(new[] { oldId }), "Replacement releases only preceding key");
    }
    Check(released.Count == 2, "Shutdown unregisters active shortcut");
    using (var modes = new ModeController(new(), key => created[key] = new TrackingModule()))
    {
        Check(created.Count == 1 && created["home"].Active, "Only home module created at startup");
        modes.Navigate("shortcuts");
        Check(!created["home"].Active && created["shortcuts"].Active, "Navigation suspends previous module");
        Check(!modes.Navigate("system") && !modes.Navigate("focus") && !modes.Navigate("settings"), "Removed sections and settings are not content routes");
        modes.Switch(AppMode.Game);
        Check(modes.Current == "game" && created.Where(p => p.Key != "game").All(p => p.Value.Disposed), "Game releases normal modules");
        Check(!modes.Navigate("home") && !modes.Navigate("shortcuts"), "Game content is isolated");
        modes.Switch(AppMode.Normal);
        Check(modes.Current == "shortcuts", "Game exit restores previous normal page");
        modes.Switch(AppMode.Game);
        Check(store.Save(modes.Snapshot(new()) with { Theme = "Dark", Hotkey = "Alt+F11", GameAnimation = false }), "Runtime settings saved");
    }
    Check(created.Values.All(m => m.Disposed), "Shutdown disposes modules");
    using var restored = new ModeController(store.Load());
    Check(restored.Mode == AppMode.Game && restored.Current == "game", "Game restores on restart");
    restored.Switch(AppMode.Normal);
    Check(restored.Current == "shortcuts" && store.Load().Theme == "Dark" && store.Load().Hotkey == "Alt+F11" && !store.Load().GameAnimation, "Restart preserves page and preferences");
    foreach (var version in new[] { 1, 2 })
    foreach (var mode in new[] { 0, 1, 2 })
    {
        var legacyPath = Path.Combine(directory, $"legacy-{version}-{mode}.json");
        var original = System.Text.Json.JsonSerializer.Serialize(new { Version = version, Mode = mode, DisplayId = "b", Theme = "Dark", Hotkey = "Alt+F11", GameHighSampling = true, FullSection = "system", LightSection = "media" });
        File.WriteAllText(legacyPath, original);
        var migrationStore = new SettingsStore(legacyPath);
        var updated = migrationStore.Load();
        Check(updated.Version == 3 && updated.NormalSection == "home" && updated.Mode == (mode == 2 ? AppMode.Game : AppMode.Normal), "Explicit legacy numeric mode migration");
        Check(updated.DisplayId == "b" && updated.Theme == "Dark" && updated.Hotkey == "Alt+F11" && updated.GameHighSampling, "Migration retains user preferences");
        Check(File.ReadAllText(legacyPath) == original && migrationStore.NeedsSave, "Migration does not overwrite before backup");
        Check(migrationStore.Save(updated), "Migrated settings saved");
        Check(File.ReadAllText(Directory.GetFiles(directory, $"legacy-{version}-{mode}.json.*.bak").Single()) == original, "Exact original backup retained");
        Check(!migrationStore.NeedsSave && migrationStore.Load() == updated, "Migration is idempotent");
    }
    Check(Enum.GetValues<AppMode>().SequenceEqual(new[] { AppMode.Normal, AppMode.Game }), "No light or full modes remain");
    var home = new HomeSettings("Flip", 1.3, "#65DCEC", .2, .8, false, true, false, "Video", @"C:\Wallpapers\loop.mp4");
    var widgets = new[] { new HomeWidgetSettings("note", "Note", new WidgetPlacement(.2,.7,.8), "便箋測試"), new HomeWidgetSettings("timer", "Countdown", Minutes: 25) };
    Check(store.Save(new AppSettings(Home: new HomeSettings(Widgets: widgets))) && store.Load().Home!.Widgets!.SequenceEqual(widgets.Select(w => w.Normalize())), "Utility widget contents and placement survive reload");
    var cleanWidgets = new HomeSettings(Widgets: [new("a","Note",Text:new string('x',5000),Minutes:0),new("a","Calendar"),new("b","Unknown")]).Normalize().Widgets!;
    Check(cleanWidgets.Length == 1 && cleanWidgets[0].Text.Length == 4000 && cleanWidgets[0].Minutes == 1, "Widget normalization removes duplicates and unsupported kinds and bounds content");
    Check(new HomeSettings(Widgets:Enumerable.Range(0,20).Select(i => new HomeWidgetSettings(i.ToString(),"Note")).ToArray()).Normalize().Widgets!.Length == 12, "Widget collection has bounded size");
    Check(new WidgetPlacement(-2,4,double.NaN).Normalize() == new WidgetPlacement(0,1,1), "Widget placement bounds and invalid scale normalize");
    Check(new HomeSettings(Size:3).Normalize().Size == 3 && new HomeSettings(Size:.3).Normalize().Size == .3, "Clock supports expanded widget scale range");
    foreach (var navSize in new[] { "Standard", "Small" })
    foreach (var navPosition in new[] { "Top", "Bottom", "Left", "Right" })
    {
        store.Save(new AppSettings(NavigationSize: navSize, NavigationPosition: navPosition));
        Check(store.Load() is var nav && nav.NavigationSize == navSize && nav.NavigationPosition == navPosition, "Navigation preferences round trip " + navSize + navPosition);
    }
    store.Save(new AppSettings(NavigationSize: "invalid", NavigationPosition: "invalid"));
    Check(store.Load() is { NavigationSize: "Standard", NavigationPosition: "Bottom" }, "Invalid navigation options use original defaults");
    Check(store.Save(new AppSettings(Home: home)) && store.Load().Home == home, "Home options survive settings round trip");
    Check(store.Save(new AppSettings()) && store.Load().Home is null, "Existing V3 without Home retains compatible defaults");
    var invalidHome = new HomeSettings("Bad", double.NaN, "invalid", -4, 3, Background: "Unknown", MediaPath: "https://example.com/test.mp4").Normalize();
    Check(invalidHome.ClockStyle == "Digital" && invalidHome.Size == 1 && invalidHome.X == 0 && invalidHome.Y == 1 && invalidHome.Color == "Auto" && invalidHome.Background == "Static" && invalidHome.MediaPath is null, "Home invalid options sanitize safely");
    Check(new HomeSettings(Background: "Gradient").Normalize().Background == "Static" && new HomeSettings(Background: "Aurora").Normalize().Background == "Static",
        "Removed animated backgrounds load as the static background");
    Check(!HomeSettings.IsLocalPath(@"\\server\share\video.mp4") && !HomeSettings.IsLocalPath("relative.png") && HomeSettings.IsLocalPath(@"D:\Pictures\背景.png"), "Background restricts sources to local drive paths");
    Check(HomeSettings.ValidColor("Auto") && HomeSettings.ValidColor("#aAbB00") && !HomeSettings.ValidColor("#123") && !HomeSettings.ValidColor(null), "Clock color validation");
    var midnight = new DateTime(2026, 9, 15, 0, 0, 0);
    var shortcuts = new ShortcutLibrary(Path.Combine(directory,"shortcuts.json"));
    var link = new ShortcutEntry("a","網站",Steps:[new("Open","https://example.com")]);
    var folder = new ShortcutEntry("b","資料夾","工作",Steps:[new("Open",@"C:\Work")]);
    var approvedLink = link with { ApprovedFingerprint = ShortcutApproval.Fingerprint(link,"review") };
    Check(!ShortcutApproval.IsApproved(link,"review") && ShortcutApproval.IsApproved(approvedLink,"review"), "Only explicitly confirmed shortcut skips first-run confirmation");
    Check(ShortcutApproval.IsApproved(approvedLink with { Name = "改名", Group = "其他", Icon = "★" },"review"), "Cosmetic changes preserve approval");
    Check(!ShortcutApproval.IsApproved(approvedLink with { Id = "copy" },"review"), "Copied shortcut needs independent approval");
    Check(!ShortcutApproval.IsApproved(approvedLink with { Steps = [new("Open","https://example.org")] },"review"), "Changed execution step invalidates approval");
    Check(!ShortcutApproval.IsApproved(approvedLink,"changed script hash"), "Changed script review invalidates approval");
    Check(shortcuts.Save([approvedLink]) && ShortcutApproval.IsApproved(shortcuts.Load().Single(),"review"), "Approval survives application restart");
    Check(shortcuts.Save([link,folder]), "Shortcut library saves atomically");
    Check(shortcuts.Load().Select(e => e.Id).SequenceEqual(new[] { "a","b" }), "Shortcut order persists");
    Check(ShortcutPolicy.Filter([link,folder],"資料","工作").Single().Id == "b", "Shortcut search combines group and text");
    Check(ShortcutPolicy.Move([link,folder],"b","a")[0].Id == "b", "Shortcut drag reorder changes persisted order");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open","javascript:alert(1)")] }) is not null, "Shortcut blocks non HTTP schemes");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open",@"C:\x.ps1")] }) is not null, "Script cannot bypass explicit script step");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Script",@"C:\x.ps1")] }) is null, "Explicit script step is supported");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Wait",Seconds:0)] }) is not null, "Macro wait duration bounded");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Keys","Ctrl+S",ProcessName:"notepad",WindowTitle:"Document")] }) is null, "Keys require a precise named target");
    // Linux rules: .sh scripts, arguments for executables and AppImages, no key sending under Wayland.
    var linux = ShortcutRules.Linux;
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Script",@"/tools/backup.sh")] }, linux) is null && ShortcutPolicy.Validate(link with { Steps = [new("Script",@"/x.ps1")] }, linux) is not null,
        "Linux scripts are .sh only");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/tools/backup.sh")] }, linux) is not null && ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/tools/run.py")] }, linux) is not null,
        "Linux scripts cannot bypass the script step");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/opt/game/launcher","--windowed")] }, linux) is null && ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/apps/Tool.AppImage","-v")] }, linux) is null,
        "Linux passes arguments to executables and AppImages");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/docs/note.txt","-x")] }, linux) is not null && ShortcutPolicy.Validate(link with { Steps = [new("Open",@"/apps/tool.exe","-x")] }, linux) is not null,
        "Linux refuses arguments for documents and Windows programs");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Keys","Ctrl+S",ProcessName:"gedit",WindowTitle:"Document")] }, linux) is { } keys && keys.Contains("Wayland"),
        "Linux explains that window key steps are unavailable");
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Open",@"C:\apps\tool.exe","-x")] }) is null, "Windows rules are unchanged by default");
    var desktopDirectory = Path.Combine(Path.GetTempPath(), "Secondary.Desktop-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(desktopDirectory);
    try
    {
        var desktopFile = Path.Combine(desktopDirectory, "org.example.Editor.desktop");
        File.WriteAllText(desktopFile, "[Desktop Entry]\nType=Application\nName=Text Editor\nName[zh_CN]=文本编辑器\nName[zh_TW]=文字編輯器\nExec=editor %U\n\n[Desktop Action New]\nName=Other\n");
        var imported = ShortcutImport.FromPath(desktopFile, "日常", linux);
        Check(imported.Name == "文字編輯器" && imported.Steps![0] is { Kind: "Open" } && imported.Steps[0].Target == desktopFile, "Linux imports a .desktop launcher under its Traditional Chinese name");
        var script = Path.Combine(desktopDirectory, "backup.sh"); File.WriteAllText(script, "echo ok\n");
        Check(ShortcutImport.FromPath(script, "日常", linux).Steps![0].Kind == "Script", "Dropped .sh files become script steps on Linux");
        File.WriteAllText(desktopFile, "[Desktop Entry]\nType=Application\nExec=editor\n");
        Check(ShortcutImport.FromPath(desktopFile, "日常", linux).Name == "org.example.Editor", "A launcher without a name falls back to its file name");
    }
    finally { Directory.Delete(desktopDirectory, true); }
    Check(ShortcutPolicy.Validate(link with { Steps = [new("Keys","Ctrl+S")] }) is not null, "Keys without window identity rejected");
    var fakeExecutor = new FakeShortcutExecutor(); var macroRunner = new ShortcutRunner(fakeExecutor);
    var macro = link with { Steps = [new("Open","https://example.com/1"),new("Open","https://example.com/2")] };
    await macroRunner.Run(macro,_ => {},CancellationToken.None);
    Check(fakeExecutor.Calls.Count == 2 && fakeExecutor.Calls[0].Target.EndsWith("/1") && !macroRunner.IsRunning, "Macro executes in order without launching real processes in tests");
    fakeExecutor.Calls.Clear(); fakeExecutor.Fail = true;
    try { await macroRunner.Run(macro,_ => {},CancellationToken.None); } catch (IOException) { }
    Check(fakeExecutor.Calls.Count == 1 && !macroRunner.IsRunning, "Macro fails fast and resets running state");
    fakeExecutor.Fail = false; fakeExecutor.Block = true; fakeExecutor.Calls.Clear();
    using (var cancel = new CancellationTokenSource())
    {
        var running = macroRunner.Run(macro,_ => {},cancel.Token);
        var rejected = false;
        try { await macroRunner.Run(macro,_ => {},CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Only one macro can run at a time");
        cancel.Cancel(); try { await running; } catch (OperationCanceledException) { }
        Check(fakeExecutor.Calls.Count == 1 && !macroRunner.IsRunning, "Cancellation prevents all later steps");
    }
    File.WriteAllText(Path.Combine(directory,"shortcuts.json"),"not json");
    Check(shortcuts.Load().Length == 0 && shortcuts.ReadOnly && !shortcuts.Save([link]) && File.ReadAllText(Path.Combine(directory,"shortcuts.json")) == "not json", "Corrupt shortcut library is preserved rather than overwritten");
    Check(ClockTextLayout.Centers("11:11:11",100,40).SequenceEqual(ClockTextLayout.Centers("08:08:08",100,40)), "Clock digit and separator slots never shift with numeral widths");
    Check(ClockTextLayout.Centers("23:59",100,40).SequenceEqual(ClockTextLayout.Centers("00:00",100,40)), "Clock slots remain stable across midnight without seconds");
    Check(ClockTextLayout.Centers("11",100,40).SequenceEqual(new[] { -50d,50d }), "Flip clock pairs use centered fixed-width digit slots");
    Check(new HomeSettings().TimeText(midnight) == "00:00" && home.TimeText(midnight) == "12:00:00", "Clock midnight 12 and 24 hour formats");
    Check(home.TimeText(midnight.AddHours(12)) == "12:00:00" && home.TimeText(midnight.AddHours(23).AddMinutes(59).AddSeconds(59)) == "11:59:59", "Clock noon and final second formats");

}
finally { Directory.Delete(directory, true); }
DataOptimizationChecks.Run(Check);
ConnectionChecks.Run(Check);
RemoteGameChecks.Run(Check);
RemoteDeviceChecks.Run(Check);
ShortcutTransferChecks.Run(Check);
AiUsageChecks.Run(Check);
Console.WriteLine($"{passed} tests passed.");

sealed class TrackingModule : ISectionModule
{
    public bool Active { get; private set; }
    public bool Disposed { get; private set; }
    public void Activate() { if (Disposed) throw new ObjectDisposedException(nameof(TrackingModule)); Active = true; }
    public void Suspend() => Active = false;
    public void Dispose() { Active = false; Disposed = true; }
}

sealed class FakeShortcutExecutor : IShortcutExecutor
{
    public List<ShortcutStep> Calls { get; } = [];
    public bool Fail { get; set; }
    public bool Block { get; set; }
    public async Task Execute(ShortcutStep step,CancellationToken cancellation)
    {
        Calls.Add(step);
        if (Fail) throw new IOException("Injected failure");
        if (Block) await Task.Delay(Timeout.Infinite,cancellation);
    }
}
