using System;
using System.Drawing;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Connect;

// The helper's only interface: a tray icon whose menu shows the connection, opens pairing and manages devices.
public sealed class ConnectTray : IDisposable
{
    private readonly HelperServer server;
    private readonly PairedStore devices;
    private readonly IStartupSetting startup;
    private readonly string? problem;
    private readonly Control marshal = new();
    private readonly NotifyIcon icon;
    public ContextMenuStrip Menu { get; } = new();
    public ApplicationContext Context { get; } = new();
    public PairingForm? PairingWindow { get; private set; }
    public GameService Games { get; }
    public MediaService Media { get; }
    public ShortcutService Shortcuts { get; }
    public HelperHotkey Hotkey { get; }
    public ShortcutConfirmForm? ConfirmWindow { get; private set; }
    // Tests replace the confirmation dialog.
    public Func<string, bool> Confirm { get; set; } = text => MessageBox.Show(text, "Secondary 連接助手", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    // Tests pass showIcon: false so no icon appears in the real notification area.
    public ConnectTray(HelperServer server, PairedStore devices, IStartupSetting startup, string? problem = null, bool showIcon = true,
        GameService? games = null, MediaService? media = null, ShortcutService? shortcuts = null)
    {
        this.server = server; this.devices = devices; this.startup = startup; this.problem = problem;
        marshal.CreateControl();
        Games = games ?? new GameService();
        Media = media ?? new MediaService();
        Shortcuts = shortcuts ?? new ShortcutService(System.IO.Path.Combine(Secondary.Shared.AppPaths.Current.DataDirectory, "Connect", "shortcuts.json"));
        Shortcuts.Confirm ??= ShowConfirmation;
        Shortcuts.Busy = () => Games.Current is not null;
        Hotkey = new HelperHotkey(() => _ = server.SendToActiveAsync(Message("toggle-game")));
        server.MessageHandler = Handle;
        Menu.Font = new Font("Segoe UI", 10.5f);
        Menu.ShowImageMargin = false;
        Menu.Opening += (_, _) => Build();
        Build();
        icon = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = Menu, Visible = showIcon, Text = TrayText };
        icon.DoubleClick += (_, _) => ShowStatus();
        // The hotkey belongs to the connected device; without one it is released for other programs.
        server.Changed += () => Invoke(() => { icon.Text = TrayText; if (server.ActiveDevice is null) Hotkey.Clear(); });
        devices.Changed += () => Invoke(() => icon.Text = TrayText);
    }

    public string StatusText => server.ActiveDevice is { } device ? $"已連線：{device}（{server.ActiveAddress}）" : "等待設備連線";
    // NotifyIcon text is limited to 127 characters.
    public string TrayText { get { var text = "Secondary 連接助手 · " + StatusText; return text.Length > 127 ? text[..127] : text; } }

    public void Invoke(Action action)
    {
        if (marshal.IsDisposed) return;
        if (marshal.InvokeRequired) marshal.BeginInvoke(action); else action();
    }

    private async Task Handle(string device, JsonObject message, FrameChannel channel, System.Threading.CancellationToken session)
    {
        var type = TypeOf(message);
        if (type.StartsWith("media-", StringComparison.Ordinal)) { await Media.Handle(message, channel, session).ConfigureAwait(false); return; }
        if (type.StartsWith("shortcut", StringComparison.Ordinal)) { await Shortcuts.Handle(server.ActiveDevice ?? "設備", message, channel, session).ConfigureAwait(false); return; }
        if (type != "hotkey") { await Games.Handle(message, channel, session).ConfigureAwait(false); return; }
        var result = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gesture = Text(message, "gesture");
        Invoke(() => result.TrySetResult(Hotkey.Set(gesture)));
        var (active, text) = await result.Task.WaitAsync(TimeSpan.FromSeconds(5), session).ConfigureAwait(false);
        await channel.SendAsync(RemoteGame.HotkeyStatus(active, text), session).ConfigureAwait(false);
    }

