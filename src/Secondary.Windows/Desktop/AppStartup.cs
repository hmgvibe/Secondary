using System;
using Microsoft.Win32;

namespace Secondary.Windows;

public interface IAppStartup
{
    bool Enabled { get; }
    void Set(bool enabled);
}

// "登入時啟動": a per-user Run entry, so no administrator rights are needed. Off by default.
// Launches the main app silently into the tray via --background.
public sealed class AppStartup : IAppStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "Secondary";
    private readonly string executable;
    private readonly string command;

    public AppStartup(string? executablePath = null)
    {
        executable = executablePath ?? Environment.ProcessPath ?? string.Empty;
        command = $"\"{executable}\" --background";
    }

    public bool Enabled
    {
        get
        {
            if (executable.Length < 2) return false;
            using var run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Value) is string existing
                && existing.Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Set(bool enabled)
    {
        if (executable.Length < 2) return;
        using var run = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) run.SetValue(Value, command);
        else run.DeleteValue(Value, false);
    }
}
