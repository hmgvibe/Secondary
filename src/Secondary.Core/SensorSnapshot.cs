using System.Text.Json;

namespace Secondary.Core;

public sealed record SensorSnapshot(int Schema, MonitoringReading[] Readings)
{
    public static SensorSnapshot Parse(string json)
    {
        if (json.Length > 512 * 1024) throw new InvalidDataException("感測結果超出大小限制。");
        var result = JsonSerializer.Deserialize<SensorSnapshot>(json);
        if (result is null || result.Schema != 1 || result.Readings is null || result.Readings.Length > 512 ||
            result.Readings.Any(r => r is null || string.IsNullOrWhiteSpace(r.Metric) ||
                string.IsNullOrWhiteSpace(r.Identity) || r.Source != "LibreHardwareMonitor 0.9.6" ||
                !Enum.IsDefined(r.State) || (r.Value.HasValue && !double.IsFinite(r.Value.Value)) ||
                (r.State == ReadingState.Available) != r.Value.HasValue))
            throw new InvalidDataException("感測助手回傳的格式無效。");
        return result;
    }
}
