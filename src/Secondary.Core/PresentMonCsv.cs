using System.Globalization;
using System.Text;

namespace Secondary.Core;

public sealed record PresentMonFrame(int ProcessId, string SwapChain, double StartMilliseconds, double CpuFrameMilliseconds);

// Parse the explicitly requested v2 schema by header, never by column position.
public sealed class PresentMonCsv(int processId)
{
    private string[]? header;
    public bool HasHeader => header is not null;
    public PresentMonFrame? Read(string line)
    {
        if (line.Length > 16384) return null;
        var cells = Split(line.TrimStart('\uFEFF'));
        if (cells is null) return null;
        if (cells.Contains("ProcessID") && (cells.Contains("CPUStartTime") || cells.Contains("CPUStartQPCTime")) &&
            (cells.Contains("FrameTime") || (cells.Contains("MsCPUBusy") && cells.Contains("MsCPUWait"))) && cells.Contains("SwapChainAddress"))
        { header = cells; return null; }
        if (header is null || cells.Length != header.Length) return null;
        string Field(string name) => cells[Array.IndexOf(header, name)];
        if (!int.TryParse(Field("ProcessID"), out var pid) || pid != processId) return null;
        bool Number(string name, out double value) => double.TryParse(Field(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && value >= 0;
        var qpc = header.Contains("CPUStartQPCTime");
        if (!Number(qpc ? "CPUStartQPCTime" : "CPUStartTime", out var start)) return null;
        double interval;
        if (header.Contains("FrameTime")) { if (!Number("FrameTime", out interval)) return null; }
        else
        {
            if (!Number("MsCPUBusy", out var busy) || !Number("MsCPUWait", out var wait)) return null;
            interval = busy + wait;
        }
        // Pinned 2.6.0 v2 default CPUStartTime is seconds; --qpc_time_ms is explicitly milliseconds.
        if (!qpc) start *= 1000;
        if (!double.IsFinite(start) || !double.IsFinite(interval) || interval <= 0) return null;
        var chain = Field("SwapChainAddress");
        return string.IsNullOrWhiteSpace(chain) ? null : new(pid, chain, start, interval);
    }

    private static string[]? Split(string line)
    {
        var fields = new List<string>();
        var text = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { text.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { fields.Add(text.ToString()); text.Clear(); }
            else text.Append(c);
        }
        if (quoted) return null;
        fields.Add(text.ToString());
        return fields.ToArray();
    }
}
