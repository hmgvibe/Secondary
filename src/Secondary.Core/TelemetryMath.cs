namespace Secondary.Core;

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public static class TelemetryMath
{
    public static double? CpuPercent(CpuTimes? previous, CpuTimes current)
    {
        if (previous is not { } p || current.Idle < p.Idle || current.Kernel < p.Kernel || current.User < p.User) return null;
        var total = (double)(current.Kernel - p.Kernel) + (current.User - p.User);
        return total <= 0 ? null : Math.Clamp(100 * (1 - (current.Idle - p.Idle) / total), 0, 100);
    }
    public static double? Rate(long previous, long current, double seconds) =>
        current < previous || seconds <= 0 || !double.IsFinite(seconds) ? null : (current - previous) / seconds;
    public static string Bytes(double value) => value >= 1073741824 ? $"{value / 1073741824:0.0} GiB" : value >= 1048576 ? $"{value / 1048576:0.0} MiB" : $"{value / 1024:0.0} KiB";
}
