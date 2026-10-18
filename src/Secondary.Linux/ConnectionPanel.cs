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
    private readonly Button searchButton = new() { Content = "搜尋主電腦", Margin = new Thickness(0, 0, 12, 8) };
    private readonly Button disconnectButton = new() { Content = "中斷連線", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox hostInput = new() { MaxLength = 260 };
    private readonly TextBox codeInput = new() { MaxLength = 7, FontSize = 18 };
    private readonly Button pairButton = new() { Content = "配對並連線", Margin = new Thickness(0, 12, 0, 0) };
    private bool searched, pairing;
    // Avalonia styles match the exact type; this keeps the shared "Border.card" style applying to the subclass.
    protected override Type StyleKeyOverride => typeof(Border);

    public ConnectionPanel(ConnectionController? controller, Func<string, string, string, Task<bool>> confirm, string? unavailable = null)
    {
        this.controller = controller;
        this.confirm = confirm;
        Classes.Add("card");
        AutomationProperties.SetName(this, "連線模式");
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "連線模式", Classes = { "title" } });
        content.Children.Add(Text(Label(14), "沒有副屏時，用這臺設備顯示主電腦的資料。主電腦需執行「Secondary 連接助手」，兩臺在同一個區域網路；資料經加密，只在配對過的設備之間傳輸。", new Thickness(0, 6, 0, 16)));
        Child = content;
        if (controller is null)
        {
            content.Children.Add(Text(status, unavailable ?? "連線模式目前無法使用。", default));
            return;
        }
        AutomationProperties.SetName(status, "連線狀態");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        content.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(searchButton); buttons.Children.Add(disconnectButton);
        content.Children.Add(buttons);
        content.Children.Add(search);
        content.Children.Add(list);
        content.Children.Add(Text(Label(14), "配對新的主電腦", new Thickness(0, 8, 0, 6)));
        content.Children.Add(Text(Label(13), "在主電腦的連接助手系統匣選單按「開始配對」，再輸入畫面上的 6 位數配對碼。搜尋不到時，可直接輸入主電腦的 IP。", new Thickness(0, 0, 0, 10)));
        AutomationProperties.SetName(hostInput, "主電腦 IP 位址");
        AutomationProperties.SetName(codeInput, "6 位數配對碼");
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*") };
        var code = Field("配對碼", codeInput);
        Grid.SetColumn(code, 2);
        fields.Children.Add(Field("主電腦 IP", hostInput)); fields.Children.Add(code);
        content.Children.Add(fields);
        content.Children.Add(pairButton);
        AutomationProperties.SetLiveSetting(pairStatus, AutomationLiveSetting.Polite);
        content.Children.Add(Text(pairStatus, "", new Thickness(0, 8, 0, 0)));
        content.Children.Add(Text(identity, "本機識別碼：" + ConnectionIdentity.Short(controller.Fingerprint) + " · " + controller.DeviceName, new Thickness(0, 16, 0, 0)));
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
        if (!ConnectionController.TryParseAddress(hostInput.Text, out var host, out var port)) { pairStatus.Text = "請輸入主電腦的 IP，例如 192.168.1.20。"; return; }
        pairing = true; pairButton.IsEnabled = false;
        pairStatus.Text = "正在配對…";
        try
        {
            var problem = await controller.PairAsync(host, port, codeInput.Text ?? "");
            pairStatus.Text = problem ?? "配對成功，正在連線。";
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
        searchButton.Content = controller.Searching ? "搜尋中…" : "搜尋主電腦";
        disconnectButton.IsEnabled = controller.State != LinkState.Idle;
        list.Children.Clear();
        foreach (var entry in controller.Helpers)
        {
            var row = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(actions, Dock.Right);
            if (entry.Paired)
            {
                var connect = new Button { Content = entry.Connected ? "已連線" : "連線", IsEnabled = !entry.Connected };
                connect.Click += (_, _) => controller.Connect(entry.Fingerprint);
                var forget = new Button { Content = "移除配對" };
                forget.Click += async (_, _) =>
                {
                    if (await confirm("移除配對", $"移除與「{entry.Name}」的配對？連接助手那邊也會一併移除，之後需要重新配對。", "移除")) controller.Forget(entry.Fingerprint);
                };
                AutomationProperties.SetName(connect, "連線到 " + entry.Name);
                AutomationProperties.SetName(forget, "移除與 " + entry.Name + " 的配對");
                actions.Children.Add(connect); actions.Children.Add(forget);
            }
            else
            {
                var pair = new Button { Content = "配對…" };
                pair.Click += (_, _) =>
                {
                    hostInput.Text = entry.Port == ConnectionProtocol.TcpPort ? entry.Address : entry.Address + ":" + entry.Port;
                    codeInput.Focus();
                    pairStatus.Text = "輸入「" + entry.Name + "」的連接助手顯示的配對碼。";
                };
                AutomationProperties.SetName(pair, "與 " + entry.Name + " 配對");
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
