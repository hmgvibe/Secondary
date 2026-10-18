using System;
using Microsoft.Win32;

namespace Secondary.Connect;

public interface IStartupSetting
{
    bool Enabled { get; }
    void Set(bool enabled);
}

// "登入時啟動": a per-user Run entry, so no administrator rights are needed. Off by default.
public sealed class SignInStartup : IStartupSetting
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Value = "Secondary Connect";
    public bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Value) is string command && command.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
    }
    public void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) run.SetValue(Value, $"\"{Environment.ProcessPath}\" --background");
        else run.DeleteValue(Value, false);
    }
}
