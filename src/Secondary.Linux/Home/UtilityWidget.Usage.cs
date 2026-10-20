using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Shared;
using Secondary.Shared.Connection;

namespace Secondary.Linux.Home;

// Mirrors the WPF AI usage widget; data comes from the shared AiUsageService, or the main computer in connection mode.
public sealed partial class UtilityWidget
{
    private sealed record UsageRowView(TextBlock Label, TextBlock Detail, TextBlock Value, Grid Track);
    private sealed record UsageSectionView(StackPanel Panel, TextBlock Name, TextBlock Age, TextBlock Note, UsageRowView FiveHour, UsageRowView Weekly);

    private readonly AiUsageService usageSource;
    private CancellationTokenSource? usageRequest;
    private bool usageBusy;
    private UsageSectionView? claudeUsage, codexUsage;
    private AiUsageSnapshot? lastUsage;
    internal AiUsageSnapshot? UsageSnapshot => lastUsage;
    public string[] UsageTexts => claudeUsage is null || codexUsage is null ? [] :
        new[] { claudeUsage, codexUsage }.Where(s => s.Panel.IsVisible).SelectMany(s => new[] { s.Name, s.Age, s.Note, s.FiveHour.Detail, s.FiveHour.Value, s.Weekly.Detail, s.Weekly.Value })
            .Where(t => t.IsVisible).Select(t => t.Text ?? "").ToArray();

    private CheckBox? usageAutoUpdate;
    public CheckBox? UsageAutoUpdate => usageAutoUpdate;
    private Button? usageSourceButton;
    public Button? UsageSourceButton => usageSourceButton;
    private IDisposable? remoteUsageWatch;
    private RemoteUsageClient? watchedUsageClient;
    private UsageRequest watchedUsageRequest;
    private bool watchingUsageClient;
    private bool UsagePaused => Settings.Kind == "AiUsage" && Settings.UsagePaused;
    private bool MainUsage => Settings.Kind == "AiUsage" && Settings.UsageSource == "Main";

