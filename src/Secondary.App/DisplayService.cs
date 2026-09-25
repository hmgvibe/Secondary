using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Secondary.Core;
using Forms = System.Windows.Forms;

namespace Secondary.App;

public sealed class DisplayService : IDisposable
{
    public event Action? Changed;
    public DisplayService() => SystemEvents.DisplaySettingsChanged += OnChanged;
    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();
    public DisplayInfo[] GetDisplays() => Forms.Screen.AllScreens.Select(screen =>
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        var found = EnumDisplayDevices(screen.DeviceName, 0, ref device, 1);
        var id = found && !string.IsNullOrWhiteSpace(device.DeviceId) ? device.DeviceId : screen.DeviceName;
        var name = screen.DeviceName;
        return new DisplayInfo(id, name, screen.Primary, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
    }).ToArray();
    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnChanged;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
}
