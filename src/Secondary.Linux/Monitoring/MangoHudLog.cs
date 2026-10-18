using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Secondary.Core;

namespace Secondary.Linux.Monitoring;

// One MangoHud CSV log. The system-information lines come first; frame rows follow a header that
// names the columns, so columns are found by name, never by position.
public sealed class MangoHudCsv
{
    private int frametime = -1, elapsed = -1, columns;
    private double lastElapsed = double.NaN;
    private readonly List<double> gaps = [], frames = [];
    public bool HasHeader => frametime >= 0;
    public int Rows { get; private set; }
    // MangoHud writes one row per frame only with log_interval=0; otherwise each row samples one frame.
    public bool? PerFrame { get; private set; }

    public PresentMonFrame? Parse(string line, string source)
    {
        var fields = line.Split(',');
        if (!HasHeader)
        {
            var names = fields.Select(f => f.Trim().ToLowerInvariant()).ToArray();
            if (Array.IndexOf(names, "frametime") is var f and >= 0 && Array.IndexOf(names, "elapsed") is var e and >= 0)
            { frametime = f; elapsed = e; columns = names.Length; }
            return null;
        }
        if (fields.Length < columns ||
            !double.TryParse(fields[frametime], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) ||
            !double.TryParse(fields[elapsed], NumberStyles.Float, CultureInfo.InvariantCulture, out var nanoseconds) ||
            !double.IsFinite(milliseconds) || milliseconds <= 0 || !double.IsFinite(nanoseconds) || nanoseconds < 0) return null;
        // elapsed counts nanoseconds since the log started.
        var start = nanoseconds / 1e6;
        Rows++;
        if (PerFrame is null && double.IsFinite(lastElapsed) && start > lastElapsed)
        {
            gaps.Add(start - lastElapsed); frames.Add(milliseconds);
            if (gaps.Count >= 20) PerFrame = Median(gaps) <= Median(frames) * 2;
        }
        lastElapsed = start;
        return new PresentMonFrame(0, source, start, milliseconds);
    }
    private static double Median(List<double> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
}

// Follows the newest MangoHud log in Secondary's folder while a game writes it.
public sealed partial class MangoHudLog(string folder, Func<DateTime>? utcNow = null)
{
    public const long TailBytes = 4 * 1024 * 1024;
    private readonly Func<DateTime> utcNow = utcNow ?? (() => DateTime.UtcNow);
    private string? path;
    private long position;
    private MangoHudCsv csv = new();
    private readonly StringBuilder partial = new();
    private Decoder decoder = Encoding.UTF8.GetDecoder();
    private DateTime lastScan = DateTime.MinValue;
    public string Folder => folder;
    public string? Source { get; private set; }
    public string? CurrentFile => path;
    public FrameWindow Window { get; private set; } = new(0);
    public bool? PerFrame => csv.PerFrame;
    public bool HasHeader => csv.HasHeader;
    // Generation changes whenever a different log is followed, so the caller can clear its history.
    public int Generation { get; private set; }

    public static string DefaultFolder(Func<string, string?> environment, string home)
    {
        var data = environment("XDG_DATA_HOME");
        return Path.Combine(!string.IsNullOrEmpty(data) && Path.IsPathFullyQualified(data) ? data : Path.Combine(home, ".local", "share"), "secondary", "mangohud");
    }
    // The launch option for Steam and similar launchers. no_display keeps the overlay off the game screen.
    public static string LaunchOption(string folder) =>
        $"MANGOHUD_CONFIG=\"output_folder={folder},log_interval=0,autostart_log=1,no_display\" mangohud %command%";

    [GeneratedRegex(@"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$")]
    private static partial Regex Timestamp();
    public static string GameName(string file) => Timestamp().Replace(Path.GetFileNameWithoutExtension(file), "");

    // Reads whatever the game appended since the last call and returns the number of frames added.
    public int Poll(TimeSpan activeWithin)
    {
        var now = utcNow();
        if (now - lastScan >= TimeSpan.FromSeconds(2) || path is null)
        {
            lastScan = now;
            var newest = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*.csv").Where(f => !f.Name.EndsWith("_summary.csv", StringComparison.Ordinal))
                    .MaxBy(f => f.LastWriteTimeUtc) : null;
            // A log that stopped growing long ago belongs to a finished session, not the current game.
            if (newest is not null && newest.FullName != path && now - newest.LastWriteTimeUtc <= activeWithin) Follow(newest.FullName);
        }
        return path is null ? 0 : Read();
    }

    private void Follow(string file)
    {
        path = file;
        Source = GameName(file);
        Window = new FrameWindow(0);
        Generation++;
        LoadHeader();
    }
    // Reads the header from the start, then skips to the recent tail of a long log.
    private void LoadHeader()
    {
        position = 0; partial.Clear(); decoder = Encoding.UTF8.GetDecoder();
        csv = new MangoHudCsv();
        using var stream = Open(path!);
        if (stream is null) return;
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
        {
            for (var i = 0; i < 16 && !csv.HasHeader && reader.ReadLine() is { } line; i++) csv.Parse(line, Source!);
        }
        if (!csv.HasHeader) { position = 0; return; }
        if (stream.Length > TailBytes)
        {
            stream.Seek(stream.Length - TailBytes, SeekOrigin.Begin);
            // Drop the partial line the seek landed in.
            int next; long skipped = 0;
            while ((next = stream.ReadByte()) >= 0) { skipped++; if (next == '\n') break; }
            position = stream.Length - TailBytes + skipped;
        }
        else position = HeaderEnd(stream);
    }
    // Byte offset just after the header line.
    private static long HeaderEnd(FileStream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var line = new List<byte>();
        int next;
        while ((next = stream.ReadByte()) >= 0)
        {
            if (next != '\n') { line.Add((byte)next); continue; }
            var text = Encoding.UTF8.GetString(line.ToArray());
            line.Clear();
            if (text.Contains("frametime", StringComparison.OrdinalIgnoreCase) && text.Contains("elapsed", StringComparison.OrdinalIgnoreCase)) return stream.Position;
        }
        return stream.Position;
    }

    private int Read()
    {
        using var stream = Open(path!);
        if (stream is null) return 0;
        if (stream.Length < position) { Follow(path!); return Read(); }
        // MangoHud may not have written the header yet when the log is first seen.
        if (!csv.HasHeader) { LoadHeader(); if (!csv.HasHeader) return 0; }
        stream.Seek(position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var added = 0;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            position += count;
            partial.Append(chars, 0, decoder.GetChars(buffer, 0, count, chars, 0));
            var text = partial.ToString();
            var end = text.LastIndexOf('\n');
            if (end < 0) continue;
            partial.Clear().Append(text, end + 1, text.Length - end - 1);
            foreach (var line in text.AsSpan(0, end).EnumerateLines())
                if (csv.Parse(line.ToString(), Source!) is { } frame) { Window.Add(frame); added++; }
        }
        return added;
    }
    private static FileStream? Open(string file)
    {
        try { return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}
