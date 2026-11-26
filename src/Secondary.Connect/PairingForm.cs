using System;
using System.Drawing;
using System.Windows.Forms;
using Secondary.Shared.Connection;

namespace Secondary.Connect;

// Shows the pairing code while pairing is open. Closing the window ends pairing.
public sealed class PairingForm : Form
{
    private readonly HelperServer server;
    private readonly Label code = new() { AutoSize = true, Font = new Font("Consolas", 40f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 8), AccessibleName = "配对码" };
    private readonly Label remaining = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 12, 0, 0), AccessibleName = "配对状态" };
    private readonly Button close = new() { Text = "关闭", AutoSize = true, MinimumSize = new Size(110, 40), Margin = new Padding(0, 20, 0, 0) };
    private readonly Timer clock = new() { Interval = 1000 };
    private readonly Action changed;

    public PairingForm(HelperServer server)
    {
        this.server = server;
        Text = "配对新设备 · Secondary 连接助手";
        Font = new Font("Segoe UI", 10.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(440, 0), Text = "在另一台设备开启 Secondary 的「设置 → 连接模式」，选择这台主机后输入下方配对码。" });
        layout.Controls.Add(code);
        layout.Controls.Add(remaining);
        layout.Controls.Add(status);
        layout.Controls.Add(close);
        Controls.Add(layout);
        AcceptButton = CancelButton = close;
        close.Click += (_, _) => Close();
        server.StartPairing();
        changed = () => { if (IsHandleCreated && !IsDisposed) BeginInvoke(UpdateView); };
        server.Changed += changed;
        clock.Tick += (_, _) => UpdateView();
        clock.Start();
        UpdateView();
    }

    public string CodeText => code.Text;
    public string StatusText => status.Text;

    public void UpdateView()
    {
        var current = server.PairingCode;
        code.Text = current is null ? "— — —" : current[..3] + " " + current[3..];
        var left = server.PairingExpires - DateTimeOffset.UtcNow;
        remaining.Text = current is null ? "" : left > TimeSpan.Zero ? $"{(int)left.TotalMinutes}:{left.Seconds:00} 后失效" : "";
        status.Text = current is null && server.PairingStatus == "等待设备输入配对码" ? "配对码已过期；关闭后可重新开始配对。" : server.PairingStatus;
        close.Text = current is null ? "完成" : "关闭";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        clock.Stop();
        server.Changed -= changed;
        if (server.PairingCode is not null) server.StopPairing();
        base.OnFormClosed(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) clock.Dispose();
        base.Dispose(disposing);
    }
}
