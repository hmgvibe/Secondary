using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Secondary.Core;

namespace Secondary.App;

public sealed class GlobalHotkey : IDisposable
{
    private readonly HwndSource source;
    private readonly Action action;
    private readonly HotkeyBinding binding;
    public GlobalHotkey(IntPtr handle, Action action)
    {
        source = HwndSource.FromHwnd(handle);
        this.action = action;
        binding = new HotkeyBinding((id, gesture) => RegisterHotKey(source.Handle, id, gesture.Modifiers | 0x4000, gesture.VirtualKey), id => UnregisterHotKey(source.Handle, id));
        source.AddHook(Hook);
    }
    public bool Set(HotkeyGesture gesture) => binding.Set(gesture);
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && binding.ActiveId != 0 && wParam.ToInt32() == binding.ActiveId)
        { handled = true; action(); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        binding.Dispose();
        source.RemoveHook(Hook);
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
