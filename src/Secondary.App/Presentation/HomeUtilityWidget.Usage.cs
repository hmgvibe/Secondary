using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using Secondary.Shared;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    private sealed record UsageRowView(TextBlock Label, TextBlock Detail, TextBlock Value, Grid Track);
    private sealed record UsageSectionView(TextBlock Name, TextBlock Age, TextBlock Note, UsageRowView FiveHour, UsageRowView Weekly);

    private readonly AiUsageService usageSource;
    private CancellationTokenSource? usageRequest;
    private bool usageBusy;
    private UsageSectionView? claudeUsage, codexUsage;
    private AiUsageSnapshot? lastUsage;
    internal AiUsageSnapshot? UsageSnapshot => lastUsage;
    private CheckBox? usageAutoUpdate;
    public CheckBox? UsageAutoUpdate => usageAutoUpdate;
    private bool UsagePaused => Settings.Kind == "AiUsage" && Settings.UsagePaused;

    private void RenderUsage()
    {
        claudeUsage = UsageSection("Claude", new Thickness(0, 2, 0, 0));
        codexUsage = UsageSection("Codex", new Thickness(0, 12, 0, 0));
        // Unchecked: the widget keeps its last reading and stops reading files and the network.
        usageAutoUpdate = new CheckBox { Content = "每分鐘自動更新", IsChecked = !Settings.UsagePaused, FontSize = 11, MinHeight = 28, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        usageAutoUpdate.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(usageAutoUpdate, "AI 用量每分鐘自動更新");
        usageAutoUpdate.Click += (_, _) => SetUsagePaused(usageAutoUpdate.IsChecked != true);
        body.Children.Add(usageAutoUpdate);
        // The status line appears only when reading fails, so both services fit the default size.
        status.Visibility = Visibility.Collapsed;
        body.Children.Add(status);
        // One shared line until the first reading, so a paused widget still fits the default size.
        claudeUsage.Note.Text = Settings.UsagePaused ? "已暫停自動更新；勾選下方開關後讀取" : "正在讀取…";
        claudeUsage.Note.Visibility = Visibility.Visible;
    }

    public bool SetUsagePaused(bool paused)
    {
        if (Settings.Kind != "AiUsage") return false;
        if (Settings.UsagePaused != paused && !Commit(Settings with { UsagePaused = paused }))
        {
            usageAutoUpdate!.IsChecked = !Settings.UsagePaused;
            status.Visibility = Visibility.Visible;
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
        var age = Label("", 11); age.Margin = new Thickness(8, 0, 0, 0); age.VerticalAlignment = VerticalAlignment.Bottom;
        age.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(age, Dock.Right); header.Children.Add(age);
        var title = Label(name, 15); title.Margin = new Thickness(0); title.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
        body.Children.Add(header);
        var fiveHour = UsageRow(name, "五小時");
        var weekly = UsageRow(name, "每週");
        var note = Label("", 11); note.Visibility = Visibility.Collapsed;
        note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(note);
        return new(title, age, note, fiveHour, weekly);
    }

    private UsageRowView UsageRow(string provider, string window)
    {
        var line = new Grid { Margin = new Thickness(0, 6, 0, 3) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = window, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        var detail = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var value = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Bahnschrift"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(detail, 1); Grid.SetColumn(value, 2);
        line.Children.Add(label); line.Children.Add(detail); line.Children.Add(value);
        AutomationProperties.SetName(value, provider + " " + window + "用量");
        body.Children.Add(line);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var track = new Grid { Height = 6, ClipToBounds = true };
        track.SetResourceReference(Panel.BackgroundProperty, "InsetBrush");
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
            status.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && !request.IsCancellationRequested) { status.Text = "讀取用量紀錄失敗，下一分鐘會重試。"; status.Visibility = Visibility.Visible; }
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
        if (!Commit(Settings with { ClaudeOnline = enabled })) { status.Visibility = Visibility.Visible; return false; }
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
        ShowSection(codexUsage, snapshot.Codex, now, snapshot.Codex.HasData ? "" :
            "尚無紀錄，使用 Codex 後自動顯示");
        UpdateResponsiveLayout();
    }

    private static void ShowSection(UsageSectionView section, ProviderUsage usage, DateTimeOffset now, string note)
    {
        section.Age.Text = AiUsageText.Age(usage.ObservedAt, now);
        section.Note.Text = note;
        section.Note.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowRow(section.FiveHour, usage.FiveHour, now);
        ShowRow(section.Weekly, usage.Weekly, now);
    }

    private static void ShowRow(UsageRowView row, UsageWindow? window, DateTimeOffset now)
    {
        var text = AiUsageText.Describe(window, now);
        row.Value.Text = text.Value;
        row.Detail.Text = text.Detail;
        row.Detail.ToolTip = text.Detail;
        row.Track.ColumnDefinitions[0].Width = new GridLength(text.Fraction, GridUnitType.Star);
        row.Track.ColumnDefinitions[1].Width = new GridLength(1 - text.Fraction, GridUnitType.Star);
    }

    private void UpdateUsageLayout(double width, double height, double scale)
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
