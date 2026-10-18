using System;
using System.Drawing;
using System.Windows.Forms;

namespace Secondary.Connect;

// Shown on the main computer when a device asks to run a shortcut that has not been confirmed here, or changed
// since. Enter does not confirm; closing, Esc or two minutes without an answer cancel.
public sealed class ShortcutConfirmForm : Form
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly Button run = new() { Text = "確認並執行", AutoSize = true, MinimumSize = new Size(130, 40), Margin = new Padding(0, 0, 12, 0) };
    private readonly Button cancel = new() { Text = "取消", AutoSize = true, MinimumSize = new Size(110, 40) };
    private readonly Timer expiry = new() { Interval = (int)Timeout.TotalMilliseconds };
    private bool decided;
    public event Action<bool>? Decided;
    public string DetailsText { get; }
    public string Heading { get; }

    public ShortcutConfirmForm(ShortcutConfirmation request)
    {
        Text = "確認執行 · Secondary 連接助手";
        Font = new Font("Segoe UI", 10.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);
        Heading = $"「{request.Device}」要求在這臺電腦執行「{request.Name}」";
        DetailsText = request.Details;
        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(560, 0), Font = new Font("Segoe UI", 13f, FontStyle.Bold), Text = Heading });
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 8),
            Text = "這是首次執行，或內容已變更。請確認以下目標與步驟，只執行你信任的內容。確認後會記住選擇；執行步驟或腳本內容變更時才會再次詢問。2 分鐘內未回應即取消。" });
        var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 560, Height = 220, Text = DetailsText.Replace("\n", Environment.NewLine), AccessibleName = "執行內容", TabIndex = 2 };
        layout.Controls.Add(details);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(run); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = cancel;
        // Focus starts on 取消, with nothing selected, so a stray key press never confirms.
        cancel.TabIndex = 0; run.TabIndex = 1;
        Shown += (_, _) => { details.SelectionLength = 0; ActiveControl = cancel; };
        run.Click += (_, _) => Decide(true);
        cancel.Click += (_, _) => Decide(false);
        expiry.Tick += (_, _) => Decide(false);
        FormClosed += (_, _) => { expiry.Dispose(); Decide(false); };
        expiry.Start();
    }

    public void Decide(bool confirmed)
    {
        if (decided) return;
        decided = true;
        expiry.Stop();
        Decided?.Invoke(confirmed);
        if (!IsDisposed) Close();
    }
}
