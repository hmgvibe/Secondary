using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Secondary.Shared.Connection;

namespace Secondary.Linux;

// Settings card for connection mode, matching the Windows card: search, pair with a code, connect, disconnect, forget.
public sealed class ConnectionPanel : Border
{
    private readonly ConnectionController? controller;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly TextBlock status = Label(14), search = Label(13), pairStatus = Label(13), identity = Label(12);
    private readonly StackPanel list = new() { Margin = new Thickness(0, 4, 0, 8), Spacing = 8 };
    private readonly Button searchButton = new() { Content = "搜索主机", Margin = new Thickness(0, 0, 12, 8) };
    private readonly Button disconnectButton = new() { Content = "断开连接", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox hostInput = new() { MaxLength = 260 };
    private readonly TextBox codeInput = new() { MaxLength = 7, FontSize = 18 };
    private readonly Button pairButton = new() { Content = "配对并连接", Margin = new Thickness(0, 12, 0, 0) };
    private bool searched, pairing;
    // Avalonia styles match the exact type; this keeps the shared "Border.card" style applying to the subclass.
    protected override Type StyleKeyOverride => typeof(Border);

    public ConnectionPanel(ConnectionController? controller, Func<string, string, string, Task<bool>> confirm, string? unavailable = null)
    {
        this.controller = controller;
        this.confirm = confirm;
        Classes.Add("card");
        AutomationProperties.SetName(this, "连接模式");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "连接模式", Classes = { "title" } });
        content.Children.Add(Text(Label(14), "没有副屏时，用这台设备显示主机的数据。主机需执行「Secondary 连接助手」，两台在同一个区域网络；数据经加密，只在配对过的设备之间传输。", new Thickness(0, 6, 0, 16)));
        Child = content;
        if (controller is null)
        {
            content.Children.Add(Text(status, unavailable ?? "连接模式目前无法使用。", default));
            return;
        }
        AutomationProperties.SetName(status, "连接状态");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        content.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(searchButton); buttons.Children.Add(disconnectButton);
        content.Children.Add(buttons);
        content.Children.Add(search);
        content.Children.Add(list);
        content.Children.Add(Text(Label(14), "配对新的主机", new Thickness(0, 8, 0, 6)));
        content.Children.Add(Text(Label(13), "在主机的连接助手系统托盘菜单按「开始配对」，再输入界面上的 6 位数配对码。搜索不到时，可直接输入主机的 IP。", new Thickness(0, 0, 0, 10)));
        AutomationProperties.SetName(hostInput, "主机 IP 位址");
        AutomationProperties.SetName(codeInput, "6 位数配对码");
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*") };
        var code = Field("配对码", codeInput);
        Grid.SetColumn(code, 2);
        fields.Children.Add(Field("主机 IP", hostInput)); fields.Children.Add(code);
        content.Children.Add(fields);
        content.Children.Add(pairButton);
        AutomationProperties.SetLiveSetting(pairStatus, AutomationLiveSetting.Polite);
        content.Children.Add(Text(pairStatus, "", new Thickness(0, 8, 0, 0)));
        content.Children.Add(Text(identity, "本机识别码：" + ConnectionIdentity.Short(controller.Fingerprint) + " · " + controller.DeviceName, new Thickness(0, 16, 0, 0)));
        searchButton.Click += async (_, _) => await SearchAsync();
        disconnectButton.Click += (_, _) => controller.Disconnect();
        pairButton.Click += async (_, _) => await PairAsync();
        codeInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await PairAsync(); };
        controller.Changed += () => Dispatcher.UIThread.Post(UpdateView);
        UpdateView();
    }

    public string StatusText => status.Text ?? "";
    public string SearchText => search.Text ?? "";
    public string PairText => pairStatus.Text ?? "";
    public TextBox HostInput => hostInput;
    public TextBox CodeInput => codeInput;
    public Button DisconnectButton => disconnectButton;
    public StackPanel Rows => list;

    // The window calls this when settings opens, so helpers are looked for once without a click.
    public Task SearchOnceAsync() => searched ? Task.CompletedTask : SearchAsync();
    public async Task SearchAsync()
    {
        if (controller is null || controller.Searching) return;
        searched = true;
        await controller.SearchAsync();
    }

    public async Task PairAsync()
    {
        if (controller is null || pairing) return;
        if (!ConnectionController.TryParseAddress(hostInput.Text, out var host, out var port)) { pairStatus.Text = "请输入主机的 IP，例如 192.168.1.20。"; return; }
        pairing = true; pairButton.IsEnabled = false;
        pairStatus.Text = "正在配对…";
        try
        {
            var problem = await controller.PairAsync(host, port, codeInput.Text ?? "");
            pairStatus.Text = problem ?? "配对成功，正在连接。";
            if (problem is null) codeInput.Text = "";
        }
        finally { pairing = false; pairButton.IsEnabled = true; }
    }

    private void UpdateView()
    {
        if (controller is null) return;
        status.Text = controller.StatusLine + (controller.StoreProblem is { } problem ? "\n" + problem : "");
        search.Text = controller.SearchStatus;
        searchButton.IsEnabled = !controller.Searching;
        searchButton.Content = controller.Searching ? "搜索中…" : "搜索主机";
        disconnectButton.IsEnabled = controller.State != LinkState.Idle;
        list.Children.Clear();
        foreach (var entry in controller.Helpers)
        {
            var row = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(actions, Dock.Right);
            if (entry.Paired)
            {
                var connect = new Button { Content = entry.Connected ? "已连接" : "连接", IsEnabled = !entry.Connected };
                connect.Click += (_, _) => controller.Connect(entry.Fingerprint);
                var forget = new Button { Content = "移除配对" };
                forget.Click += async (_, _) =>
                {
                    if (await confirm("移除配对", $"移除与「{entry.Name}」的配对？连接助手那边也会一并移除，之后需要重新配对。", "移除")) controller.Forget(entry.Fingerprint);
                };
                AutomationProperties.SetName(connect, "连接到 " + entry.Name);
                AutomationProperties.SetName(forget, "移除与 " + entry.Name + " 的配对");
                actions.Children.Add(connect); actions.Children.Add(forget);
            }
            else
            {
                var pair = new Button { Content = "配对…" };
                pair.Click += (_, _) =>
                {
                    hostInput.Text = entry.Port == ConnectionProtocol.TcpPort ? entry.Address : entry.Address + ":" + entry.Port;
                    codeInput.Focus();
                    pairStatus.Text = "输入「" + entry.Name + "」的连接助手显示的配对码。";
                };
                AutomationProperties.SetName(pair, "与 " + entry.Name + " 配对");
                actions.Children.Add(pair);
            }
            row.Children.Add(actions);
            row.Children.Add(new TextBlock { Text = ConnectionController.Describe(entry), VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 });
            list.Children.Add(row);
        }
    }

    private static TextBlock Label(double size) => new() { FontSize = size, Classes = { "label" } };
    private static TextBlock Text(TextBlock block, string text, Thickness margin) { block.Text = text; block.Margin = margin; return block; }
    private static StackPanel Field(string label, TextBox box)
    {
        var field = new StackPanel();
        field.Children.Add(Text(Label(13), label, new Thickness(0, 0, 0, 4)));
        field.Children.Add(box);
        return field;
    }
}
