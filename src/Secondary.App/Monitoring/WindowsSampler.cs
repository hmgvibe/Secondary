using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Secondary.Core;
using Microsoft.Win32;

namespace Secondary.App.Monitoring;

public sealed record SystemSample(double? Cpu, ulong TotalMemory, ulong AvailableMemory,
    double? Download, double? Upload, double? Gpu, string GpuStatus, string Storage, string Power, DateTimeOffset Time,
    DiskUsage[]? Disks = null, string? CpuName = null, string? SystemInfo = null, string? Connections = null, long UptimeMilliseconds = 0,
    TelemetrySourceState CpuState = TelemetrySourceState.Waiting, TelemetrySourceState MemoryState = TelemetrySourceState.Waiting,
    TelemetrySourceState NetworkState = TelemetrySourceState.Waiting, string? SourceStatus = null);

public interface ISystemSampler : IDisposable { SystemSample Read(bool game, bool high = false); }

public sealed class WindowsSampler(bool enableGpu = true) : ISystemSampler
{
    private CpuTimes? previousCpu;
    private readonly Dictionary<string, (long Received, long Sent, long Time)> network = new();
    private NetworkInterface[] adapters = Array.Empty<NetworkInterface>();
    private long inventoryAt, gpuAt;
    private string storage = "等待讀取";
    private DiskUsage[] disks = Array.Empty<DiskUsage>();
    private string cpuName = "處理器資訊暫時不可用", systemInfo = "等待讀取", connections = "等待讀取";
    private GpuCounter? gpu;
    private double? gpuValue;
    private string gpuStatus = "等待兩次採樣";
    public SystemSample Read(bool game, bool high = false)
    {
        var now = Stopwatch.GetTimestamp();
        double? cpu = null;
        var cpuState = TelemetrySourceState.Waiting;
        var issues = new List<string>();
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var current = new CpuTimes(idle, kernel, user);
            cpu = TelemetryMath.CpuPercent(previousCpu, current);
            previousCpu = current;
            cpuState = cpu.HasValue ? TelemetrySourceState.Available : TelemetrySourceState.Waiting;
        }
        else { previousCpu = null; cpuState = TelemetrySourceState.Failed; issues.Add("CPU 採樣失敗（" + Marshal.GetLastWin32Error() + "）"); }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryValid = GlobalMemoryStatusEx(ref memory);
        if (!memoryValid) { issues.Add("記憶體採樣失敗（" + Marshal.GetLastWin32Error() + "）"); memory = default; }
        var memoryState = memoryValid && memory.TotalPhysical > 0 ? TelemetrySourceState.Available : TelemetrySourceState.Failed;
        var networkFailed = false;
        if (inventoryAt == 0 || Stopwatch.GetElapsedTime(inventoryAt, now).TotalSeconds >= 30)
        {
            try { adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel).ToArray(); }
            catch (NetworkInformationException) { adapters = Array.Empty<NetworkInterface>(); networkFailed = true; issues.Add("網路介面清單讀取失敗"); }
            var ids = adapters.Select(n => n.Id).ToHashSet();
            foreach (var id in network.Keys.Where(k => !ids.Contains(k)).ToArray()) network.Remove(id);
            if (!game)
            {
                try
                {
                    using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    cpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "處理器資訊暫時不可用";
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { cpuName = "處理器資訊暫時不可用"; }
                systemInfo = RuntimeInformation.OSDescription + " · " + RuntimeInformation.OSArchitecture;
                connections = adapters.Length == 0 ? "沒有活動網路介面" : string.Join("\n", adapters.Select(a => a.Name));
                var volumes = new List<DiskUsage>();
                var failures = 0;
                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        try
                        {
                            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                            volumes.Add(new DiskUsage(drive.Name, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace));
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failures++; }
                    }
                    storage = failures > 0 ? $"{failures} 個磁碟無法讀取；其餘照常顯示" : volumes.Count == 0 ? "沒有可讀取的固定磁碟" : "固定磁碟 · 每 30 秒更新容量";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { storage = "磁碟資訊暫時無法讀取"; }
                disks = volumes.ToArray();
            }
            inventoryAt = now;
        }
        double down = 0, up = 0;
        var validNetwork = false;
        foreach (var adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) { network.Remove(adapter.Id); continue; }
                var stats = adapter.GetIPStatistics();
                if (network.TryGetValue(adapter.Id, out var old))
                {
                    var elapsed = Stopwatch.GetElapsedTime(old.Time, now).TotalSeconds;
                    var received = TelemetryMath.Rate(old.Received, stats.BytesReceived, elapsed);
                    var sent = TelemetryMath.Rate(old.Sent, stats.BytesSent, elapsed);
                    if (received.HasValue && sent.HasValue) { down += received.Value; up += sent.Value; validNetwork = true; }
                }
                network[adapter.Id] = (stats.BytesReceived, stats.BytesSent, now);
            }
            catch (NetworkInformationException) { network.Remove(adapter.Id); networkFailed = true; }
        }
        if (enableGpu && (gpuAt == 0 || Stopwatch.GetElapsedTime(gpuAt, now).TotalSeconds >= SamplingPolicy.GpuIntervalSeconds(game, high)))
        {
            gpu ??= new GpuCounter();
            gpuValue = gpu.Read();
            gpuStatus = gpu.Status;
            gpuAt = now;
        }
        var power = "無法讀取電源";
        if (GetSystemPowerStatus(out var p))
            power = (p.AcLineStatus == 1 ? "外接電源" : p.AcLineStatus == 0 ? "使用電池" : "電源狀態未知") +
                (p.BatteryFlag == 255 ? " · 電池狀態未知" : (p.BatteryFlag & 128) != 0 ? " · 無電池" : p.BatteryPercent <= 100 ? $" · 電量 {p.BatteryPercent}%" : " · 電量未知");
        return new(cpu, memory.TotalPhysical, memory.AvailablePhysical, validNetwork ? down : null, validNetwork ? up : null,
            gpuValue, gpuStatus, game ? "遊戲模式不讀取磁碟" : storage, power, DateTimeOffset.Now,
            game ? Array.Empty<DiskUsage>() : disks, game ? null : cpuName, game ? null : systemInfo, game ? null : connections, game ? 0 : Environment.TickCount64,
            cpuState, memoryState, validNetwork ? TelemetrySourceState.Available : networkFailed ? TelemetrySourceState.Failed : TelemetrySourceState.Waiting,
            issues.Count > 0 ? string.Join("；", issues) : networkFailed ? "部分網路介面無法讀取；可用介面仍更新" : cpu is null ? "CPU／網路正在建立採樣基準" : "CPU／記憶體／網路採樣持續更新");
    }
    public void Dispose() => gpu?.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLineStatus, BatteryFlag, BatteryPercent, SystemStatus; public uint LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