    // The confirmation appears on this computer's screen; the device only waits.
    private Task<bool> ShowConfirmation(ShortcutConfirmation request, System.Threading.CancellationToken token)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Invoke(() =>
        {
            ConfirmWindow?.Decide(false);
            var form = ConfirmWindow = new ShortcutConfirmForm(request);
            form.Decided += confirmed => { answer.TrySetResult(confirmed); if (ConfirmWindow == form) ConfirmWindow = null; };
            token.Register(() => Invoke(() => form.Decide(false)));
            form.Show();
            form.Activate();
        });
        return answer.Task;
    }

    // Rebuilt each time the menu opens; tests call it directly.
    public void Build()
    {
        Menu.Items.Clear();
        Item(StatusText, null).Enabled = false;
        Item($"主電腦：{server.Name} · {ConnectionIdentity.Short(server.Fingerprint)}", null).Enabled = false;
        Item(Games.Status, null).Enabled = false;
        Item(Hotkey.Status, null).Enabled = false;
        Item(Media.Status, null).Enabled = false;
        Item(Shortcuts.Status, null).Enabled = false;
        foreach (var note in new[] { problem, server.DiscoveryError, devices.SaveError }.Where(n => n is not null)) Item("⚠ " + note, null).Enabled = false;
        Menu.Items.Add(new ToolStripSeparator());
        Item("開始配對…", (_, _) => OpenPairing());
        var list = new ToolStripMenuItem("已配對設備") { Padding = new Padding(12, 8, 20, 8) };
        var peers = devices.Peers;
        if (peers.Count == 0) list.DropDownItems.Add(new ToolStripMenuItem("（尚未配對任何設備）") { Enabled = false });
        foreach (var peer in peers)
        {
            var seen = peer.LastSeen is { } last ? "上次連線 " + last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "尚未連線";
            list.DropDownItems.Add(new ToolStripMenuItem($"移除「{peer.Name}」… · {seen}", null, (_, _) => Remove(peer.Fingerprint)));
        }
        Menu.Items.Add(list);
        var signIn = new ToolStripMenuItem("登入時啟動") { Checked = startup.Enabled, CheckOnClick = true, Padding = new Padding(12, 8, 20, 8) };
        signIn.CheckedChanged += (_, _) =>
        {
            try { startup.Set(signIn.Checked); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            { icon?.ShowBalloonTip(5000, "Secondary 連接助手", "無法變更登入時啟動：" + error.Message, ToolTipIcon.Warning); }
        };
        Menu.Items.Add(signIn);
        Menu.Items.Add(new ToolStripSeparator());
        Item("結束連接助手", (_, _) => Context.ExitThread());
    }
    private ToolStripItem Item(string text, EventHandler? click)
    {
        var item = Menu.Items.Add(text, null, click);
        item.Padding = new Padding(12, 8, 20, 8);
        return item;
    }

    public void OpenPairing()
    {
        if (PairingWindow is { IsDisposed: false } open) { open.Activate(); return; }
        PairingWindow = new PairingForm(server);
        PairingWindow.FormClosed += (_, _) => PairingWindow = null;
        PairingWindow.Show();
        PairingWindow.Activate();
    }

    public bool Remove(string fingerprint)
    {
        if (devices.Find(fingerprint) is not { } peer) return false;
        if (!Confirm($"移除「{peer.Name}」的配對？\n若它正在連線會立即中斷，之後需要重新配對才能連線。")) return false;
        server.Forget(fingerprint);
        return true;
    }

    public void ShowStatus()
    {
        var text = problem ?? (server.ActiveDevice is null
            ? "正在背景執行，等待設備連線。在另一臺設備的「設定 → 連線模式」搜尋這臺主電腦；首次連線請先在這裡按「開始配對」。"
            : StatusText);
        icon.ShowBalloonTip(6000, "Secondary 連接助手", text, problem is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        PairingWindow?.Close();
        ConfirmWindow?.Decide(false);
        Shortcuts.CancelAll();
        Media.Stop();
        Hotkey.Dispose();
        icon.Visible = false;
        icon.Dispose();
        Menu.Dispose();
        marshal.Dispose();
        Context.Dispose();
    }
}
