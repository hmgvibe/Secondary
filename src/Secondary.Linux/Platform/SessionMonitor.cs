using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Secondary.Shared;
using Tmds.DBus.Protocol;

namespace Secondary.Linux.Platform;

// Lock (GNOME screen shield) and sleep (systemd-logind) stop home resources, as on Windows.
// Missing services leave the app running as if neither ever happens.
public sealed class SessionMonitor : IDisposable
{
    private readonly List<IDisposable> watches = new();
    private readonly List<DBusConnection> connections = new();
    public event Action<bool>? LockChanged;
    public event Action<bool>? SleepChanged;

    public async Task StartAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WatchAsync(DBusAddress.Session, "org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver", "ActiveChanged", value => LockChanged?.Invoke(value)).ConfigureAwait(false);
        await WatchAsync(DBusAddress.System, "org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", "PrepareForSleep", value => SleepChanged?.Invoke(value)).ConfigureAwait(false);
    }

    private async Task WatchAsync(string? address, string sender, string path, string @interface, string signal, Action<bool> handler)
    {
        if (address is null) return;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            watches.Add(await connection.WatchSignalAsync(sender, path, @interface, signal, (m, _) => m.GetBodyReader().ReadBool(),
                (Notification<bool> n) => { if (n.HasValue) handler(n.Value); }, ObserverFlags.None, false, null).ConfigureAwait(false));
            connections.Add(connection);
        }
        catch (Exception error) when (error is DBusExceptionBase or System.IO.IOException)
        { AppDiagnostics.Write("session-monitor", error); connection.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var watch in watches) watch.Dispose();
        foreach (var connection in connections) connection.Dispose();
        watches.Clear(); connections.Clear();
    }
}
