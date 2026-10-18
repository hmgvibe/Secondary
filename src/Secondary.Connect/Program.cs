using System;
using System.IO;
using System.Net.Sockets;
using System.Windows.Forms;
using Secondary.Shared;
using Secondary.Shared.Connection;

namespace Secondary.Connect;

internal static class Program
{
    // Arguments: [--background] (started at sign-in: no greeting) [--data=<folder>] (a separate profile, for tests)
    // [--startup-check=<file>] (packaging: build the tray in a throwaway profile, write a receipt and exit without listening)
    [STAThread]
    private static int Main(string[] args)
    {
        var background = false;
        string? data = null, check = null;
        foreach (var argument in args)
        {
            if (argument == "--background") background = true;
            else if (argument.StartsWith("--data=", StringComparison.Ordinal) && argument.Length > 7) data = Path.GetFullPath(argument[7..]);
            else if (argument.StartsWith("--startup-check=", StringComparison.Ordinal) && argument.Length > 16) check = Path.GetFullPath(argument[16..]);
            else return 2;
        }
        if (check is not null) return StartupCheck(check);
        var folder = data ?? Path.Combine(AppPaths.Current.DataDirectory, "Connect");
        Directory.CreateDirectory(folder);
        using var instance = SingleInstanceCoordinator.Acquire(Path.Combine(folder, "devices.json"));
        if (instance is null)
        {
            // A second start shows the running helper's status instead of starting another.
            SingleInstanceCoordinator.ShowExistingAsync(Path.Combine(folder, "devices.json")).GetAwaiter().GetResult();
            return 0;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var identity = ConnectionIdentity.LoadOrCreate(Path.Combine(folder, "identity.pfx"), Environment.MachineName);
        var devices = new PairedStore(Path.Combine(folder, "devices.json"));
        var server = new HelperServer(identity, devices, Environment.MachineName);
        string? problem = null;
        try { server.Start(); }
        catch (SocketException error)
        {
            AppDiagnostics.Write("connect-listen", error);
            problem = $"無法使用連接埠 {ConnectionProtocol.TcpPort}：{error.Message}。可能有另一個程式正在使用；請結束後重新啟動連接助手。";
        }
        var shortcuts = new ShortcutService(Path.Combine(folder, "shortcuts.json"));
        using var tray = new ConnectTray(server, devices, new SignInStartup(), problem ?? devices.LoadError ?? (identity.Recreated ? "連線身分已重新建立，之前配對的設備需要重新配對。" : null),
            shortcuts: shortcuts);
        instance.Listen(() => tray.Invoke(tray.ShowStatus));
        if (!background || problem is not null) tray.ShowStatus();
        Application.Run(tray.Context);
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        tray.Games.StopAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static int StartupCheck(string receipt)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Secondary.Connect.StartupCheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            using var identity = ConnectionIdentity.LoadOrCreate(Path.Combine(folder, "identity.pfx"), Environment.MachineName);
            var devices = new PairedStore(Path.Combine(folder, "devices.json"));
            var server = new HelperServer(identity, devices, Environment.MachineName);
            using (var tray = new ConnectTray(server, devices, new SignInStartup(), showIcon: false, shortcuts: new ShortcutService(Path.Combine(folder, "shortcuts.json")))) tray.Build();
            File.WriteAllText(receipt, $"{{ \"Status\": \"Started\", \"Version\": \"{typeof(Program).Assembly.GetName().Version}\" }}");
            return 0;
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
