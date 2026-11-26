using System;
using System.Drawing;
using Secondary.Core;
using Forms = System.Windows.Forms;

namespace Secondary.App;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon = null!;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Secondary.Windows.IAppStartup startup = null!;
    public TrayService(MainWindow window, Action exit, Secondary.Windows.IAppStartup? startup = null)
    {
        this.startup = startup ?? new Secondary.Windows.AppStartup();
        ChineseLocale.Changed += OnLocaleChanged;
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
        var show = Item("显示界面", (_, _) => window.ShowInterface());
        var hide = Item("隐藏界面", (_, _) => window.Hide());
        var display = Item("选择显示器", (_, _) => window.ShowPicker());
        var windowed = Item("返回一般窗口", (_, _) => { window.ReturnToWindow(); window.ShowInterface(); });
        menu.Items.AddRange(new Forms.ToolStripItem[] { show, hide, display, windowed, new Forms.ToolStripSeparator() });
        var signIn = new Forms.ToolStripMenuItem(("登录时启动")) { Checked = this.startup!.Enabled, CheckOnClick = true };
        signIn.Tag = "登录时启动";
        signIn.CheckedChanged += (_, _) =>
        {
            try { this.startup!.Set(signIn.Checked); }
            catch (Exception error) { icon.ShowBalloonTip(5000, "Secondary", ("无法变更登录时启动：") + error.Message, Forms.ToolTipIcon.Warning); }
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(signIn);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("退出 Secondary", (_, _) => { window.AllowClose = true; window.Close(); exit(); }));
        foreach (Forms.ToolStripItem item in menu.Items)
            if (item is not Forms.ToolStripSeparator) item.Padding = new Forms.Padding(12,8,20,8);
        icon = new Forms.NotifyIcon { Text = "Secondary · 副屏", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => window.ShowInterface();
        menu.Opening += (_, _) =>
        {
            signIn.Checked = this.startup!.Enabled;
        };
    }
    private Forms.ToolStripMenuItem Item(string source, EventHandler handler)
    {
        var item = new Forms.ToolStripMenuItem((source)) { Tag = source };
        item.Click += handler;
        return item;
    }
    private void OnLocaleChanged()
    {
        if (menu.InvokeRequired) { try { menu.BeginInvoke((Action)OnLocaleChanged); } catch (Exception) { } return; }
        foreach (Forms.ToolStripItem item in menu.Items)
            if (item.Tag is string source) item.Text = (source);
    }
    public void Dispose()
    {
        ChineseLocale.Changed -= OnLocaleChanged;
        icon.Visible = false; icon.Dispose(); menu.Dispose();
    }
}
