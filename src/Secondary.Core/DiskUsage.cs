namespace Secondary.Core;

public sealed record DiskUsage(string Name, string FileSystem, long Total, long Free)
{
    public bool IsValid => Total > 0 && Free >= 0 && Free <= Total;
    public long Used => IsValid ? Total - Free : 0;
    public double Percent => IsValid ? 100.0 * Used / Total : 0;
    public string UsageLabel => IsValid ? $"{Percent:0.0}% 已使用" : "容量不可用";
    public string CapacityLabel => IsValid ? $"已用 {TelemetryMath.Bytes(Used)} / {TelemetryMath.Bytes(Total)}" : "等待有效容量資料";
    public string FreeLabel => IsValid ? $"剩餘 {TelemetryMath.Bytes(Free)} · {FileSystem}" : FileSystem;
}
