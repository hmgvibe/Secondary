using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Secondary.Core;

namespace Secondary.Connect;

// The device's hotkey, registered on the main computer only while that device is connected, so pressing it in a
// game switches the device in or out of game mode. A hidden window on the tray's thread receives WM_HOTKEY.
public sealed class HelperHotkey : NativeWindow, IDisposable
{
    private const string Idle = "快捷键：等待设备连接";
    private readonly Action pressed;
    private readonly HotkeyBinding binding;
    public string? Gesture { get; private set; }
    public string Status { get; private set; } = Idle;

    public HelperHotkey(Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams());
        binding = new HotkeyBinding((id, gesture) => RegisterHotKey(Handle, id, gesture.Modifiers | 0x4000, gesture.VirtualKey), id => UnregisterHotKey(Handle, id));
    }

    // Returns whether the keys work on the main computer, and a sentence for the device's settings.
    public (bool Active, string Text) Set(string? text)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture) || gesture is null)
        {
            Clear();
            Status = "快捷键：设备送来的快捷键格式无效";
            return (false, "主机无法使用这个快捷键格式。");
        }
        if (!binding.Set(gesture))
        {
            Clear();
            Status = $"快捷键：{gesture} 已被主机上的其他程序使用";
            return (false, $"主机上的 {gesture} 已被其他程序使用；请改用其他快捷键。");
        }
        Gesture = gesture.ToString();
        Status = $"快捷键：{Gesture}（按下时设备进出游戏模式）";
        return (true, $"在主机按 {Gesture} 也能进出游戏模式。");
    }

    public void Clear()
    {
        binding.Dispose();
        Gesture = null;
        Status = Idle;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && binding.ActiveId != 0 && m.WParam.ToInt32() == binding.ActiveId) pressed();
        else base.WndProc(ref m);
    }

    // Tests post the same message the system sends when the keys are pressed.
    public int ActiveId => binding.ActiveId;

    public void Dispose()
    {
        binding.Dispose();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
