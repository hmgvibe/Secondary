namespace Secondary.Core;

public enum ReadingState { Available, WarmingUp, Unavailable, Disabled }
public enum TelemetrySourceState { Waiting, Available, Stale, Failed, Stopped, Disabled }

public sealed record MonitoringReading(string Metric, string Source, string Identity,
    DateTimeOffset Time, double? Value, string Unit, ReadingState State, string? Reason = null);