    private void RenderUsage()
    {
        claudeUsage = UsageSection("Claude", new Thickness(0, 2, 0, 0));
        codexUsage = UsageSection("Codex", new Thickness(0, 12, 0, 0));
        // Bottom row: the auto-update switch, and the source (this computer or the main computer in connection mode).
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        usageSourceButton = ActionButton("", () => SetUsageSource(MainUsage ? "Local" : "Main"));
        usageSourceButton.FontSize = 11; usageSourceButton.MinHeight = usageSourceButton.Height = 28;
        usageSourceButton.Padding = new Thickness(10, 0); usageSourceButton.Margin = new Thickness(8, 0, 0, 0);
        usageSourceButton.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(usageSourceButton, "切換 AI 用量來源：本機或主電腦");
        DockPanel.SetDock(usageSourceButton, Dock.Right); row.Children.Add(usageSourceButton);
        // Unchecked: the widget keeps its last reading and stops reading files and the network.
        usageAutoUpdate = new CheckBox { Content = "每分鐘自動更新", IsChecked = !Settings.UsagePaused, FontSize = 11, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
        usageAutoUpdate.Bind(TemplatedControl.ForegroundProperty, usageAutoUpdate.GetResourceObservable("MutedBrush"));
        AutomationProperties.SetName(usageAutoUpdate, "AI 用量每分鐘自動更新");
        usageAutoUpdate.Click += (_, _) => SetUsagePaused(usageAutoUpdate.IsChecked != true);
        DockPanel.SetDock(usageAutoUpdate, Dock.Left); row.Children.Add(usageAutoUpdate);
        body.Children.Add(row);
        // The status line appears only when reading fails, so both services fit the default size.
        status.IsVisible = false;
        body.Children.Add(status);
        ApplyUsageProviders();
    }

    // Shows only the chosen services; the first shown section carries the one waiting line,
    // so a paused or freshly switched widget still fits the default size.
    private void ApplyUsageProviders()
    {
        claudeUsage!.Panel.IsVisible = Settings.ShowsClaude();
        codexUsage!.Panel.IsVisible = Settings.ShowsCodex();
        codexUsage.Panel.Margin = new Thickness(0, Settings.ShowsClaude() ? 12 : 2, 0, 0);
        usageSourceButton!.Content = MainUsage ? "主電腦" : "本機";
        ShowUsageMessage(Settings.UsagePaused ? "已暫停自動更新；勾選下方開關後讀取" : MainUsage ? "等待主電腦回報用量" : "正在讀取…");
    }

    // Clears the readings and puts one explanation under the first shown section.
    private void ShowUsageMessage(string message)
    {
        if (claudeUsage is null || codexUsage is null) return;
        var empty = new ProviderUsage(null, null, null);
        var now = DateTimeOffset.UtcNow;
        foreach (var section in new[] { claudeUsage, codexUsage })
        {
            ShowSection(section, empty, now, "", "");
            section.FiveHour.Detail.Text = section.Weekly.Detail.Text = "";
        }
        var first = Settings.ShowsClaude() ? claudeUsage : codexUsage;
        first.Note.Text = message;
        first.Note.IsVisible = true;
        UpdateResponsiveLayout();
    }

    public bool SetUsageProviders(string providers)
    {
        if (Settings.Kind != "AiUsage" || Array.IndexOf(HomeWidgetSettings.UsageProviderChoices, providers) < 0) return false;
        if (Settings.UsageProviders == providers) return true;
        return ChangeUsage(Settings with { UsageProviders = providers });
    }

    public bool SetUsageSource(string source)
    {
        if (Settings.Kind != "AiUsage" || source is not ("Local" or "Main")) return false;
        if (Settings.UsageSource == source) return true;
        return ChangeUsage(Settings with { UsageSource = source });
    }

    // A changed choice reads again (or re-subscribes) instead of showing stale gaps.
    private bool ChangeUsage(HomeWidgetSettings next)
    {
        if (!Commit(next)) { status.IsVisible = true; return false; }
        PauseUsageRequests();
        SetUsageSubscription(false);
        lastUsage = null;
        ApplyUsageProviders();
        SetActive(widgetActive);
        return true;
    }

    public bool SetUsagePaused(bool paused)
    {
        if (Settings.Kind != "AiUsage") return false;
        if (Settings.UsagePaused != paused && !Commit(Settings with { UsagePaused = paused }))
        {
            usageAutoUpdate!.IsChecked = !Settings.UsagePaused;
            status.IsVisible = true;
            return false;
        }
        usageAutoUpdate!.IsChecked = !paused;
        if (paused) PauseUsageRequests();
        // Re-applying the active state stops the timer and subscription, or restarts them with an immediate read.
        SetActive(widgetActive);
        return true;
    }

    // Enabling sends a Claude Code CLI token to an unofficial endpoint (here, or on the main computer); callers confirm first.
    public bool SetClaudeOnline(bool enabled)
    {
        if (Settings.Kind != "AiUsage" || Settings.ClaudeOnline == enabled) return Settings.ClaudeOnline == enabled;
        if (!Commit(Settings with { ClaudeOnline = enabled })) { status.IsVisible = true; return false; }
        PauseUsageRequests();
        SetUsageSubscription(false);
        SetActive(widgetActive);
        return true;
    }

    // Main-computer usage: one watch while the widget is on screen, unpaused and set to the main computer.
    private void SetUsageSubscription(bool on)
    {
        // Showing the page again keeps an identical watch instead of stopping and restarting the helper.
        var request = new UsageRequest(Settings.ShowsClaude(), Settings.ShowsCodex(), Settings.ClaudeOnline);
        if (on && remoteUsageWatch is not null && watchedUsageClient == RemoteUsageClient.Current && watchedUsageRequest == request) { ShowRemoteUsage(); return; }
        remoteUsageWatch?.Dispose(); remoteUsageWatch = null; watchedUsageClient = null;
        if (on != watchingUsageClient)
        {
            if (on) RemoteUsageClient.CurrentChanged += OnUsageClientChanged; else RemoteUsageClient.CurrentChanged -= OnUsageClientChanged;
            watchingUsageClient = on;
        }
        if (on && RemoteUsageClient.Current is { } client)
        {
            remoteUsageWatch = client.Watch(request, OnRemoteUsageChanged);
            watchedUsageClient = client; watchedUsageRequest = request;
        }
        if (on) ShowRemoteUsage();
    }
    private void OnUsageClientChanged() => Dispatcher.UIThread.Post(() => { if (!disposed && widgetActive && MainUsage && !UsagePaused) SetUsageSubscription(true); });
    private void OnRemoteUsageChanged() => Dispatcher.UIThread.Post(() => { if (!disposed && widgetActive && MainUsage && !UsagePaused) ShowRemoteUsage(); });

    private void ShowRemoteUsage()
    {
        var client = RemoteUsageClient.Current;
        if (client is null) { lastUsage = null; ShowUsageMessage("連線模式未啟用；可切回本機"); return; }
        if (!client.Connected) { lastUsage = null; ShowUsageMessage("主電腦未連線；連上後自動顯示"); return; }
        if (client.Snapshot is not { } snapshot) { lastUsage = null; ShowUsageMessage(client.Status); return; }
        lastUsage = snapshot;
        ShowUsage(snapshot, client.MainNow, "主電腦 · ");
    }

    private async Task RefreshUsageAsync()
    {
        if (disposed || !widgetActive || UsagePaused) return;
        // The main computer reports on its own; the minute tick only moves the countdowns.
        if (MainUsage) { ShowRemoteUsage(); return; }
        if (lastUsage is not null) ShowUsage(lastUsage, usageSource.UtcNow);
        if (usageBusy) return;
        usageRequest?.Cancel();
        var request = new CancellationTokenSource(); usageRequest = request; usageBusy = true;
        try
        {
            var snapshot = await usageSource.ReadAsync(Settings.ShowsClaude(), Settings.ShowsCodex(), Settings.ClaudeOnline, request.Token);
            if (disposed || !widgetActive || request.IsCancellationRequested || MainUsage) return;
            lastUsage = snapshot; ShowUsage(snapshot, usageSource.UtcNow);
            status.IsVisible = false;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && !request.IsCancellationRequested) { status.Text = "讀取用量紀錄失敗，下一分鐘會重試。"; status.IsVisible = true; }
        }
        finally
        {
            if (usageRequest == request) { usageRequest = null; usageBusy = false; }
            request.Dispose();
        }
    }

