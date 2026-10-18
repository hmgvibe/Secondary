using System;
using System.Drawing;
using System.Windows.Forms;
using Secondary.Shared.Connection;

namespace Secondary.Connect;

// Shows the pairing code while pairing is open. Closing the window ends pairing.
public sealed class PairingForm : Form
{
    private readonly HelperServer server;
    private readonly Label code = new() { AutoSize = true, Font = new Font("Consolas", 40f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 8), AccessibleName = "配對碼" };
    private readonly Label remaining = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 12, 0, 0), AccessibleName = "配對狀態" };
    private readonly Button close = new() { Text = "關閉", AutoSize = true, MinimumSize = new Size(110, 40), Margin = new Padding(0, 20, 0, 0) };
    private readonly Timer clock = new() { Interval = 1000 };
    private readonly Action changed;

    public PairingForm(HelperServer server)
    {
        this.server = server;
        Text = "配對新設備 · Secondary 連接助手";
        Font = new Font("Segoe UI", 10.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(440, 0), Text = "在另一臺設備開啟 Secondary 的「設定 → 連線模式」，選擇這臺主電腦後輸入下方配對碼。" });
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
        remaining.Text = current is null ? "" : left > TimeSpan.Zero ? $"{(int)left.TotalMinutes}:{left.Seconds:00} 後失效" : "";
        status.Text = current is null && server.PairingStatus == "等待設備輸入配對碼" ? "配對碼已過期；關閉後可重新開始配對。" : server.PairingStatus;
        close.Text = current is null ? "完成" : "關閉";
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
