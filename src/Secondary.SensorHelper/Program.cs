using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using Secondary.Core;

// No service, named IPC endpoint, installer, elevation verb, commands or control writes.
if (args.Length != 1 || args[0] is not ("--sample" or "--stream-low" or "--stream-high")) return 2;
var streaming = args[0] != "--sample";
using var parentLease = new System.Threading.Timer(_ => Environment.Exit(125), null, streaming ? 20000 : Timeout.Infinite, Timeout.Infinite);
if (streaming) _ = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } command)
    {
        if (command != "ping") { Environment.Exit(2); return; }
        parentLease.Change(20000, Timeout.Infinite);
    }
    Environment.Exit(0); // Parent closed its anonymous pipe; never remain as an orphan.
});
using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, 20000, Timeout.Infinite);
var computer = new Computer();
var readings = new List<MonitoringReading>();
void Unavailable(string metric, string identity, string reason)
{
    if (readings.Count >= 512) readings.RemoveAt(511);
    readings.Add(new(metric, "LibreHardwareMonitor 0.9.6", identity, DateTimeOffset.UtcNow,
        null, "", ReadingState.Unavailable, reason));
}
try
{
    // CPU low-level sources are deliberately skipped when PawnIO is absent.
    var cpuEnabled = PawnIo.IsInstalled;
    computer.IsCpuEnabled = cpuEnabled;
    computer.IsGpuEnabled = true;
    computer.IsMemoryEnabled = true;
    computer.Open();
    void Update(IHardware hardware)
    {
        hardware.Update();
        foreach (var child in hardware.SubHardware) Update(child);
    }
    foreach (var hardware in computer.Hardware) Update(hardware);
    await Task.Delay(1100);
    void Read(IHardware hardware)
    {
        try
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                var unit = sensor.SensorType switch
                {
                    SensorType.Temperature => "°C", SensorType.Clock => "MHz", SensorType.Power => "W",
                    SensorType.Load => "%", SensorType.Data => "GB", SensorType.SmallData => "MB", _ => null
                };
                if (unit is null || readings.Count >= 512) continue;
                double? value = sensor.Value is float v && float.IsFinite(v) ? v : null;
                readings.Add(new(hardware.Name + " / " + sensor.Name, "LibreHardwareMonitor 0.9.6",
                    sensor.Identifier.ToString(), DateTimeOffset.UtcNow, value, unit,
                    value.HasValue ? ReadingState.Available : ReadingState.Unavailable,
                    value.HasValue ? null : "感測器未回傳數值；可能不支援或缺少存取權限。"));
            }
            foreach (var child in hardware.SubHardware) Read(child);
        }
        catch (Exception e) { Unavailable(hardware.Name, hardware.Identifier.ToString(), e.Message); }
    }
    do
    {
        watchdog.Change(20000, Timeout.Infinite);
        readings.Clear();
        foreach (var hardware in computer.Hardware) Read(hardware);
        if (!cpuEnabled) Unavailable("CPU 溫度／時脈／功耗、Intel GPU", "cpu/pawnio",
            "未偵測到已安裝的 PawnIO，已略過依賴此來源的 CPU 與 Intel GPU 探測；不會自動安裝。");
        if (readings.Count == 0) Unavailable("硬體感測", "system", "沒有可讀取的感測器。");
        if (!streaming) break;
        Console.WriteLine(JsonSerializer.Serialize(new SensorSnapshot(1, readings.ToArray())));
        await Console.Out.FlushAsync();
        await Task.Delay(args[0] == "--stream-high" ? 2000 : 10000);
    } while (streaming);
}
catch (Exception e) { Unavailable("硬體感測", "system", e.Message); }
finally
{
    try { computer.Close(); }
    catch (Exception e) { Unavailable("釋放感測元件", "system", e.Message); }
}
Console.WriteLine(JsonSerializer.Serialize(new SensorSnapshot(1, readings.ToArray())));
return 0;
