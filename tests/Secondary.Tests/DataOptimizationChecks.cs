using System.Text.Json;
using Secondary.Core;

internal static class DataOptimizationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Secondary.DataChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "occupies the directory path");
            var store = new SettingsStore(Path.Combine(blocked, "settings.json"));
            var desired = new AppSettings(Theme: "Dark");
            check(!store.Save(desired) && store.NeedsSave, "Failed settings save retains a retry obligation");
            File.Delete(blocked); Directory.CreateDirectory(blocked);
            check(store.Save(desired) && !store.NeedsSave && store.LastError is null, "Identical settings retry succeeds after the path is repaired");
            check(new SettingsStore(Path.Combine(blocked, "settings.json")).Load().Theme == "Dark", "Retry persists the intended settings across restart");
            check(!store.Save(new AppSettings(Theme: new string('x', SettingsStore.MaximumBytes))) && store.NeedsSave && store.LastError!.Contains("過大") &&
                new SettingsStore(Path.Combine(blocked, "settings.json")).Load().Theme == "Dark", "Oversized settings save reports failure and preserves the previous readable settings");
            check(store.Save(desired) && !store.NeedsSave, "Settings save can recover after a total-size validation failure");

            var settingsPath = Path.Combine(directory, "oversized-settings.json");
            using (var file = File.Create(settingsPath)) file.SetLength(SettingsStore.MaximumBytes + 1L);
            var bounded = new SettingsStore(settingsPath);
            check(bounded.Load().Home is null && bounded.LastError!.Contains("過大") && new FileInfo(settingsPath).Length == SettingsStore.MaximumBytes + 1L,
                "Oversized settings are bounded before JSON parsing and preserved");
            File.WriteAllText(settingsPath, "{\"Version\":3,\"Unknown\":" + new string('[', 40) + "0" + new string(']', 40) + "}");
            check(bounded.Load().Theme == "System" && bounded.LastError is not null, "Settings JSON nesting has a finite depth limit");

            var shortcutPath = Path.Combine(directory, "shortcuts.json");
            var library = new ShortcutLibrary(shortcutPath);
            var original = new[] { new ShortcutEntry("original", "原始", Steps: [new("Open", "https://example.com")]) };
            check(library.Save(original), "Initial shortcut document is readable before size rejection");
            var previousBytes = File.ReadAllBytes(shortcutPath);
            var large = Enumerable.Range(0, 60).Select(i => new ShortcutEntry(i.ToString(), "Boundary " + i,
                Steps: Enumerable.Repeat(new ShortcutStep("Open", @"C:\Game.exe", new string('a', 4096)), 20).ToArray())).ToArray();
            check(large.All(e => ShortcutPolicy.Validate(e) is null) && !library.Save(large) && library.Error!.Contains("4 MB"),
                "Individually valid shortcuts cannot save an unreadable total document");
            check(previousBytes.SequenceEqual(File.ReadAllBytes(shortcutPath)) && new ShortcutLibrary(shortcutPath).Load().Single().Id == "original",
                "Oversized shortcut save preserves the previous readable file");

            var left = 0; var right = 4096;
            var options = new JsonSerializerOptions { WriteIndented = true };
            ShortcutEntry[] At(int length) => large.Select(e => e with
            { Steps = Enumerable.Repeat(new ShortcutStep("Open", @"C:\Game.exe", new string('a', length)), 20).ToArray() }).ToArray();
            while (left < right)
            {
                var middle = (left + right + 1) / 2;
                if (JsonSerializer.SerializeToUtf8Bytes(new ShortcutDocument(Entries: At(middle)), options).Length <= ShortcutLibrary.MaximumBytes) left = middle;
                else right = middle - 1;
            }
            var limitEntries = At(left);
            check(library.Save(limitEntries) && new ShortcutLibrary(shortcutPath).Load().Length == limitEntries.Length,
                "Largest fixture below the shared UTF8 limit round trips");
            check(!library.Save(At(left + 1)) && new ShortcutLibrary(shortcutPath).Load().Length == limitEntries.Length,
                "Next fixture above the UTF8 limit is rejected without damage");
            var escaped = new[] { new ShortcutEntry("unicode", "中文捷徑", Steps: [new("Open", @"C:\Game.exe", "引號\"及反斜線\\\n字元")]) };
            check(library.Save(escaped) && new ShortcutLibrary(shortcutPath).Load().Single().Steps![0].Arguments == escaped[0].Steps![0].Arguments,
                "Multibyte and escaped shortcut content shares save and load representation");

            var frames = new FrameWindow(42);
            for (var i = 0; i < 1000; i++) frames.Add(new(42, "old", i, 1));
            check(frames.Read().SwapChain == "old", "Initial dense chain is selected");
            for (var i = 0; i < 210; i++) frames.Add(new(42, "new", 1200 + i * 10, 10));
            var switched = frames.Read();
            check(switched.SwapChain == "new" && switched.Fps == 100 && switched.LowOnePercent == 100 && switched.Count == 210,
                "Stopped historical chain yields to the active chain without inheriting its low statistic");
            check(switched.LastArrivalTimestamp > 0 && ReferenceEquals(switched, frames.Read()), "Selected-chain arrival freshness and unchanged statistics are cached");
            for (var i = 0; i < 300; i++) frames.Add(new(42, "late-old", i, 100));
            check(frames.Read().SwapChain == "new", "Delayed old-chain events do not replace a currently active chain");

            var simultaneous = new FrameWindow(42);
            for (var i = 0; i < 300; i++)
            {
                simultaneous.Add(new(42, "primary", i * 10, 10));
                if (i % 2 == 0) simultaneous.Add(new(42, "secondary", Math.Max(0, i * 10 - 20), 20));
                if (i % 10 == 0) check(simultaneous.Read().SwapChain == "primary", "Simultaneous chains stay separate and do not jitter between sources");
            }
            var varying = new FrameWindow(42);
            var values = Enumerable.Range(0, 1201).Select(i => (double)(1 + (i * 37 % 231))).ToArray();
            for (var i = 0; i < values.Length; i++) varying.Add(new(42, "fixture", i * 10, values[i]));
            var expected = 1000 / values.OrderDescending().Take((int)Math.Ceiling(values.Length * .01)).Average();
            check(Math.Abs(varying.Read().LowOnePercent!.Value - expected) < 1e-10, "Tail heap matches the exact full-sort definition of 1% low");
            var arrival = varying.Read().LastArrivalTimestamp;
            varying.Add(new(42, "fixture", 100, 10));
            check(varying.Read().LastArrivalTimestamp == arrival, "Late frame on the selected chain does not refresh its latest presentation timestamp");

            var extreme = new FrameWindow(42);
            for (var i = 0; i < 100; i++) extreme.Add(new(42, "extreme", i, double.MaxValue / 2));
            check(extreme.Read().Fps is > 0 && extreme.Read().LowOnePercent is > 0 && double.IsFinite(extreme.Read().Milliseconds!.Value),
                "Finite large intervals cannot overflow a sum into fabricated zero FPS");
            var tiny = new FrameWindow(42);
            tiny.Add(new(42, "tiny", 0, double.Epsilon)); tiny.Add(new(42, "tiny", 1, double.Epsilon));
            check(tiny.Read().Fps is null, "Unrepresentable frame rate is unavailable instead of Infinity");

            var concurrent = new FrameWindow(42);
            using (var start = new ManualResetEventSlim())
            {
                var inconsistent = 0;
                var writer = Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 4000; i++)
                    {
                        concurrent.Add(new(42, "a", i * 10, 10));
                        if (i % 2 == 0) concurrent.Add(new(42, "b", i * 10, 20));
                    }
                });
                var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 256; i++)
                    {
                        var snapshot = concurrent.Read();
                        var rate = snapshot.SwapChain == "a" ? 100 : 50;
                        if ((snapshot.Fps.HasValue && snapshot.Fps != rate) ||
                            (snapshot.LowOnePercent.HasValue && snapshot.LowOnePercent != rate) || snapshot.Count > 60000)
                            Interlocked.Increment(ref inconsistent);
                    }
                })).ToArray();
                start.Set(); Task.WaitAll(readers.Append(writer).ToArray());
                check(inconsistent == 0 && concurrent.Read().SwapChain == "a" && concurrent.Read().Count == 4000,
                    "Concurrent capture and snapshot readers never mix two chain rates or reuse returned pool data");
            }

            var highRate = new FrameWindow(42);
            for (var i = 0; i < 14400; i++) highRate.Add(new(42, "high", i * (1000.0 / 240), 1000.0 / 240));
            highRate.Read();
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 60; i++) { highRate.Add(new(42, "high", 60000 + i, 1000.0 / 240)); highRate.Read(); }
            var perRead = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 60;
            check(perRead < 4096, "Changing high-rate statistics use under 4 KiB per read after pool warmup");
            Console.WriteLine("FRAME STATISTICS ALLOCATION: " + perRead + " bytes/read at about 14,400 frames");
        }
        finally { Directory.Delete(directory, true); }
    }
}
