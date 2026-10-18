using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Secondary.Monitoring;

// Sum process counters for each physical engine, then display the busiest engine.
// This is not an average across GPUs and is deliberately labelled as such.
internal sealed class GpuCounter : IDisposable
{
    private IntPtr query, counter;
    private bool primed;
    public string Status { get; private set; } = "等待兩次採樣";
    public GpuCounter()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0 ||
            PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
        { Dispose(); Status = "驅動未提供 GPU 計數器"; }
    }
    public double? Read()
    {
        if (query == IntPtr.Zero) return null;
        if (PdhCollectQueryData(query) != 0) { Status = "GPU 計數器暫時無資料"; primed = false; return null; }
        if (!primed) { primed = true; return null; }
        uint bytes = 0;
        PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out _, IntPtr.Zero);
        if (bytes == 0 || bytes > 8 * 1024 * 1024) { Status = "GPU 計數器無可用實例"; return null; }
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref bytes, out var count, buffer) != 0)
            { Status = "GPU 實例變動，等待重試"; return null; }
            var engines = new Dictionary<string, double>();
            var stride = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(IntPtr.Add(buffer, checked(i * stride)));
                if (item.Status > 1 || !double.IsFinite(item.Value)) continue;
                var name = Marshal.PtrToStringUni(item.Name) ?? "";
                var luid = name.IndexOf("luid_", StringComparison.Ordinal);
                if (luid < 0) continue;
                var engine = name[luid..];
                engines.TryGetValue(engine, out var current);
                engines[engine] = current + Math.Max(0, item.Value);
            }
            if (engines.Count == 0) { Status = "GPU 計數器尚無有效資料"; return null; }
            double busiest = 0;
            foreach (var value in engines.Values) busiest = Math.Max(busiest, value);
            Status = "所有 GPU 中最忙引擎";
            return Math.Clamp(busiest, 0, 100);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = counter = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem { [FieldOffset(0)] public IntPtr Name; [FieldOffset(8)] public uint Status; [FieldOffset(16)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
