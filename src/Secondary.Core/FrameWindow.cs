namespace Secondary.Core;

public sealed record FrameStatistics(string? SwapChain, int Count, double? Fps, double? LowOnePercent, double? Milliseconds, bool Truncated);

// Caller serializes access. Time is PresentMon's monotonically increasing capture clock, in ms.
public sealed class FrameWindow(int processId)
{
    private readonly Queue<PresentMonFrame> frames = new();
    private double latest = -1;
    private double truncatedAt = double.NegativeInfinity;
    public int Count => frames.Count;
    public void Add(PresentMonFrame frame)
    {
        if (frame.ProcessId != processId || !double.IsFinite(frame.StartMilliseconds) ||
            frame.StartMilliseconds < 0 || frame.StartMilliseconds < latest - 60000 || !double.IsFinite(frame.CpuFrameMilliseconds) ||
            frame.CpuFrameMilliseconds <= 0 || string.IsNullOrEmpty(frame.SwapChain)) return;
        latest = Math.Max(latest, frame.StartMilliseconds);
        frames.Enqueue(frame);
        while (frames.Count > 0 && frames.Peek().StartMilliseconds < latest - 60000) frames.Dequeue();
        while (frames.Count > 60000) { truncatedAt = latest; frames.Dequeue(); }
    }
    public FrameStatistics Read()
    {
        if (frames.Count == 0) return new(null, 0, null, null, null, false);
        // Different swapchains may complete out of timestamp order; filter by time, not just arrival order.
        var chain = frames.Where(f => f.StartMilliseconds >= latest - 60000).GroupBy(f => f.SwapChain)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First();
        var values = chain.Select(f => f.CpuFrameMilliseconds).OrderDescending().ToArray();
        var recent = chain.Where(f => f.StartMilliseconds >= latest - 1000).ToArray();
        double? milliseconds = recent.Length >= 2 ? recent.Average(f => f.CpuFrameMilliseconds) : null;
        var truncated = latest - truncatedAt <= 60000;
        double? low = values.Length >= 100 && !truncated ? 1000 / values.Take((int)Math.Ceiling(values.Length * .01)).Average() : null;
        return new(chain.Key, values.Length, milliseconds.HasValue ? 1000 / milliseconds : null, low, milliseconds, truncated);
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
