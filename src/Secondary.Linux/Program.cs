using System;
using System.IO;
using System.Linq;
using Avalonia;
using Secondary.Shared;

namespace Secondary.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LaunchOptions options;
        try { options = LaunchOptions.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine("secondary: " + error.Message); return 2; }
        // A startup check never touches the everyday profile.
        var profile = options.Profile ?? (options.StartupCheck is null ? null
            : Path.Combine(Path.GetDirectoryName(options.StartupCheck)!, ".secondary-startup-" + Guid.NewGuid().ToString("N")));
        var paths = profile is null ? AppPaths.Current : new AppPaths(profile, Path.Combine(profile, "logs"));
        var instance = SingleInstanceCoordinator.Acquire(paths.SettingsPath);
        if (instance is null)
        {
            // A running instance owns the settings; forward the request and leave.
            var command = options.ToggleGame ? InstanceCommand.ToggleGame : InstanceCommand.Show;
            if (SingleInstanceCoordinator.SendAsync(paths.SettingsPath, command).GetAwaiter().GetResult()) return 0;
            Console.Error.WriteLine("secondary: 已有執行中的 Secondary，但暫時無法回應。請從系統匣顯示介面。");
            return 1;
        }
        try
        {
            App.Startup = new(paths, instance, options);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            instance.Dispose();
            // Only the uniquely named startup-check profile is removed.
            if (options.StartupCheck is not null && options.Profile is null && profile is not null)
                try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

public sealed record LaunchOptions(bool ToggleGame, string? Profile, string? StartupCheck = null, int CheckSeconds = 0)
{
    public static LaunchOptions Parse(string[] args)
    {
        var toggle = false; string? profile = null, check = null; var seconds = 0;
        foreach (var arg in args)
        {
            if (arg == "--toggle-game" && !toggle) toggle = true;
            else if (arg == "--show") { }
            else if (arg.StartsWith("--profile=", StringComparison.Ordinal) && profile is null && arg.Length > 10)
                profile = Path.GetFullPath(arg[10..]);
            // Packaging verification: start, render, write a receipt and exit.
            else if (arg.StartsWith("--startup-check=", StringComparison.Ordinal) && check is null && arg.Length > 16)
                check = Path.GetFullPath(arg[16..]);
            // How long a startup check keeps running, for acceptance measurements (1–120 s).
            else if (arg.StartsWith("--check-seconds=", StringComparison.Ordinal) && seconds == 0 && int.TryParse(arg[16..], out var value) && value is >= 1 and <= 120)
                seconds = value;
            else throw new ArgumentException("不支援的參數：" + arg + "（可用 --toggle-game、--show、--profile=目錄）");
        }
        if (seconds > 0 && check is null) throw new ArgumentException("--check-seconds 只能與 --startup-check 一起使用");
        return new(toggle, profile, check, seconds);
    }
}

public sealed record StartupContext(AppPaths Paths, SingleInstanceCoordinator? Instance, LaunchOptions Options);
