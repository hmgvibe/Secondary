using System;
using System.Runtime.InteropServices;

namespace Secondary.Linux.Monitoring;

public sealed record NvidiaReading(double? Load, double? Temperature, double? Clock, double? Power, double? MemoryUsed, double? MemoryTotal);

// NVML ships with the proprietary driver (libnvidia-ml.so.1); without it NVIDIA cards show "—".
// Function pointers keep a missing library or symbol a soft failure instead of a load exception.
public sealed unsafe class NvidiaSource : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    private readonly IntPtr library;
    private readonly delegate* unmanaged<int> shutdown;
    private readonly delegate* unmanaged<byte*, IntPtr*, int> byBusId;
    private readonly delegate* unmanaged<IntPtr, Utilization*, int> utilization;
    private readonly delegate* unmanaged<IntPtr, Memory*, int> memory;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> temperature;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> clock;
    private readonly delegate* unmanaged<IntPtr, uint*, int> power;
    private bool disposed;

    private NvidiaSource(IntPtr library)
    {
        this.library = library;
        IntPtr Export(string name) => NativeLibrary.GetExport(library, name);
        shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
        byBusId = (delegate* unmanaged<byte*, IntPtr*, int>)Export("nvmlDeviceGetHandleByPciBusId_v2");
        utilization = (delegate* unmanaged<IntPtr, Utilization*, int>)Export("nvmlDeviceGetUtilizationRates");
        memory = (delegate* unmanaged<IntPtr, Memory*, int>)Export("nvmlDeviceGetMemoryInfo");
        temperature = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetTemperature");
        clock = (delegate* unmanaged<IntPtr, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
        power = (delegate* unmanaged<IntPtr, uint*, int>)Export("nvmlDeviceGetPowerUsage");
    }

    public static NvidiaSource? TryLoad()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libnvidia-ml.so.1", out var library)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvmlInit_v2", out var init) || ((delegate* unmanaged<int>)init)() != 0)
            { NativeLibrary.Free(library); return null; }
            return new NvidiaSource(library);
        }
        catch (EntryPointNotFoundException) { NativeLibrary.Free(library); return null; }
    }

    // NVML wants the PCI bus ID as sysfs writes it ("0000:01:00.0"); it accepts the short domain form.
    public NvidiaReading? Read(string? busId)
    {
        if (disposed || string.IsNullOrEmpty(busId)) return null;
        IntPtr device;
        var bytes = System.Text.Encoding.ASCII.GetBytes(busId + "\0");
        fixed (byte* text = bytes) if (byBusId(text, &device) != 0) return null;
        Utilization use; Memory mem; uint degrees, megahertz, milliwatts;
        return new(
            utilization(device, &use) == 0 ? use.Gpu : null,
            temperature(device, 0, &degrees) == 0 ? degrees : null,
            clock(device, 0, &megahertz) == 0 ? megahertz : null,
            power(device, &milliwatts) == 0 ? milliwatts / 1000.0 : null,
            memory(device, &mem) == 0 ? mem.Used / 1048576.0 : null,
            memory(device, &mem) == 0 ? mem.Total / 1048576.0 : null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shutdown();
        NativeLibrary.Free(library);
    }
}
