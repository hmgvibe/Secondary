using System.Buffers;
using System.Diagnostics;

namespace Secondary.Core;

public sealed record FrameStatistics(string? SwapChain, int Count, double? Fps, double? LowOnePercent, double? Milliseconds, bool Truncated, long LastArrivalTimestamp = 0);

// Capture locks only enqueue/copy data; statistics run on the snapshot outside that lock.
// Time is PresentMon's monotonically increasing capture clock, in ms.
public sealed class FrameWindow(int processId)
{
    private readonly record struct Arrival(PresentMonFrame Frame, long Received);
    private sealed class Chain
    {
        public int Count, Recent;
        public double Last = -1, RecentMean;
        public long Received;
        public void Reset() { Count = Recent = 0; Last = -1; RecentMean = 0; Received = 0; }
    }
    private readonly object captureGate = new(), readGate = new();
    private readonly Queue<Arrival> frames = new();
    private readonly Dictionary<string, Chain> chains = new(StringComparer.Ordinal);
    private double latest = -1;
    private double truncatedAt = double.NegativeInfinity;
    private long version, cachedVersion = -1;
    private string? selected, challenger;
    private double challengeSince;
    private FrameStatistics cached = new(null, 0, null, null, null, false);
    public int Count { get { lock (captureGate) return frames.Count; } }
    public void Add(PresentMonFrame frame)
    {
        lock (captureGate)
        {
            if (frame.ProcessId != processId || !double.IsFinite(frame.StartMilliseconds) ||
                frame.StartMilliseconds < 0 || frame.StartMilliseconds < latest - 60000 || !double.IsFinite(frame.CpuFrameMilliseconds) ||
                frame.CpuFrameMilliseconds <= 0 || string.IsNullOrEmpty(frame.SwapChain)) return;
            latest = Math.Max(latest, frame.StartMilliseconds);
            frames.Enqueue(new(frame, Stopwatch.GetTimestamp()));
            while (frames.Count > 0 && frames.Peek().Frame.StartMilliseconds < latest - 60000) frames.Dequeue();
            while (frames.Count > 60000) { truncatedAt = latest; frames.Dequeue(); }
            version++;
        }
    }
    public FrameStatistics Read()
    {
        lock (readGate)
        {
            Arrival[] snapshot;
            int count;
            double now, clipped;
            long observed;
            lock (captureGate)
            {
                if (cachedVersion == version) return cached;
                count = frames.Count;
                snapshot = ArrayPool<Arrival>.Shared.Rent(Math.Max(1, count));
                frames.CopyTo(snapshot, 0);
                now = latest; clipped = truncatedAt; observed = version;
            }
            try
            {
                cached = Calculate(snapshot, count, now, clipped);
                cachedVersion = observed;
                return cached;
            }
            finally { ArrayPool<Arrival>.Shared.Return(snapshot, clearArray: true); }
        }
    }
    private FrameStatistics Calculate(Arrival[] snapshot, int count, double now, double clipped)
    {
        foreach (var chain in chains.Values) chain.Reset();
        for (var i = 0; i < count; i++)
        {
            var item = snapshot[i]; var frame = item.Frame;
            // Arrival order can differ between chains and within a chain.
            if (frame.StartMilliseconds < now - 60000) continue;
            if (!chains.TryGetValue(frame.SwapChain, out var chain)) chains.Add(frame.SwapChain, chain = new());
            chain.Count++;
            if (frame.StartMilliseconds >= now - 1000)
            { chain.Recent++; chain.RecentMean += (frame.CpuFrameMilliseconds - chain.RecentMean) / chain.Recent; }
            if (frame.StartMilliseconds > chain.Last) { chain.Last = frame.StartMilliseconds; chain.Received = item.Received; }
        }
        string? best = null;
        foreach (var pair in chains)
        {
            var candidate = pair.Value;
            if (candidate.Count == 0 || candidate.Last < now - 1000) continue;
            if (best is null || candidate.Recent > chains[best].Recent ||
                (candidate.Recent == chains[best].Recent && (candidate.Count > chains[best].Count ||
                (candidate.Count == chains[best].Count && StringComparer.Ordinal.Compare(pair.Key, best) < 0)))) best = pair.Key;
        }
        if (best is null) { selected = challenger = null; return new(null, 0, null, null, null, false); }
        var currentActive = selected is not null && chains.TryGetValue(selected, out var current) && current.Last >= now - 1000;
        if (!currentActive) { selected = best; challenger = null; }
        else if (best != selected && chains[best].Recent > chains[selected!].Recent * 1.25)
        {
            // A new dominant chain must persist for half a capture second while both remain active.
            if (challenger != best) { challenger = best; challengeSince = now; }
            else if (now - challengeSince >= 500) { selected = best; challenger = null; }
        }
        else challenger = null;
        var active = chains[selected!];
        double? milliseconds = active.Recent >= 2 ? active.RecentMean : null;
        var truncated = now - clipped <= 60000;
        double? low = null;
        if (active.Count >= 100 && !truncated)
        {
            var tailCount = (int)Math.Ceiling(active.Count * .01);
            var heap = ArrayPool<double>.Shared.Rent(tailCount);
            var used = 0;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var frame = snapshot[i].Frame;
                    if (frame.SwapChain != selected || frame.StartMilliseconds < now - 60000) continue;
                    var value = frame.CpuFrameMilliseconds;
                    if (used < tailCount)
                    {
                        var index = used++;
                        while (index > 0 && heap[(index - 1) / 2] > value)
                        { heap[index] = heap[(index - 1) / 2]; index = (index - 1) / 2; }
                        heap[index] = value;
                    }
                    else if (value > heap[0])
                    {
                        var index = 0;
                        while (index * 2 + 1 < used)
                        {
                            var child = index * 2 + 1;
                            if (child + 1 < used && heap[child + 1] < heap[child]) child++;
                            if (heap[child] >= value) break;
                            heap[index] = heap[child]; index = child;
                        }
                        heap[index] = value;
                    }
                }
                double mean = 0; for (var i = 0; i < used; i++) mean += (heap[i] - mean) / (i + 1);
                low = Rate(mean);
            }
            finally { ArrayPool<double>.Shared.Return(heap); }
        }
        // Empty chain metadata is bounded by the retained snapshot, even for many recreated chains.
        if (chains.Count > 256)
            foreach (var key in chains.Where(p => p.Value.Count == 0).Select(p => p.Key).ToArray()) chains.Remove(key);
        return new(selected, active.Count, milliseconds.HasValue ? Rate(milliseconds.Value) : null, low,
            milliseconds, truncated, active.Received);
    }
    private static double? Rate(double milliseconds)
    {
        var value = 1000 / milliseconds;
        return double.IsFinite(value) && value > 0 ? value : null;
    }
}

public sealed record TracePoint(double Seconds, double? Value);

public sealed class TelemetryHistory
{
    private readonly Queue<TracePoint> points = new();
    public void Add(double seconds, double? value)
    {
        if (!double.IsFinite(seconds) || (points.Count > 0 && seconds < points.Last().Seconds)) return;
        points.Enqueue(new(seconds, value.HasValue && double.IsFinite(value.Value) ? value : null));
        while (points.Count > 121 || (points.Count > 0 && points.Peek().Seconds < seconds - 60)) points.Dequeue();
    }
    public TracePoint[] Snapshot() => points.ToArray();
}
