using System;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using Windows.Storage.Streams;
using Windows.Media.Control;
using Secondary.Core;
using Secondary.Desktop;
using Secondary.Shared.Connection;
using Secondary.Shared;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    private CancellationTokenSource? weatherRequest;
    private CancellationTokenSource? weatherSearchRequest;
    private CancellationTokenSource? mediaRefreshRequest;
    private bool disposed, widgetActive, mediaBusy, mediaControlling, weatherBusy;
    private TextBlock? mediaTitle, mediaArtist, mediaState, weatherReading, weatherDetails, weatherCity, weatherIcon, weatherMeta, weatherEditorStatus, eventReading, eventDescription, eventEditorStatus;
    private Button? mediaToggle, mediaPrevious, mediaNext;
    private Grid? mediaHero, weatherHero;
    private IDisposable? mediaSubscription, remoteMediaWatch;
    private RemoteMediaClient? watchedClient;
    private Button? mediaSourceButton;
    private TextBlock? mediaHeading;
    private bool MainMedia => Settings.MediaSource == "Main";
    public string MediaTitleText => mediaTitle?.Text ?? "";
    public string MediaStateText => mediaState?.Text ?? "";
    public Button? MediaSourceButton => mediaSourceButton;
    private int mediaRefreshQueued;
    private Border? mediaArtwork, mediaProgress;
    private TextBlock? mediaArtworkIcon, mediaTime;
    private StackPanel? weatherEditor, weatherDisplay, eventEditor, eventDisplay;
    private Button? eventSettingsButton;
    private string? artworkKey;
    private double mediaFraction;
    private TextBlock? weatherUpdated;
    private TextBox? weatherCityInput;
    private ComboBox? weatherCandidates;
    private Button? weatherSelect;
    private WeatherReading? lastWeather;
    private DateTimeOffset weatherAttempt;
    private bool weatherFailed;

    private static TextBlock Label(string text, int size = 13) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
    };

    private static Button SettingsButton(string name, Action action)
    {
        var button = ActionButton("", action);
        button.Content = new TextBlock { Text = "\uE713", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15 };
        button.Width = button.MinWidth = button.Height = button.MinHeight = 32;
        button.Margin = new Thickness(0); button.Padding = new Thickness(0);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static Border AccentPanel(UIElement content, Thickness padding, double radius = 14)
    {
        var panel = new Border { Child = content, Padding = padding, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        panel.SetResourceReference(BackgroundProperty, "InsetBrush");
        panel.SetResourceReference(BorderBrushProperty, "LineBrush");
        return panel;
    }

    private void RenderMedia()
    {
        // The source switch: this computer's player, or the main computer's in connection mode.
        var heading = new DockPanel();
        mediaSourceButton = ActionButton("", ToggleMediaSource);
        mediaSourceButton.FontSize = 11; mediaSourceButton.MinHeight = 28; mediaSourceButton.Padding = new Thickness(10, 2, 10, 2); mediaSourceButton.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetName(mediaSourceButton, "切換媒體來源：本機或主電腦");
        DockPanel.SetDock(mediaSourceButton, Dock.Right); heading.Children.Add(mediaSourceButton);
        mediaHeading = Label("", 10); mediaHeading.VerticalAlignment = VerticalAlignment.Center;
        mediaHeading.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(mediaHeading);
        body.Children.Add(heading);
        UpdateMediaHeading();

        var hero = mediaHero = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mediaArtworkIcon = new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mediaArtworkIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        mediaArtwork = AccentPanel(mediaArtworkIcon, new Thickness(0), 12);
        mediaArtwork.Width = mediaArtwork.Height = 72;
        mediaArtwork.HorizontalAlignment = HorizontalAlignment.Left;
        hero.Children.Add(mediaArtwork);
        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mediaTitle = Label("尚未播放媒體", 19); mediaTitle.FontWeight = FontWeights.SemiBold;
        mediaArtist = Label("等待 Windows 媒體工作階段", 12);
        mediaArtist.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        meta.Children.Add(mediaTitle); meta.Children.Add(mediaArtist);
        Grid.SetColumn(meta, 1); hero.Children.Add(meta);
        body.Children.Add(hero);

        var track = new Grid { Height = 5, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
        track.SetResourceReference(Panel.BackgroundProperty, "LineBrush");
        mediaProgress = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
        mediaProgress.SetResourceReference(BackgroundProperty, "AccentBrush");
        track.Children.Add(mediaProgress);
        track.SizeChanged += (_, _) => UpdateMediaProgress();
        body.Children.Add(track);
        mediaTime = Label("—:—  /  —:—", 10); mediaTime.TextAlignment = TextAlignment.Right;
        mediaTime.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(mediaTime);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
        var previous = mediaPrevious = ActionButton("\uE892", () => _ = ControlMediaAsync("previous"));
        var next = mediaNext = ActionButton("\uE893", () => _ = ControlMediaAsync("next"));
        mediaToggle = ActionButton("\uE768", () => _ = ControlMediaAsync("toggle"));
        foreach (var button in new[] { previous, mediaToggle, next })
        {
            button.FontFamily = new FontFamily("Segoe MDL2 Assets"); button.FontSize = 18;
            button.Width = button.MinWidth = button == mediaToggle ? 52 : 42;
            button.Height = button.MinHeight = button == mediaToggle ? 44 : 38;
            button.Padding = new Thickness(0);
            controls.Children.Add(button);
        }
        mediaToggle.SetResourceReference(Control.BackgroundProperty, "SelectedBrush");
        AutomationProperties.SetName(previous, "上一首");
        AutomationProperties.SetName(mediaToggle, "播放或暫停");
        AutomationProperties.SetName(next, "下一首");
        body.Children.Add(controls);
        mediaState = Label("", 11); mediaState.TextAlignment = TextAlignment.Center;
        mediaState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(mediaState);
    }

    private void UpdateMediaHeading()
    {
        mediaHeading!.Text = MainMedia ? "NOW PLAYING  /  主電腦" : "NOW PLAYING  /  SYSTEM AUDIO";
        mediaSourceButton!.Content = MainMedia ? "來源：主電腦" : "來源：本機";
    }
    private void ToggleMediaSource()
    {
        if (disposed || !Commit(Settings with { MediaSource = MainMedia ? "Local" : "Main" })) return;
        UpdateMediaHeading();
        SetMediaSubscription(false);
        artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon;
        if (widgetActive) { SetMediaSubscription(true); _ = RefreshMediaAsync(); }
    }

    // The main computer's player, as reported by the connection helper; values clear when the connection is lost.
    private async Task RefreshMainMediaAsync(CancellationToken token)
    {
        var client = RemoteMediaClient.Current;
        // Connection mode may start (or restart) after this widget became active.
        if (client != watchedClient) { remoteMediaWatch?.Dispose(); remoteMediaWatch = null; watchedClient = null; }
        if (client is not null && remoteMediaWatch is null && widgetActive) { remoteMediaWatch = client.Watch(OnMediaSourceChanged); watchedClient = client; }
        if (client is null || !client.Connected || client.Media is not { } media)
        {
            mediaTitle!.Text = "尚未播放媒體";
            mediaArtist!.Text = client is null ? "連線模式無法使用；可切回本機" : !client.Connected ? "主電腦未連線；連上後自動顯示，或切回本機" : client.Status;
            mediaState!.Text = client?.Connected == true ? "主電腦「" + client.HelperName + "」" : "";
            mediaTime!.Text = "—:—  /  —:—";
            mediaFraction = 0; UpdateMediaProgress();
            mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
            if (artworkKey is not null) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; }
            return;
        }
        mediaTitle!.Text = string.IsNullOrWhiteSpace(media.Title) ? "未提供標題" : media.Title;
        mediaArtist!.Text = string.IsNullOrWhiteSpace(media.Artist) ? "未提供演出者" : media.Artist;
        mediaState!.Text = "主電腦 · " + (media.Playing ? "播放中" : "已暫停");
        mediaToggle!.Content = media.Playing ? "\uE769" : "\uE768";
        mediaPrevious!.IsEnabled = !mediaControlling && media.CanPrevious;
        mediaNext!.IsEnabled = !mediaControlling && media.CanNext;
        mediaToggle.IsEnabled = !mediaControlling && media.CanToggle;
        if (media.Duration is > 0 and var duration)
        {
            var position = Math.Min(media.Position ?? 0, duration);
            mediaFraction = position / duration;
            mediaTime!.Text = $"{MediaClock(TimeSpan.FromSeconds(position))}  /  {MediaClock(TimeSpan.FromSeconds(duration))}";
        }
        else { mediaFraction = 0; mediaTime!.Text = "—:—  /  —:—"; }
        UpdateMediaProgress();
        var key = "main|" + media.ArtworkKey;
        if (key != artworkKey)
        {
            if (media.ArtworkKey.Length == 0) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; }
            else
            {
                var bytes = await client.ArtworkAsync(media.ArtworkKey, token).WaitAsync(TimeSpan.FromSeconds(8), token);
                if (disposed || !widgetActive || token.IsCancellationRequested) return;
                if (bytes is null) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; }
                else ShowArtwork(bytes, key);
            }
        }
        UpdateResponsiveLayout();
    }

    private async Task RefreshMediaAsync()
    {
        if (mediaBusy || disposed || !widgetActive) return;
        mediaBusy = true;
        var request = mediaRefreshRequest = new CancellationTokenSource();
        try
        {
            if (MainMedia) { await RefreshMainMediaAsync(request.Token); return; }
            var snapshot = await MediaWidgetSource.Shared.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5), request.Token);
            var session = snapshot.Session;
            if (disposed || !widgetActive || request.IsCancellationRequested) return;
            if (session is null)
            {
                mediaTitle!.Text = "尚未播放媒體";
                mediaArtist!.Text = "等待 Windows 媒體工作階段";
                mediaState!.Text = "";
                mediaTime!.Text = "—:—  /  —:—";
                mediaFraction = 0; UpdateMediaProgress();
                mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
                if (artworkKey is not null) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; }
                return;
            }
            var props = snapshot.Properties!;
            if (disposed || !widgetActive || request.IsCancellationRequested) return;
            var playback = snapshot.Playback!;
            mediaTitle!.Text = string.IsNullOrWhiteSpace(props.Title) ? "未提供標題" : props.Title;
            mediaArtist!.Text = string.IsNullOrWhiteSpace(props.Artist) ? "未提供演出者" : props.Artist;
            mediaState!.Text = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "播放中" : "已暫停";
            mediaToggle!.Content = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "\uE769" : "\uE768";
            mediaPrevious!.IsEnabled = !mediaControlling && playback.Controls.IsPreviousEnabled;
            mediaNext!.IsEnabled = !mediaControlling && playback.Controls.IsNextEnabled;
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            mediaToggle.IsEnabled = !mediaControlling && (playback.Controls.IsPlayPauseToggleEnabled || (playing ? playback.Controls.IsPauseEnabled : playback.Controls.IsPlayEnabled));
            try
            {
                var timeline = snapshot.Timeline!;
                var duration = timeline.EndTime - timeline.StartTime;
                var position = timeline.Position - timeline.StartTime;
                mediaFraction = duration.TotalSeconds > 0 ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
                mediaTime!.Text = duration.TotalSeconds > 0 ? $"{MediaClock(position)}  /  {MediaClock(duration)}" : "—:—  /  —:—";
                UpdateMediaProgress();
            }
            catch (Exception) { mediaFraction = 0; mediaTime!.Text = "—:—  /  —:—"; UpdateMediaProgress(); }
            var key = session.SourceAppUserModelId + "|" + props.Title + "|" + props.Artist + "|" + snapshot.ArtworkRevision.ToString(CultureInfo.InvariantCulture);
            if (key != artworkKey)
            {
                await UpdateArtworkAsync(props.Thumbnail, key, request.Token);
            }
            UpdateResponsiveLayout();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && widgetActive && !request.IsCancellationRequested)
            {
                mediaState!.Text = "無法讀取系統媒體工作階段";
                mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
            }
        }
        finally
        {
            if (ReferenceEquals(mediaRefreshRequest, request)) { mediaRefreshRequest = null; mediaBusy = false; }
            request.Dispose();
        }
    }

    private void SetMediaSubscription(bool active)
    {
        if (active && !disposed && MainMedia)
        {
            if (remoteMediaWatch is null && RemoteMediaClient.Current is { } client) { remoteMediaWatch = client.Watch(OnMediaSourceChanged); watchedClient = client; }
        }
        else if (active && !disposed) mediaSubscription ??= MediaWidgetSource.Shared.Subscribe(OnMediaSourceChanged);
        else
        {
            mediaSubscription?.Dispose(); mediaSubscription = null;
            remoteMediaWatch?.Dispose(); remoteMediaWatch = null; watchedClient = null;
            var request = mediaRefreshRequest; mediaRefreshRequest = null; mediaBusy = false;
            request?.Cancel();
        }
    }
    private void OnMediaSourceChanged()
    {
        if (disposed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref mediaRefreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref mediaRefreshQueued, 0);
            if (!disposed && widgetActive) _ = RefreshMediaAsync();
        }));
    }

    private static string MediaClock(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";

    private void UpdateMediaProgress()
    {
        if (mediaProgress?.Parent is Grid track) mediaProgress.Width = Math.Max(0, track.ActualWidth * mediaFraction);
    }

    private async Task UpdateArtworkAsync(IRandomAccessStreamReference? thumbnail, string key, CancellationToken token)
    {
        if (thumbnail is null) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; return; }
        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (token.IsCancellationRequested || disposed || !widgetActive) return;
            if (stream.Size is 0 or > 2_000_000) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; return; }
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var size = (uint)stream.Size;
            if (await reader.LoadAsync(size).AsTask(token).WaitAsync(TimeSpan.FromSeconds(5), token) != size) return;
            var bytes = new byte[size]; reader.ReadBytes(bytes);
            if (disposed || !widgetActive || token.IsCancellationRequested) return;
            ShowArtwork(bytes, key);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!disposed && widgetActive && !token.IsCancellationRequested) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; } }
    }

    // Decodes a cover from either source; an image that cannot be decoded falls back to the icon.
    private void ShowArtwork(byte[] bytes, string key)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 336; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            var cover = Math.Max(44, mediaArtwork!.Width);
            mediaArtwork!.Child = new Image
            {
                Source = bitmap, Stretch = Stretch.UniformToFill, Width = cover, Height = cover,
                Clip = new RectangleGeometry(new Rect(0, 0, cover, cover), 12, 12)
            };
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException) { mediaArtwork!.Child = mediaArtworkIcon; }
        artworkKey = key;
    }

    public async Task ControlMediaAsync(string command)
    {
        if (disposed || mediaControlling) return;
        mediaControlling = true;
        mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
        try
        {
            if (MainMedia)
            {
                if (RemoteMediaClient.Current is not { Connected: true } client) { mediaState!.Text = "主電腦未連線"; return; }
                mediaState!.Text = "正在控制主電腦的播放器…";
                var sent = await client.ControlAsync(command);
                if (!disposed) mediaState!.Text = sent ? "指令已送到主電腦" : "主電腦的播放器不支援此操作";
                return;
            }
            var session = (await MediaWidgetSource.Shared.ManagerAsync().WaitAsync(TimeSpan.FromSeconds(5))).GetCurrentSession();
            if (session is null) { mediaState!.Text = "目前沒有可控制的媒體"; return; }
            mediaState!.Text = "正在控制播放器…";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            async Task<bool> SendAsync() => command switch
            {
                "previous" => await session.TrySkipPreviousAsync().AsTask(deadline.Token),
                "next" => await session.TrySkipNextAsync().AsTask(deadline.Token),
                _ when session.GetPlaybackInfo().Controls.IsPlayPauseToggleEnabled => await session.TryTogglePlayPauseAsync().AsTask(deadline.Token),
                _ when session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => await session.TryPauseAsync().AsTask(deadline.Token),
                _ => await session.TryPlayAsync().AsTask(deadline.Token)
            };
            var ok = await SendAsync().WaitAsync(deadline.Token);
            if (!disposed) mediaState!.Text = ok ? "指令已送出" : "播放器不支援此操作";
            await RefreshMediaAsync();
        }
        catch (Exception) { if (!disposed) mediaState!.Text = "媒體控制失敗"; }
        finally { mediaControlling = false; if (!disposed && widgetActive) _ = RefreshMediaAsync(); }
    }

    private void RenderWeather()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var settings = SettingsButton("設定天氣城市", () =>
        {
            weatherEditor!.Visibility = weatherEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            weatherDisplay!.Visibility = weatherEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (weatherEditor.Visibility == Visibility.Visible) weatherCityInput!.Focus();
        });
        DockPanel.SetDock(settings, Dock.Right); header.Children.Add(settings);
        weatherCity = Label(Settings.Location?.Name ?? (Settings.City.Length == 0 ? "設定城市" : Settings.City), 15);
        weatherCity.FontWeight = FontWeights.SemiBold; weatherCity.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(weatherCity); body.Children.Add(header);

        weatherEditor = new StackPanel { Visibility = Settings.Location is null ? Visibility.Visible : Visibility.Collapsed };
        weatherEditor.Children.Add(Label("城市名稱", 12));
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        weatherCityInput = new TextBox { Text = Settings.City, MaxLength = 100, MinHeight = 36 };
        weatherCityInput.SetResourceReference(StyleProperty, "WidgetInput");
        AutomationProperties.SetName(weatherCityInput, "天氣城市；Enter 查詢候選地點");
        var search = ActionButton("查詢", () => _ = SearchWeatherCitiesAsync());
        weatherCityInput.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = SearchWeatherCitiesAsync(); e.Handled = true; } };
        weatherCityInput.TextChanged += (_, _) =>
        {
            weatherCandidates!.Visibility = weatherSelect!.Visibility = Visibility.Collapsed;
            weatherSearchRequest?.Cancel();
        };
        search.Margin = new Thickness(6,0,0,0);
        DockPanel.SetDock(search, Dock.Right); row.Children.Add(search); row.Children.Add(weatherCityInput);
        weatherEditor.Children.Add(row);
        weatherCandidates = new ComboBox { Visibility = Visibility.Collapsed, MinHeight = 36, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(weatherCandidates, "選擇天氣城市、行政區及國家");
        weatherEditor.Children.Add(weatherCandidates);
        weatherSelect = ActionButton("使用此地點", () =>
        {
            if (weatherCandidates.SelectedItem is not WeatherLocation location) return;
            if (!Commit(Settings with { City = weatherCityInput.Text.Trim(), Location = location })) { weatherEditorStatus!.Text = "儲存地點失敗，請重試。"; return; }
            weatherRequest?.Cancel(); lastWeather = null; weatherAttempt = default; weatherFailed = false;
            weatherCity!.Text = location.Name; weatherReading!.Text = "—°"; weatherDetails!.Text = "正在查詢…";
            weatherMeta!.Text = ""; weatherUpdated!.Text = ""; weatherEditorStatus!.Text = "";
            weatherEditor.Visibility = Visibility.Collapsed; weatherDisplay!.Visibility = Visibility.Visible;
            _ = RefreshWeatherAsync(force: true);
        });
        weatherSelect.Visibility = Visibility.Collapsed; weatherEditor.Children.Add(weatherSelect);
        weatherEditorStatus = Label("查詢會將城市名稱傳送至 Open-Meteo。選擇城市、行政區及國家後才儲存。", 11);
        weatherEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        weatherEditor.Children.Add(weatherEditorStatus); body.Children.Add(weatherEditor);

        weatherDisplay = new StackPanel { Visibility = Settings.Location is null ? Visibility.Collapsed : Visibility.Visible };
        var hero = weatherHero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        weatherIcon = Label("☀", 48); weatherIcon.FontFamily = new FontFamily("Segoe UI Symbol");
        weatherIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        weatherIcon.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Children.Add(weatherIcon);
        weatherReading = Label("—°", 48); weatherReading.FontFamily = new FontFamily("Bahnschrift");
        weatherReading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(weatherReading, 1); hero.Children.Add(weatherReading);
        weatherDisplay.Children.Add(hero);
        weatherDetails = Label("正在等待天氣資料", 15); weatherDetails.FontWeight = FontWeights.SemiBold;
        weatherDisplay.Children.Add(weatherDetails);
        weatherMeta = Label("", 12); weatherMeta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        weatherDisplay.Children.Add(weatherMeta);
        weatherUpdated = Label("", 11); weatherUpdated.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        weatherDisplay.Children.Add(weatherUpdated);
        var attribution = Label("", 10);
        attribution.Margin = new Thickness(0, 8, 0, 0);
        attribution.Inlines.Add(SourceLink("天氣資料：Open-Meteo", "https://open-meteo.com/"));
        attribution.Inlines.Add(new Run(" · "));
        attribution.Inlines.Add(SourceLink("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/"));
        attribution.ToolTip = "資料按原值顯示，數字四捨五入；約每 15 分鐘更新。";
        weatherDisplay.Children.Add(attribution); body.Children.Add(weatherDisplay);
    }
    private static Hyperlink SourceLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { }
            e.Handled = true;
        };
        return link;
    }
    private async Task SearchWeatherCitiesAsync()
    {
        if (disposed) return;
        var query = weatherCityInput!.Text.Trim();
        if (query.Length < 2) { weatherEditorStatus!.Text = "請輸入至少 2 個字的城市名稱。"; return; }
        weatherSearchRequest?.Cancel();
        var request = weatherSearchRequest = new CancellationTokenSource();
        weatherEditorStatus!.Text = "正在搜尋城市…";
        try
        {
            var locations = await weatherSource.SearchAsync(query, request.Token);
            if (disposed || request.IsCancellationRequested || weatherCityInput.Text.Trim() != query) return;
            weatherCandidates!.ItemsSource = locations;
            weatherCandidates.SelectedIndex = locations.Length > 0 ? 0 : -1;
            weatherCandidates.Visibility = weatherSelect!.Visibility = locations.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            weatherEditorStatus.Text = locations.Length > 0 ? "請核對城市、行政區及國家，再按「使用此地點」。" : "找不到城市；請嘗試城市的英文名稱。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { if (!disposed && !request.IsCancellationRequested) weatherEditorStatus.Text = "城市查詢失敗；請檢查網路後重試。"; }
        finally
        {
            if (ReferenceEquals(weatherSearchRequest, request)) weatherSearchRequest = null;
            request.Dispose();
        }
    }
    private async Task RefreshWeatherAsync(bool force = false)
    {
        if (disposed || !widgetActive || Settings.Location is not { } location) return;
        var now = weatherSource.UtcNow;
        lastWeather ??= weatherSource.Cached(location);
        UpdateWeatherState();
        if (!force && (weatherBusy || lastWeather is not null && now - lastWeather.FetchedAt < WeatherDataService.Freshness || now - weatherAttempt < TimeSpan.FromMinutes(2))) return;
        weatherRequest?.Cancel();
        var request = new CancellationTokenSource(); weatherRequest = request; weatherBusy = true; weatherAttempt = now;
        if (lastWeather is null) weatherDetails!.Text = "正在查詢…";
        else weatherUpdated!.Text += " · 正在更新";
        try
        {
            var reading = await weatherSource.ReadAsync(location, force, request.Token);
            if (disposed || !widgetActive || request.IsCancellationRequested || Settings.Location != location) return;
            lastWeather = reading; weatherFailed = false; UpdateWeatherState();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!disposed && !request.IsCancellationRequested)
            {
                weatherFailed = true; UpdateWeatherState();
                if (lastWeather is null) weatherDetails!.Text = "天氣查詢失敗；檢查網路後再試。";
                if (lastWeather is null) weatherUpdated!.Text = "尚無成功更新 · 更新失敗，稍後重試";
            }
        }
        finally
        {
            if (ReferenceEquals(weatherRequest, request)) { weatherBusy = false; weatherRequest = null; }
            request.Dispose();
        }
    }
    private void UpdateWeatherState()
    {
        if (lastWeather is not { } reading) return;
        weatherCity!.Text = reading.Location.Name;
        weatherReading!.Text = $"{reading.Temperature:0.#}°C";
        weatherIcon!.Text = WeatherSymbol(reading.Code); weatherDetails!.Text = WeatherDescription(reading.Code);
        weatherMeta!.Text = $"體感 {reading.ApparentTemperature:0.#}° · 濕度 {reading.Humidity}%\n風速 {reading.WindSpeed:0.#} km/h";
        var stale = weatherSource.UtcNow - reading.FetchedAt >= WeatherDataService.Freshness;
        weatherUpdated!.Text = $"更新 {reading.FetchedAt.ToLocalTime():HH:mm}" + (stale ? " · 資料已過期" : " · 最新快取");
        if (weatherFailed) weatherUpdated.Text += " · 更新失敗，稍後重試";
        weatherUpdated.ToolTip = $"地點：{reading.Location}\n最後成功查詢：{reading.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n服務資料時間（地點當地時間）：{reading.SourceTime}";
        UpdateResponsiveLayout();
    }
    private void PauseWeatherRequests()
    {
        // Clear the generation before cancelling. A quick return can start its
        // next request immediately; an older continuation cannot reset that state.
        var forecast = weatherRequest; weatherRequest = null;
        var search = weatherSearchRequest; weatherSearchRequest = null;
        weatherBusy = false; weatherAttempt = default;
        forecast?.Cancel(); search?.Cancel();
        if (!disposed && search is not null) weatherEditorStatus!.Text = "查詢已暫停；返回後可重新查詢。";
    }
    private static string WeatherDescription(int code) => code switch
    {
        0 => "晴朗", 1 or 2 => "局部多雲", 3 => "陰天", 45 or 48 => "有霧",
        51 or 53 or 55 or 56 or 57 => "毛毛雨", 61 or 63 or 65 or 66 or 67 => "下雨",
        71 or 73 or 75 or 77 or 85 or 86 => "下雪", 80 or 81 or 82 => "陣雨",
        95 or 96 or 99 => "雷雨", _ => "天氣變化"
    };

    private static string WeatherSymbol(int code) => code switch
    {
        0 or 1 => "☀", 2 or 3 => "☁", 45 or 48 => "≋",
        71 or 73 or 75 or 77 or 85 or 86 => "❄", 95 or 96 or 99 => "ϟ", _ => "☂"
    };

    private void RenderEventCountdown()
    {
        eventSettingsButton = SettingsButton("設定倒數日", () =>
        {
            eventEditor!.Visibility = eventEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            eventDisplay!.Visibility = eventEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        });

        eventEditor = new StackPanel { Visibility = Settings.EventDate.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 36, 0, 0) };
        var title = new TextBox { Text = Settings.EventTitle, MaxLength = 80, MinHeight = 36 };
        title.SetResourceReference(StyleProperty, "WidgetInput");
        System.Windows.Automation.AutomationProperties.SetName(title, "倒數日名稱");
        eventEditor.Children.Add(Label("事件名稱")); eventEditor.Children.Add(title);
        var date = new TextBox { Text = Settings.EventDate, MaxLength = 10, MinHeight = 36 };
        date.SetResourceReference(StyleProperty, "WidgetInput");
        System.Windows.Automation.AutomationProperties.SetName(date, "目標日期，西元年-月-日");
        eventEditor.Children.Add(Label("目標日期 · YYYY-MM-DD")); eventEditor.Children.Add(date);
        eventEditor.Children.Add(ActionButton("儲存日期", () =>
        {
            if (!DateOnly.TryParseExact(date.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
            { eventEditorStatus!.Text = "日期格式請填 YYYY-MM-DD。"; return; }
            if (title.Text.Trim().Length == 0) { eventEditorStatus!.Text = "請輸入事件名稱。"; return; }
            if (!Commit(Settings with { EventTitle = title.Text, EventDate = target.ToString("yyyy-MM-dd") }))
            { eventEditorStatus!.Text = "儲存失敗，請重試。"; return; }
            eventEditorStatus!.Text = "";
            UpdateEventCountdown();
            eventEditor.Visibility = Visibility.Collapsed;
            eventDisplay!.Visibility = Visibility.Visible;
        }));
        eventEditorStatus = Label("", 11); eventEditor.Children.Add(eventEditorStatus);
        body.Children.Add(eventEditor);

        eventDisplay = new StackPanel { Visibility = Settings.EventDate.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
        eventReading = Label("—", 56); eventReading.FontFamily = new FontFamily("Bahnschrift");
        eventReading.TextAlignment = TextAlignment.Center;
        eventReading.Margin = new Thickness(0, 38, 0, 6);
        eventDescription = Label("", 13); eventDescription.TextAlignment = TextAlignment.Center;
        eventDescription.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        eventDisplay.Children.Add(eventReading); eventDisplay.Children.Add(eventDescription);
        body.Children.Add(eventDisplay);
        UpdateEventCountdown();
    }

    private void UpdateEventCountdown()
    {
        if (eventReading is null || eventDescription is null) return;
        if (!DateOnly.TryParseExact(Settings.EventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
        { eventReading.Text = "—"; eventDescription.Text = "設定日期後開始倒數"; return; }
        var days = target.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        eventReading.Text = days > 0 ? $"{days} 天" : days == 0 ? "就是今天" : $"已過 {Math.Abs((long)days)} 天";
        eventDescription.Text = target.ToString("yyyy 年 MM 月 dd 日", CultureInfo.InvariantCulture);
        UpdateResponsiveLayout();
    }
}
