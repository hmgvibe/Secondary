using System;
using System.IO;

namespace Secondary.App;

// Bounded local operational records: never user text, targets, arguments or exception messages.
public static class AppDiagnostics
{
    private static readonly object gate = new();
    public static void Write(string operation, Exception error)
    {
        try
        {
            lock (gate)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Secondary", "Logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "operations.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation} {error.GetType().Name} HRESULT=0x{error.HResult:X8}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
