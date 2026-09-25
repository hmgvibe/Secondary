using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace Secondary.App;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    public TrayService(MainWindow window, Action exit)
    {
        menu.Renderer = new TrayMenuRenderer(window);
        menu.ShowImageMargin = false; menu.ShowCheckMargin = false;
        menu.Font = new Font("Segoe UI",10.5f);
        menu.Padding = new Forms.Padding(6);
        menu.SizeChanged += (_, _) =>
        {
            if (menu.Width < 24 || menu.Height < 24) return;
            using var outline = TrayMenuRenderer.Rounded(new RectangleF(0,0,menu.Width,menu.Height),10*menu.DeviceDpi/96f);
            var old = menu.Region; menu.Region = new Region(outline); old?.Dispose();
        };
        menu.Items.Add("顯示介面", null, (_, _) => window.ShowInterface());
        menu.Items.Add("隱藏介面", null, (_, _) => window.Hide());
        menu.Items.Add("選擇顯示器", null, (_, _) => window.ShowPicker());
        menu.Items.Add("返回一般視窗", null, (_, _) => { window.ReturnToWindow(); window.ShowInterface(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        var game = menu.Items.Add("進入遊戲模式", null, (_, _) => window.ToggleGame());
        menu.Opening += (_, _) => game.Text = window.IsGame ? "退出遊戲模式" : "進入遊戲模式";
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 Secondary", null, (_, _) => { window.AllowClose = true; window.Close(); exit(); });
        foreach (Forms.ToolStripItem item in menu.Items)
            if (item is not Forms.ToolStripSeparator) item.Padding = new Forms.Padding(12,8,20,8);
        icon = new Forms.NotifyIcon { Text = "Secondary · 副屏", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => window.ShowInterface();
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }
}
