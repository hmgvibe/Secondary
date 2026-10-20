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
using Secondary.Shared;

namespace Secondary.Linux.Home;

// Mirrors the WPF AI usage widget; data comes from the shared AiUsageService.
public sealed partial class UtilityWidget
{
    private sealed record UsageRowView(TextBlock Label, TextBlock Detail, TextBlock Value, Grid Track);
    private sealed record UsageSectionView(TextBlock Name, TextBlock Age, TextBlock Note, UsageRowView FiveHour, UsageRowView Weekly);

    private readonly AiUsageService usageSource;
    private CancellationTokenSource? usageRequest;
    private bool usageBusy;
    private UsageSectionView? claudeUsage, codexUsage;
    private AiUsageSnapshot? lastUsage;
    internal AiUsageSnapshot? UsageSnapshot => lastUsage;
    public string[] UsageTexts => claudeUsage is null || codexUsage is null ? [] :
        new[] { claudeUsage, codexUsage }.SelectMany(s => new[] { s.Name, s.Age, s.Note, s.FiveHour.Detail, s.FiveHour.Value, s.Weekly.Detail, s.Weekly.Value })
            .Where(t => t.IsVisible).Select(t => t.Text ?? "").ToArray();

    private CheckBox? usageAutoUpdate;
    public CheckBox? UsageAutoUpdate => usageAutoUpdate;
    private bool UsagePaused => Settings.Kind == "AiUsage" && Settings.UsagePaused;

    private void RenderUsage()
    {
        claudeUsage = UsageSection("Claude", new Thickness(0, 2, 0, 0));
        codexUsage = UsageSection("Codex", new Thickness(0, 12, 0, 0));
        // Unchecked: the widget keeps its last reading and stops reading files and the network.
        usageAutoUpdate = new CheckBox { Content = "每分鐘自動更新", IsChecked = !Settings.UsagePaused, FontSize = 11, MinHeight = 28, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        usageAutoUpdate.Bind(TemplatedControl.ForegroundProperty, usageAutoUpdate.GetResourceObservable("MutedBrush"));
        AutomationProperties.SetName(usageAutoUpdate, "AI 用量每分鐘自動更新");
        usageAutoUpdate.Click += (_, _) => SetUsagePaused(usageAutoUpdate.IsChecked != true);
        body.Children.Add(usageAutoUpdate);
        // The status line appears only when reading fails, so both services fit the default size.
        status.IsVisible = false;
        body.Children.Add(status);
        // One shared line until the first reading, so a paused widget still fits the default size.
        claudeUsage.Note.Text = Settings.UsagePaused ? "已暫停自動更新；勾選下方開關後讀取" : "正在讀取…";
        claudeUsage.Note.IsVisible = true;
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
        // Re-applying the active state stops the timer, or restarts it with an immediate read.
        SetActive(widgetActive);
        return true;
    }

    private UsageSectionView UsageSection(string name, Thickness margin)
    {
        var header = new DockPanel { Margin = margin };
        var age = Label("", 11); age.Margin = new Thickness(8, 0, 0, 0); age.VerticalAlignment = VerticalAlignment.Bottom; Muted(age);
        DockPanel.SetDock(age, Dock.Right); header.Children.Add(age);
        var title = Label(name, 15); title.Margin = new Thickness(0); title.FontWeight = FontWeight.SemiBold;
        header.Children.Add(title);
        body.Children.Add(header);
        var fiveHour = UsageRow(name, "五小時");
        var weekly = UsageRow(name, "每週");
        var note = Label("", 11); note.IsVisible = false; Muted(note);
        body.Children.Add(note);
        return new(title, age, note, fiveHour, weekly);
    }

    private UsageRowView UsageRow(string provider, string window)
    {
        var line = new Grid { Margin = new Thickness(0, 6, 0, 3), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var label = new TextBlock { Text = window, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        var detail = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
        Muted(detail);
        var value = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("fonts:Inter#Inter"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(detail, 1); Grid.SetColumn(value, 2);
        line.Children.Add(label); line.Children.Add(detail); line.Children.Add(value);
        AutomationProperties.SetName(value, provider + " " + window + "用量");
        body.Children.Add(line);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.Bind(BackgroundProperty, fill.GetResourceObservable("AccentBrush"));
        var track = new Grid { Height = 6, ClipToBounds = true, ColumnDefinitions = new ColumnDefinitions("0*,*") };
        track.Bind(Panel.BackgroundProperty, track.GetResourceObservable("InsetBrush"));
        track.Children.Add(fill);
        body.Children.Add(track);
        return new(label, detail, value, track);
    }

    private async Task RefreshUsageAsync()
    {
        if (disposed || !widgetActive || UsagePaused) return;
        if (lastUsage is not null) ShowUsage(lastUsage);
        if (usageBusy) return;
        usageRequest?.Cancel();
        var request = new CancellationTokenSource(); usageRequest = request; usageBusy = true;
        try
        {
            var snapshot = await usageSource.ReadAsync(Settings.ClaudeOnline, request.Token);
            if (disposed || !widgetActive || request.IsCancellationRequested) return;
            lastUsage = snapshot; ShowUsage(snapshot);
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

    // Enabling sends the Claude Code CLI token to an unofficial endpoint; callers confirm with the user first.
    public bool SetClaudeOnline(bool enabled)
    {
        if (Settings.Kind != "AiUsage" || Settings.ClaudeOnline == enabled) return Settings.ClaudeOnline == enabled;
        if (!Commit(Settings with { ClaudeOnline = enabled })) { status.IsVisible = true; return false; }
        PauseUsageRequests();
        if (widgetActive) _ = RefreshUsageAsync();
        return true;
    }

    private void PauseUsageRequests()
    {
        var request = usageRequest; usageRequest = null; usageBusy = false;
        request?.Cancel();
    }

    private void ShowUsage(AiUsageSnapshot snapshot)
    {
        if (claudeUsage is null || codexUsage is null) return;
        var now = usageSource.UtcNow;
        ShowSection(claudeUsage, snapshot.Claude, now, snapshot.ClaudeOnlineProblem is { } problem ? "線上用量無法取得：" + problem :
            snapshot.Claude.HasData ? "" : "桌面版不提供百分比，用完額度時顯示重置時間");
        ShowSection(codexUsage, snapshot.Codex, now, snapshot.Codex.HasData ? "" : "尚無紀錄，使用 Codex 後自動顯示");
        UpdateResponsiveLayout();
    }

    private static void ShowSection(UsageSectionView section, ProviderUsage usage, DateTimeOffset now, string note)
    {
        section.Age.Text = AiUsageText.Age(usage.ObservedAt, now);
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

    private void UpdateUsageLayout(double scale)
    {
        if (claudeUsage is null || codexUsage is null) return;
        usageAutoUpdate!.FontSize = Math.Clamp(11 * scale, 11, 16);
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
