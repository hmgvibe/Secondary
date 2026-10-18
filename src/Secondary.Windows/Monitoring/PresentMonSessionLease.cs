using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Secondary.Monitoring;

// The lease records only our random session and owner identity. Recovery never enumerates/stops ETW globally.
internal sealed class PresentMonSessionLease : IDisposable
{
    internal sealed record Identity(string Session, int OwnerPid, long StartedUtcTicks);
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "CaptureSessions");
    private readonly string path;
    private bool cleaned;
    public PresentMonSessionLease(string session)
    {
        Directory.CreateDirectory(DirectoryPath);
        path = Path.Combine(DirectoryPath, session + ".json");
        using var owner = Process.GetCurrentProcess();
        File.WriteAllText(path, JsonSerializer.Serialize(new Identity(session, owner.Id, owner.StartTime.ToUniversalTime().Ticks)));
    }
    public void MarkCleaned() => cleaned = true;
    public void Dispose()
    {
        if (!cleaned) return;
        try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    internal static async Task RecoverAsync(Func<string, Task<bool>> stopSession, Action<string> status)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        // Bounded work even if the directory is damaged or a previous release left many files.
        var attempted = 0;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "Secondary-Game-*.json"))
        {
            if (++attempted > 32) break;
            try
            {
                if (new FileInfo(file).Length > 1024) continue;
                var identity = JsonSerializer.Deserialize<Identity>(File.ReadAllText(file));
                if (identity is null || identity.OwnerPid <= 0 || identity.StartedUtcTicks <= 0 ||
                    identity.Session != Path.GetFileNameWithoutExtension(file) ||
                    !identity.Session.StartsWith("Secondary-Game-", StringComparison.Ordinal) ||
                    identity.Session.Length != 47 || !Guid.TryParseExact(identity.Session[15..], "N", out _)) continue;
                bool alive;
                try { using var owner = Process.GetProcessById(identity.OwnerPid); alive = !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks; }
                catch (ArgumentException) { alive = false; }
                catch (InvalidOperationException) { alive = false; }
                if (alive) continue;
                if (await stopSession(identity.Session).ConfigureAwait(false)) File.Delete(file);
                else status("先前自身 ETW 會話清理未完成，已保留重試紀錄。");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            { status("自身幀捕獲恢復紀錄無法處理：" + error.Message); }
        }
    }
}
