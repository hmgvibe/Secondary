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
    public int StackOrder => Title switch { "CPU" => 0, "GPU" => 1, "内存" => 2, _ => -1 };
    public bool IsStorage => Title == "保存空间";
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
        FullCards = [.. Cards, new("保存空间", "等待读取"), new("系统与电源", "等待读取")];
    }
    public long UpdateCount { get; private set; }
    public MetricCard[] Cards { get; } =
    [new("CPU", "等待两次采样"), new("内存", "实体内存"), new("GPU", "等待驱动计数器"), new("网络", "活动界面合计，虚拟界面可能重复计量")];
    public MetricCard[] Details { get; } =
    [new("处理器信息", "硬件型号与逻辑处理器"), new("系统运行", "Windows 版本与运行时间"), new("内存余量", "可供系统使用的实体内存"), new("网络连接", "活动界面清单，每 30 秒更新"), new("电源状态", "Windows 回报的供电状态")];
    private DiskUsage[] disks = Array.Empty<DiskUsage>();
    public DiskUsage[] Disks { get => disks; private set { if (disks.SequenceEqual(value)) return; disks = value; Changed(); } }
    private string status = "等待系统分区显示后开始采样。", storage = "等待读取", power = "等待读取";
    public string Status { get => status; set { if (status == value) return; status = value; Changed(); } }
    public string Storage { get => storage; private set { if (storage == value) return; storage = value; Changed(); } }
    public string Power { get => power; private set { if (power == value) return; power = value; Changed(); } }
    public void Update(SystemSample sample, bool game, bool high = false)
    {
        UpdateCount++;
        Cards[0].Update(sample.Cpu is { } cpu ? $"{cpu:0}%" : "—", "处理器时间使用率 · 非时脉加权性能" + (Environment.ProcessorCount > 64 ? "（目前处理器群组）" : ""));
        var used = sample.TotalMemory >= sample.AvailableMemory ? sample.TotalMemory - sample.AvailableMemory : 0;
        Cards[1].Update(sample.TotalMemory == 0 ? "—" : $"{100.0 * used / sample.TotalMemory:0}%",
            sample.TotalMemory == 0 ? "内存数据暂时不可用" : $"{TelemetryMath.Bytes(used)} / {TelemetryMath.Bytes(sample.TotalMemory)}");
        Cards[2].Update(sample.Gpu is { } gpu ? $"{gpu:0}%" : "—", sample.GpuStatus + $" · 每 {SamplingPolicy.GpuIntervalSeconds(game, high)} 秒");
        Cards[3].Update(sample.Download is { } down ? "↓ " + TelemetryMath.Bytes(down) + "/s" : "—",
            sample.Upload is { } up ? "↑ " + TelemetryMath.Bytes(up) + "/s · 活动界面合计" : "等待界面基准；无连接或读取失败时不显示零速率");
        Storage = sample.Storage;
        Power = sample.Power;
        if (!game)
        {
            Disks = sample.Disks ?? Array.Empty<DiskUsage>();
            Details[0].Update(sample.CpuName ?? "—", $"{Environment.ProcessorCount} 个可用逻辑处理器 · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            var uptime = TimeSpan.FromMilliseconds(Math.Max(0, sample.UptimeMilliseconds));
            Details[1].Update($"{uptime.Days} 天 {uptime.Hours} 小时 {uptime.Minutes} 分", (sample.SystemInfo ?? "版本信息不可用") + "\n自本次 Windows 启动起（含休眠）");
            Details[2].Update(sample.TotalMemory == 0 ? "—" : TelemetryMath.Bytes(sample.AvailableMemory), "可用实体内存；不是单一程序的剩余额度");
            Details[3].Update(sample.Connections ?? "—", "活动非回环／非隧道界面；虚拟界面可能重复计量");
            Details[4].Update(sample.Power, "依 Windows 电源信息更新");
            Cards[0].Extra = Details[0].Value + "\n" + Details[0].Detail;
            Cards[1].Extra = "可用 " + Details[2].Value + "\n" + Details[2].Detail;
            Cards[2].Extra = "温度：目前无可靠感测来源，不安装核心驱动；不以估算数值代替。";
            Cards[3].Extra = Details[3].Value + "\n" + Details[3].Detail;
            FullCards[4].Update("", sample.Storage);
            FullCards[5].Update(Details[1].Value, Details[1].Detail);
            FullCards[5].Extra = "电源 · " + sample.Power;
        }
        Status = $"更新于 {sample.Time:HH:mm:ss} · " + (game ? (high ? "高采样率 1 秒 · GPU 2 秒" : "低采样率 3 秒 · GPU 10 秒") + " · 无动画／图表／磁碟轮询" : "采样 1 秒 · GPU 2 秒 · 磁碟／界面清单 30 秒");
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
