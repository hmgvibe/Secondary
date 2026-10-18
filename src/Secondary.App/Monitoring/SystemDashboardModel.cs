using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Secondary.Core;
using System.Linq;
using Secondary.Monitoring;

namespace Secondary.App.Monitoring;

public sealed class MetricCard(string title, string caption) : INotifyPropertyChanged
{
    public string Title { get; } = title;
    public int StackOrder => Title switch { "CPU" => 0, "GPU" => 1, "記憶體" => 2, _ => -1 };
    public bool IsStorage => Title == "儲存空間";
    private string extra = "";
    public string Extra { get => extra; set { if (extra == value) return; extra = value; Changed(); } }
    private string value = "—", detail = caption;
    public string Value { get => value; private set { if (this.value == value) return; this.value = value; Changed(); } }
    public string Detail { get => detail; private set { if (detail == value) return; detail = value; Changed(); } }
    public void Update(string value, string detail) { Value = value; Detail = detail; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class SystemDashboardModel : INotifyPropertyChanged
{
    public MetricCard[] FullCards { get; }
    public SystemDashboardModel()
    {
        FullCards = [.. Cards, new("儲存空間", "等待讀取"), new("系統與電源", "等待讀取")];
    }
    public long UpdateCount { get; private set; }
    public MetricCard[] Cards { get; } =
    [new("CPU", "等待兩次採樣"), new("記憶體", "實體記憶體"), new("GPU", "等待驅動計數器"), new("網路", "活動介面合計，虛擬介面可能重複計量")];
    public MetricCard[] Details { get; } =
    [new("處理器資訊", "硬體型號與邏輯處理器"), new("系統運行", "Windows 版本與運行時間"), new("記憶體餘量", "可供系統使用的實體記憶體"), new("網路連線", "活動介面清單，每 30 秒更新"), new("電源狀態", "Windows 回報的供電狀態")];
    private DiskUsage[] disks = Array.Empty<DiskUsage>();
    public DiskUsage[] Disks { get => disks; private set { if (disks.SequenceEqual(value)) return; disks = value; Changed(); } }
    private string status = "等待系統分區顯示後開始採樣。", storage = "等待讀取", power = "等待讀取";
    public string Status { get => status; set { if (status == value) return; status = value; Changed(); } }
    public string Storage { get => storage; private set { if (storage == value) return; storage = value; Changed(); } }
    public string Power { get => power; private set { if (power == value) return; power = value; Changed(); } }
    public void Update(SystemSample sample, bool game, bool high = false)
    {
        UpdateCount++;
        Cards[0].Update(sample.Cpu is { } cpu ? $"{cpu:0}%" : "—", "處理器時間使用率 · 非時脈加權效能" + (Environment.ProcessorCount > 64 ? "（目前處理器群組）" : ""));
        var used = sample.TotalMemory >= sample.AvailableMemory ? sample.TotalMemory - sample.AvailableMemory : 0;
        Cards[1].Update(sample.TotalMemory == 0 ? "—" : $"{100.0 * used / sample.TotalMemory:0}%",
            sample.TotalMemory == 0 ? "記憶體資料暫時不可用" : $"{TelemetryMath.Bytes(used)} / {TelemetryMath.Bytes(sample.TotalMemory)}");
        Cards[2].Update(sample.Gpu is { } gpu ? $"{gpu:0}%" : "—", sample.GpuStatus + $" · 每 {SamplingPolicy.GpuIntervalSeconds(game, high)} 秒");
        Cards[3].Update(sample.Download is { } down ? "↓ " + TelemetryMath.Bytes(down) + "/s" : "—",
            sample.Upload is { } up ? "↑ " + TelemetryMath.Bytes(up) + "/s · 活動介面合計" : "等待介面基準；無連線或讀取失敗時不顯示零速率");
        Storage = sample.Storage;
        Power = sample.Power;
        if (!game)
        {
            Disks = sample.Disks ?? Array.Empty<DiskUsage>();
            Details[0].Update(sample.CpuName ?? "—", $"{Environment.ProcessorCount} 個可用邏輯處理器 · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            var uptime = TimeSpan.FromMilliseconds(Math.Max(0, sample.UptimeMilliseconds));
            Details[1].Update($"{uptime.Days} 天 {uptime.Hours} 小時 {uptime.Minutes} 分", (sample.SystemInfo ?? "版本資訊不可用") + "\n自本次 Windows 啟動起（含休眠）");
            Details[2].Update(sample.TotalMemory == 0 ? "—" : TelemetryMath.Bytes(sample.AvailableMemory), "可用實體記憶體；不是單一程式的剩餘額度");
            Details[3].Update(sample.Connections ?? "—", "活動非回環／非隧道介面；虛擬介面可能重複計量");
            Details[4].Update(sample.Power, "依 Windows 電源資訊更新");
            Cards[0].Extra = Details[0].Value + "\n" + Details[0].Detail;
            Cards[1].Extra = "可用 " + Details[2].Value + "\n" + Details[2].Detail;
            Cards[2].Extra = "溫度：目前無可靠感測來源，不安裝核心驅動；不以估算數值代替。";
            Cards[3].Extra = Details[3].Value + "\n" + Details[3].Detail;
            FullCards[4].Update("", sample.Storage);
            FullCards[5].Update(Details[1].Value, Details[1].Detail);
            FullCards[5].Extra = "電源 · " + sample.Power;
        }
        Status = $"更新於 {sample.Time:HH:mm:ss} · " + (game ? (high ? "高採樣率 1 秒 · GPU 2 秒" : "低採樣率 3 秒 · GPU 10 秒") + " · 無動畫／圖表／磁碟輪詢" : "採樣 1 秒 · GPU 2 秒 · 磁碟／介面清單 30 秒");
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