    private void PauseUsageRequests()
    {
        var request = usageRequest; usageRequest = null; usageBusy = false;
        request?.Cancel();
    }

    private void ShowUsage(AiUsageSnapshot snapshot, DateTimeOffset now, string agePrefix = "")
    {
        if (claudeUsage is null || codexUsage is null) return;
        ShowSection(claudeUsage, snapshot.Claude, now, snapshot.ClaudeOnlineProblem is { } problem ? "線上用量無法取得：" + problem :
            snapshot.Claude.HasData ? "" : "桌面版不提供百分比，用完額度時顯示重置時間", agePrefix);
        ShowSection(codexUsage, snapshot.Codex, now, snapshot.Codex.HasData ? "" : "尚無紀錄，使用 Codex 後自動顯示", agePrefix);
        UpdateResponsiveLayout();
    }

    private static void ShowSection(UsageSectionView section, ProviderUsage usage, DateTimeOffset now, string note, string agePrefix)
    {
        var age = AiUsageText.Age(usage.ObservedAt, now);
        section.Age.Text = age.Length == 0 ? "" : agePrefix + age;
        section.Note.Text = note;
        section.Note.IsVisible = note.Length > 0;
        ShowRow(section.FiveHour, usage.FiveHour, now);
        ShowRow(section.Weekly, usage.Weekly, now);
    }

    private static void ShowRow(UsageRowView row, UsageWindow? window, DateTimeOffset now)
    {
        var text = AiUsageText.Describe(window, now);
        row.Value.Text = text.Value;
        row.Detail.Text = text.Detail;
        ToolTip.SetTip(row.Detail, text.Detail);
        row.Track.ColumnDefinitions[0].Width = new GridLength(text.Fraction, GridUnitType.Star);
        row.Track.ColumnDefinitions[1].Width = new GridLength(1 - text.Fraction, GridUnitType.Star);
    }

    private UsageSectionView UsageSection(string name, Thickness margin)
    {
        var panel = new StackPanel { Margin = margin };
        body.Children.Add(panel);
        var header = new DockPanel();
        var age = Label("", 11); age.Margin = new Thickness(8, 0, 0, 0); age.VerticalAlignment = VerticalAlignment.Bottom; Muted(age);
        DockPanel.SetDock(age, Dock.Right); header.Children.Add(age);
        var title = Label(name, 15); title.Margin = new Thickness(0); title.FontWeight = FontWeight.SemiBold;
        header.Children.Add(title);
        panel.Children.Add(header);
        var fiveHour = UsageRow(panel, name, "五小時");
        var weekly = UsageRow(panel, name, "每週");
        var note = Label("", 11); note.IsVisible = false; Muted(note);
        panel.Children.Add(note);
        return new(panel, title, age, note, fiveHour, weekly);
    }

    private UsageRowView UsageRow(StackPanel panel, string provider, string window)
    {
        var line = new Grid { Margin = new Thickness(0, 6, 0, 3), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var label = new TextBlock { Text = window, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        var detail = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
        Muted(detail);
        var value = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("fonts:Inter#Inter"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(detail, 1); Grid.SetColumn(value, 2);
        line.Children.Add(label); line.Children.Add(detail); line.Children.Add(value);
        AutomationProperties.SetName(value, provider + " " + window + "用量");
        panel.Children.Add(line);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.Bind(BackgroundProperty, fill.GetResourceObservable("AccentBrush"));
        var track = new Grid { Height = 6, ClipToBounds = true, ColumnDefinitions = new ColumnDefinitions("0*,*") };
        track.Bind(Panel.BackgroundProperty, track.GetResourceObservable("InsetBrush"));
        track.Children.Add(fill);
        panel.Children.Add(track);
        return new(label, detail, value, track);
    }

    private void UpdateUsageLayout(double scale)
    {
        if (claudeUsage is null || codexUsage is null) return;
        usageAutoUpdate!.FontSize = usageSourceButton!.FontSize = Math.Clamp(11 * scale, 11, 16);
        foreach (var section in new[] { claudeUsage, codexUsage })
        {
            section.Name.FontSize = Math.Clamp(15 * scale, 13, 24);
            section.Age.FontSize = section.Note.FontSize = Math.Clamp(11 * scale, 11, 16);
            foreach (var row in new[] { section.FiveHour, section.Weekly })
            {
                row.Label.FontSize = Math.Clamp(12 * scale, 12, 19);
                row.Detail.FontSize = Math.Clamp(11 * scale, 11, 16);
                row.Value.FontSize = Math.Clamp(15 * scale, 14, 28);
                row.Track.Height = Math.Clamp(6 * scale, 5, 12);
            }
        }
    }
}
