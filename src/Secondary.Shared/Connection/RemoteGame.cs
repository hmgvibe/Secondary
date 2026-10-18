using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// A process on the main computer that the device can pick; Started tells a reused PID apart.
public sealed record RemoteProcess(int Id, long Started, string Name);
// What the device asks the helper to sample while its game page is on screen. Session ties snapshots to this request.
public sealed record RemoteGameRequest(long Session, bool High, bool Sensors, RemoteProcess? Target);
// One reading of the main computer's game telemetry, reduced to what the game page shows. Sent once a second.
public sealed record RemoteGameSnapshot(long Session, DateTimeOffset Time, bool High,
    double? Cpu, ulong TotalMemory, ulong AvailableMemory, double? Download, double? Upload,
    TelemetrySourceState SystemState, TelemetrySourceState CpuState, TelemetrySourceState MemoryState, TelemetrySourceState NetworkState, string SystemStatus,
    double? Gpu, TelemetrySourceState GpuState, string GpuStatus,
    MonitoringReading[] Sensors, TelemetrySourceState SensorState, string SensorStatus,
    FrameStatistics Frames, TelemetrySourceState FrameState, string FrameStatus, bool TargetExited);

// Messages for remote game monitoring and the synced hotkey. Everything received is validated: a snapshot that
// fails any check is dropped, so the device shows "—" rather than a wrong value.
public static class RemoteGame
{
    public const int MaxProcesses = 1000;
    public const int MaxSensors = 64;
    private const int MaxText = 512;
    // Older than this, a snapshot is shown as stale: three missed seconds.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3.5);

    public static JsonObject Start(RemoteGameRequest request) => Message("game-start", ("session", request.Session), ("high", request.High), ("sensors", request.Sensors),
        ("target", request.Target is { } target ? Process(target) : null));
    public static RemoteGameRequest? ReadStart(JsonObject message)
    {
        if (Number(message, "session") is not { } session || Flag(message, "high") is not { } high || Flag(message, "sensors") is not { } sensors) return null;
        var target = message["target"] is JsonObject node ? ReadProcess(node) : null;
        if (message["target"] is JsonObject && target is null) return null;
        return new(session, high, sensors, target);
    }

    public static JsonObject Snapshot(RemoteGameSnapshot snapshot)
    {
        static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;
        var frames = snapshot.Frames with { Fps = Finite(snapshot.Frames.Fps), LowOnePercent = Finite(snapshot.Frames.LowOnePercent), Milliseconds = Finite(snapshot.Frames.Milliseconds), LastArrivalTimestamp = 0 };
        var clean = snapshot with
        {
            Cpu = Finite(snapshot.Cpu), Download = Finite(snapshot.Download), Upload = Finite(snapshot.Upload), Gpu = Finite(snapshot.Gpu), Frames = frames,
            Sensors = snapshot.Sensors.Take(MaxSensors).Select(r => r with { Value = Finite(r.Value) }).ToArray()
        };
        return Message("game-snapshot", ("snapshot", JsonSerializer.SerializeToNode(clean)));
    }
    public static RemoteGameSnapshot? ReadSnapshot(JsonObject message)
    {
        if (message["snapshot"] is not JsonObject node) return null;
        RemoteGameSnapshot? value;
        try { value = node.Deserialize<RemoteGameSnapshot>(); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException) { return null; }
        return value is not null && Valid(value) ? value : null;
    }
    private static bool Valid(RemoteGameSnapshot s)
    {
        static bool Number(double? value, double maximum = double.MaxValue) => value is null || (double.IsFinite(value.Value) && value >= 0 && value <= maximum);
        static bool Text(string? text) => text is not null && text.Length <= MaxText;
        static bool State(TelemetrySourceState state) => Enum.IsDefined(state);
        return Number(s.Cpu, 100) && Number(s.Gpu, 100) && Number(s.Download) && Number(s.Upload) && s.AvailableMemory <= s.TotalMemory &&
            State(s.SystemState) && State(s.CpuState) && State(s.MemoryState) && State(s.NetworkState) && State(s.GpuState) && State(s.SensorState) && State(s.FrameState) &&
            Text(s.SystemStatus) && Text(s.GpuStatus) && Text(s.SensorStatus) && Text(s.FrameStatus) &&
            s.Frames is { Count: >= 0 } f && Number(f.Fps) && Number(f.LowOnePercent) && Number(f.Milliseconds) && (f.SwapChain is null || f.SwapChain.Length <= MaxText) &&
            s.Sensors is { Length: <= MaxSensors } && s.Sensors.All(r => r is not null && r.Metric is { Length: > 0 and <= 200 } && r.Identity is { Length: > 0 and <= 200 } &&
                r.Source is { Length: <= 100 } && r.Unit is { Length: <= 16 } && Enum.IsDefined(r.State) && (r.Value is null || double.IsFinite(r.Value.Value)) && (r.Reason is null || r.Reason.Length <= MaxText));
    }

    // The game page needs the GPU sensors and the hottest CPU values; sending only those keeps a snapshot to a few KB.
    public static MonitoringReading[] CompactSensors(IEnumerable<MonitoringReading> readings)
    {
        string[] gpuMetrics = [" / GPU Core", " / GPU Package", " / GPU Memory Used", " / GPU Memory Total"];
        var all = readings.ToArray();
        var gpu = all.Where(r => r.Identity.StartsWith("/gpu-", StringComparison.Ordinal) && gpuMetrics.Any(m => r.Metric.EndsWith(m, StringComparison.Ordinal)));
        var cpu = all.Where(r => r.Identity.StartsWith("/intelcpu/", StringComparison.Ordinal) || r.Identity.StartsWith("/amdcpu/", StringComparison.Ordinal))
            .Where(r => r.Unit is "°C" or "MHz" or "W" && r.Value.HasValue).GroupBy(r => r.Unit).Select(g => g.MaxBy(r => r.Value)!);
        var notes = all.Where(r => r.Identity == "cpu/pawnio");
        return gpu.Concat(cpu).Concat(notes).Take(MaxSensors).ToArray();
    }

    public static JsonObject Process(RemoteProcess process) => new() { ["id"] = process.Id, ["started"] = process.Started, ["name"] = process.Name };
    public static RemoteProcess? ReadProcess(JsonObject node) =>
        Number(node, "id") is { } id && id is > 0 and <= int.MaxValue && Number(node, "started") is { } started && Text(node, "name") is { Length: > 0 and <= 260 } name
            ? new((int)id, started, name) : null;
    public static JsonObject ProcessList(IEnumerable<RemoteProcess> processes) => Message("process-list", ("processes", new JsonArray(processes.Take(MaxProcesses).Select(p => (JsonNode)Process(p)).ToArray())));
    public static RemoteProcess[] ReadProcessList(JsonObject message) => message["processes"] is JsonArray list
        ? list.Take(MaxProcesses).OfType<JsonObject>().Select(ReadProcess).OfType<RemoteProcess>().ToArray() : [];

    public static async Task<RemoteProcess[]> ListProcessesAsync(ConnectionController controller, CancellationToken token = default) =>
        ReadProcessList(await controller.RequestAsync(Message("processes"), TimeSpan.FromSeconds(10), token).ConfigureAwait(false));

    public static JsonObject HotkeyStatus(bool active, string text) => Message("hotkey-status", ("active", active), ("text", text));

    private static bool? Flag(JsonObject message, string name) => message[name]?.GetValueKind() switch
    {
        JsonValueKind.True => true, JsonValueKind.False => false, _ => null
    };
}
