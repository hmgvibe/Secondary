using System;
using System.IO;

namespace Secondary.Shared;

// Windows keeps %LOCALAPPDATA%\Secondary; Linux follows the XDG base directory specification.
public sealed record AppPaths(string DataDirectory, string LogsDirectory)
{
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string ShortcutsPath => Path.Combine(DataDirectory, "shortcuts.json");

    public static AppPaths Current { get; } = OperatingSystem.IsWindows()
        ? ForWindows(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        : ForXdg(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static AppPaths ForWindows(string localApplicationData)
    {
        var data = Path.Combine(localApplicationData, "Secondary");
        return new(data, Path.Combine(data, "Logs"));
    }

    public static AppPaths ForXdg(Func<string, string?> environment, string home)
    {
        // The specification ignores relative values.
        string Base(string variable, string fallback)
        {
            var value = environment(variable);
            return !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? value : Path.Combine(home, fallback);
        }
        return new(Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "secondary"),
            Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), "secondary", "logs"));
    }
}
