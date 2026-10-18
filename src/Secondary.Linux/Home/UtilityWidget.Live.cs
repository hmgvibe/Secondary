using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.Linux.Home;

public sealed partial class UtilityWidget
{
    private static class Glyphs
    {
        public const string Play = "M8,5 L19,12 L8,19 Z";
        public const string Pause = "M7,5 H10.5 V19 H7 Z M13.5,5 H17 V19 H13.5 Z";
        public const string Previous = "M6,5 H8.5 V19 H6 Z M19,5 L9,12 L19,19 Z";
        public const string Next = "M15.5,5 H18 V19 H15.5 Z M5,5 L15,12 L5,19 Z";
        public const string Music = "M10,4 H20 V7 H12 V17 A3,3 0 1 1 10,14.2 Z";
        public const string Settings = "M3,6 H21 V8 H3 Z M3,11 H21 V13 H3 Z M3,16 H21 V18 H3 Z M7,4.5 A2.5,2.5 0 1 1 7,9.5 A2.5,2.5 0 1 1 7,4.5 Z M16,9.5 A2.5,2.5 0 1 1 16,14.5 A2.5,2.5 0 1 1 16,9.5 Z M10,14.5 A2.5,2.5 0 1 1 10,19.5 A2.5,2.5 0 1 1 10,14.5 Z";
    }
    private readonly WeatherDataService weatherSource;
    private readonly IMediaSource mediaSource;
    private Button? mediaSourceButton;
    private TextBlock? mediaHeading;
    // Media widgets show this computer's MPRIS player, or the main computer's in connection mode.
    private bool MainMedia => Settings.MediaSource == "Main";
    private IMediaSource ActiveMedia => MainMedia ? RemoteMediaSource.Shared : mediaSource;
    public Button? MediaSourceButton => mediaSourceButton;
    public string MediaTitleText => mediaTitle?.Text ?? "";
    public string MediaArtistText => mediaArtist?.Text ?? "";
    public string MediaStateText => mediaState?.Text ?? "";
    private CancellationTokenSource? weatherRequest, weatherSearchRequest, mediaRefreshRequest;
    private bool disposed, widgetActive, mediaBusy, mediaControlling, weatherBusy;
    private TextBlock? mediaTitle, mediaArtist, mediaState, weatherReading, weatherDetails, weatherCity, weatherIcon, weatherMeta, weatherEditorStatus, eventReading, eventDescription, eventEditorStatus, mediaTime, weatherUpdated;
    private Button? mediaToggle, mediaPrevious, mediaNext, eventSettingsButton, weatherSelect;
    private PathIcon? mediaToggleIcon, mediaArtworkIcon;
    private Grid? mediaHero, weatherHero;
    private IDisposable? mediaSubscription;
    private int mediaRefreshQueued;
    private Border? mediaArtwork, mediaProgress;
    private StackPanel? weatherEditor, weatherDisplay, eventEditor, eventDisplay;
    private string? artworkKey;
    private double mediaFraction;
    private TextBox? weatherCityInput;
    private ComboBox? weatherCandidates;
    private WeatherReading? lastWeather;
    private DateTimeOffset weatherAttempt;
    private bool weatherFailed;
    public string MediaTitle => mediaTitle?.Text ?? "";
    public string MediaState => mediaState?.Text ?? "";
    public bool MediaControlsEnabled => mediaToggle?.IsEnabled == true;
    public string WeatherText => weatherReading?.Text ?? "";
    public string WeatherUpdated => weatherUpdated?.Text ?? "";
    public string EventReading => eventReading?.Text ?? "";
    public bool WeatherEditorVisible => weatherEditor?.IsVisible == true;

    private static TextBlock Label(string text, int size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private static Button IconButton(string geometry, string name, Action action, double size = 32)
    {
        var button = new Button
        {
            Content = new PathIcon { Data = Geometry.Parse(geometry), Width = 16, Height = 16 }, Width = size, MinWidth = size, Height = size, MinHeight = size,
            Padding = new Thickness(0), Margin = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }
    private static Border AccentPanel(Control content, Thickness padding, double radius = 14)
    {
        var panel = new Border { Child = content, Padding = padding, CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), ClipToBounds = true };
        panel.Bind(BackgroundProperty, panel.GetResourceObservable("InsetBrush"));
        panel.Bind(BorderBrushProperty, panel.GetResourceObservable("LineBrush"));
        return panel;
    }

    private void RenderMedia()
    {
        var heading = new DockPanel();
        mediaSourceButton = new Button { FontSize = 11, MinHeight = 32, Padding = new Thickness(10, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mediaSourceButton.Click += (_, _) => ToggleMediaSource();
        AutomationProperties.SetName(mediaSourceButton, "切換媒體來源：本機或主電腦");
        DockPanel.SetDock(mediaSourceButton, Dock.Right); heading.Children.Add(mediaSourceButton);
        mediaHeading = Label("", 10); Muted(mediaHeading); mediaHeading.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(mediaHeading);
        body.Children.Add(heading);
        UpdateMediaHeading();
        var hero = mediaHero = new Grid { Margin = new Thickness(0, 12, 0, 10), ColumnDefinitions = new ColumnDefinitions("82,*") };
        mediaArtworkIcon = new PathIcon { Data = Geometry.Parse(Glyphs.Music), Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mediaArtworkIcon.Bind(PathIcon.ForegroundProperty, mediaArtworkIcon.GetResourceObservable("AccentBrush"));
        mediaArtwork = AccentPanel(mediaArtworkIcon, new Thickness(0), 12);
        mediaArtwork.Width = mediaArtwork.Height = 72; mediaArtwork.HorizontalAlignment = HorizontalAlignment.Left;
        hero.Children.Add(mediaArtwork);
        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mediaTitle = Label("尚未播放媒體", 19); mediaTitle.FontWeight = FontWeight.SemiBold;
        mediaArtist = Label("等待支援 MPRIS 的播放器", 12); Muted(mediaArtist);
        meta.Children.Add(mediaTitle); meta.Children.Add(mediaArtist);
        Grid.SetColumn(meta, 1); hero.Children.Add(meta);
        body.Children.Add(hero);

        var track = new Panel { Height = 5, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
        track.Bind(Panel.BackgroundProperty, track.GetResourceObservable("LineBrush"));
        mediaProgress = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Width = 0 };
        mediaProgress.Bind(BackgroundProperty, mediaProgress.GetResourceObservable("AccentBrush"));
        track.Children.Add(mediaProgress);
        track.SizeChanged += (_, _) => UpdateMediaProgress();
        body.Children.Add(track);
        mediaTime = Label("—:—  /  —:—", 10); mediaTime.TextAlignment = TextAlignment.Right; Muted(mediaTime);
        body.Children.Add(mediaTime);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
        mediaPrevious = IconButton(Glyphs.Previous, "上一首", () => _ = ControlMediaAsync(MediaCommand.Previous), 42);
        mediaToggle = IconButton(Glyphs.Play, "播放或暫停", () => _ = ControlMediaAsync(MediaCommand.Toggle), 48);
        mediaNext = IconButton(Glyphs.Next, "下一首", () => _ = ControlMediaAsync(MediaCommand.Next), 42);
        mediaToggle.Width = mediaToggle.MinWidth = 52; mediaToggleIcon = (PathIcon)mediaToggle.Content!;
        mediaToggle.Bind(TemplatedControl.BackgroundProperty, mediaToggle.GetResourceObservable("SelectedBrush"));
        controls.Children.Add(mediaPrevious); controls.Children.Add(mediaToggle); controls.Children.Add(mediaNext);
        mediaPrevious.IsEnabled = mediaToggle.IsEnabled = mediaNext.IsEnabled = false;
        body.Children.Add(controls);
        mediaState = Label("", 11); mediaState.TextAlignment = TextAlignment.Center; Muted(mediaState);
        body.Children.Add(mediaState);
    }

    private void UpdateMediaHeading()
    {
        mediaHeading!.Text = MainMedia ? "NOW PLAYING  /  主電腦" : "NOW PLAYING  /  SYSTEM AUDIO";
        mediaSourceButton!.Content = MainMedia ? "來源：主電腦" : "來源：本機";
    }
    public void ToggleMediaSource()
    {
        if (disposed || !Commit(Settings with { MediaSource = MainMedia ? "Local" : "Main" })) return;
        UpdateMediaHeading();
        SetMediaSubscription(false);
        artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon;
        if (widgetActive) { SetMediaSubscription(true); _ = RefreshMediaAsync(); }
    }

    public async Task RefreshMediaAsync()
    {
        if (mediaBusy || disposed || !widgetActive) return;
        mediaBusy = true;
        var request = mediaRefreshRequest = new CancellationTokenSource();
        try
        {
            var snapshot = await ActiveMedia.ReadAsync(request.Token).WaitAsync(TimeSpan.FromSeconds(8), request.Token);
            if (disposed || !widgetActive || request.IsCancellationRequested) return;
            if (snapshot is null)
            {
                mediaTitle!.Text = "尚未播放媒體";
                mediaArtist!.Text = MainMedia ? RemoteMediaSource.Status : "等待支援 MPRIS 的播放器";
                mediaState!.Text = "";
                mediaTime!.Text = "—:—  /  —:—";
                mediaFraction = 0; UpdateMediaProgress();
                mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
                if (artworkKey is not null) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; }
                return;
            }
            mediaTitle!.Text = string.IsNullOrWhiteSpace(snapshot.Title) ? "未提供標題" : snapshot.Title;
            mediaArtist!.Text = string.IsNullOrWhiteSpace(snapshot.Artist) ? "未提供演出者" : snapshot.Artist;
            mediaState!.Text = (MainMedia ? "主電腦 · " : "") + (snapshot.Playing ? "播放中" : "已暫停");
            mediaToggleIcon!.Data = Geometry.Parse(snapshot.Playing ? Glyphs.Pause : Glyphs.Play);
            mediaPrevious!.IsEnabled = !mediaControlling && snapshot.CanPrevious;
            mediaNext!.IsEnabled = !mediaControlling && snapshot.CanNext;
            mediaToggle!.IsEnabled = !mediaControlling && (snapshot.Playing ? snapshot.CanPause : snapshot.CanPlay);
            if (snapshot.Duration is { TotalSeconds: > 0 } duration)
            {
                var position = snapshot.Position ?? TimeSpan.Zero;
                mediaFraction = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
                mediaTime!.Text = $"{MediaClock(position)}  /  {MediaClock(duration)}";
            }
            else { mediaFraction = 0; mediaTime!.Text = "—:—  /  —:—"; }
            UpdateMediaProgress();
            var key = snapshot.Player + "|" + snapshot.Title + "|" + snapshot.Artist + "|" + snapshot.ArtworkPath;
            if (key != artworkKey) await UpdateArtworkAsync(snapshot.ArtworkPath, key, request.Token);
            UpdateResponsiveLayout();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException or Tmds.DBus.Protocol.DBusExceptionBase)
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
        if (active && !disposed) mediaSubscription ??= ActiveMedia.Subscribe(OnMediaSourceChanged);
        else
        {
            mediaSubscription?.Dispose(); mediaSubscription = null;
            var request = mediaRefreshRequest; mediaRefreshRequest = null; mediaBusy = false;
            request?.Cancel();
        }
    }
    private void OnMediaSourceChanged()
    {
        if (disposed || Interlocked.Exchange(ref mediaRefreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref mediaRefreshQueued, 0);
            if (!disposed && widgetActive) _ = RefreshMediaAsync();
        });
    }
    private static string MediaClock(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";
    private void UpdateMediaProgress()
    {
        if (mediaProgress?.Parent is Panel track) mediaProgress.Width = Math.Max(0, track.Bounds.Width * mediaFraction);
    }
    // Only local artwork files are read; remote artwork URLs are not fetched.
    private async Task UpdateArtworkAsync(string? path, string key, CancellationToken token)
    {
        if (path is null) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; return; }
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var file = File.OpenRead(path);
                if (file.Length is 0 or > 2_000_000) return null;
                return Bitmap.DecodeToWidth(file, 336);
            }, token).WaitAsync(TimeSpan.FromSeconds(5), token);
            if (token.IsCancellationRequested || disposed || !widgetActive) { bitmap?.Dispose(); return; }
            if (bitmap is null) { mediaArtwork!.Child = mediaArtworkIcon; artworkKey = key; return; }
            var cover = Math.Max(44, mediaArtwork!.Width);
            if (mediaArtwork.Child is Image { Source: Bitmap old }) old.Dispose();
            mediaArtwork.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Width = cover, Height = cover };
            artworkKey = key;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException or InvalidOperationException)
        { if (!disposed && widgetActive && !token.IsCancellationRequested) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; } }
    }
    public async Task ControlMediaAsync(MediaCommand command)
    {
        if (disposed || mediaControlling) return;
        mediaControlling = true;
        mediaPrevious!.IsEnabled = mediaToggle!.IsEnabled = mediaNext!.IsEnabled = false;
        try
        {
            mediaState!.Text = "正在控制播放器…";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ok = await ActiveMedia.ControlAsync(command, deadline.Token).WaitAsync(deadline.Token);
            if (!disposed) mediaState!.Text = ok ? (MainMedia ? "指令已送到主電腦" : "指令已送出") : (MainMedia ? "主電腦的播放器不支援此操作" : "播放器不支援此操作");
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or Tmds.DBus.Protocol.DBusExceptionBase)
        { if (!disposed) mediaState!.Text = "媒體控制失敗"; }
        finally { mediaControlling = false; if (!disposed && widgetActive) _ = RefreshMediaAsync(); }
    }

    private void RenderWeather()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var settings = IconButton(Glyphs.Settings, "設定天氣城市", () =>
        {
            weatherEditor!.IsVisible = !weatherEditor.IsVisible;
            weatherDisplay!.IsVisible = !weatherEditor.IsVisible;
            if (weatherEditor.IsVisible) weatherCityInput!.Focus();
        });
        DockPanel.SetDock(settings, Dock.Right); header.Children.Add(settings);
        weatherCity = Label(Settings.Location?.Name ?? (Settings.City.Length == 0 ? "設定城市" : Settings.City), 15);
        weatherCity.FontWeight = FontWeight.SemiBold; weatherCity.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(weatherCity); body.Children.Add(header);

        weatherEditor = new StackPanel { IsVisible = Settings.Location is null };
        weatherEditor.Children.Add(Label("城市名稱", 12));
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        weatherCityInput = new TextBox { Text = Settings.City, MaxLength = 100, MinHeight = 36 };
        AutomationProperties.SetName(weatherCityInput, "天氣城市；Enter 查詢候選地點");
        var search = ActionButton("查詢", () => _ = SearchWeatherCitiesAsync());
        weatherCityInput.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) { _ = SearchWeatherCitiesAsync(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        weatherCityInput.TextChanged += (_, _) =>
        {
            if (weatherCandidates is null) return;
            weatherCandidates.IsVisible = weatherSelect!.IsVisible = false;
            weatherSearchRequest?.Cancel();
        };
        search.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(search, Dock.Right); row.Children.Add(search); row.Children.Add(weatherCityInput);
        weatherEditor.Children.Add(row);
        weatherCandidates = new ComboBox { IsVisible = false, MinHeight = 36, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(weatherCandidates, "選擇天氣城市、行政區及國家");
        weatherEditor.Children.Add(weatherCandidates);
        weatherSelect = ActionButton("使用此地點", () => ChooseWeatherLocation());
        weatherSelect.IsVisible = false; weatherEditor.Children.Add(weatherSelect);
        weatherEditorStatus = Label("查詢會將城市名稱傳送至 Open-Meteo。選擇城市、行政區及國家後才儲存。", 11); Muted(weatherEditorStatus);
        weatherEditor.Children.Add(weatherEditorStatus); body.Children.Add(weatherEditor);

        weatherDisplay = new StackPanel { IsVisible = Settings.Location is not null };
        var hero = weatherHero = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*") };
        weatherIcon = Label("☀", 48); weatherIcon.HorizontalAlignment = HorizontalAlignment.Center;
        weatherIcon.Bind(TextBlock.ForegroundProperty, weatherIcon.GetResourceObservable("AccentBrush"));
        hero.Children.Add(weatherIcon);
        weatherReading = Label("—°", 48); weatherReading.FontFamily = new FontFamily("fonts:Inter#Inter"); weatherReading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(weatherReading, 1); hero.Children.Add(weatherReading);
        weatherDisplay.Children.Add(hero);
        weatherDetails = Label("正在等待天氣資料", 15); weatherDetails.FontWeight = FontWeight.SemiBold;
        weatherDisplay.Children.Add(weatherDetails);
        weatherMeta = Label("", 12); Muted(weatherMeta); weatherDisplay.Children.Add(weatherMeta);
        weatherUpdated = Label("", 11); Muted(weatherUpdated); weatherDisplay.Children.Add(weatherUpdated);
        var attribution = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        attribution.Children.Add(SourceLink("天氣資料：Open-Meteo", "https://open-meteo.com/"));
        attribution.Children.Add(new TextBlock { Text = " · ", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        attribution.Children.Add(SourceLink("CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/"));
        ToolTip.SetTip(attribution, "資料按原值顯示，數字四捨五入；約每 15 分鐘更新。");
        weatherDisplay.Children.Add(attribution); body.Children.Add(weatherDisplay);
    }
    private static HyperlinkButton SourceLink(string text, string url)
    {
        var link = new HyperlinkButton { Content = new TextBlock { Text = text, FontSize = 10 }, NavigateUri = new Uri(url), Padding = new Thickness(0), MinHeight = 0 };
        AutomationProperties.SetName(link, text);
        return link;
    }
    public void ChooseWeatherLocation()
    {
        if (weatherCandidates!.SelectedItem is not WeatherLocation location) return;
        if (!Commit(Settings with { City = (weatherCityInput!.Text ?? "").Trim(), Location = location })) { weatherEditorStatus!.Text = "儲存地點失敗，請重試。"; return; }
        weatherRequest?.Cancel(); lastWeather = null; weatherAttempt = default; weatherFailed = false;
        weatherCity!.Text = location.Name; weatherReading!.Text = "—°"; weatherDetails!.Text = "正在查詢…";
        weatherMeta!.Text = ""; weatherUpdated!.Text = ""; weatherEditorStatus!.Text = "";
        weatherEditor!.IsVisible = false; weatherDisplay!.IsVisible = true;
        _ = RefreshWeatherAsync(force: true);
    }
    public async Task SearchWeatherCitiesAsync(string? query = null)
    {
        if (disposed) return;
        if (query is not null) weatherCityInput!.Text = query;
        query = (weatherCityInput!.Text ?? "").Trim();
        if (query.Length < 2) { weatherEditorStatus!.Text = "請輸入至少 2 個字的城市名稱。"; return; }
        weatherSearchRequest?.Cancel();
        var request = weatherSearchRequest = new CancellationTokenSource();
        weatherEditorStatus!.Text = "正在搜尋城市…";
        try
        {
            var locations = await weatherSource.SearchAsync(query, request.Token);
            if (disposed || request.IsCancellationRequested || (weatherCityInput.Text ?? "").Trim() != query) return;
            weatherCandidates!.ItemsSource = locations;
            weatherCandidates.SelectedIndex = locations.Length > 0 ? 0 : -1;
            weatherCandidates.IsVisible = weatherSelect!.IsVisible = locations.Length > 0;
            weatherEditorStatus.Text = locations.Length > 0 ? "請核對城市、行政區及國家，再按「使用此地點」。" : "找不到城市；請嘗試城市的英文名稱。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException)
        { if (!disposed && !request.IsCancellationRequested) weatherEditorStatus.Text = "城市查詢失敗；請檢查網路後重試。"; }
        finally
        {
            if (ReferenceEquals(weatherSearchRequest, request)) weatherSearchRequest = null;
            request.Dispose();
        }
    }
    public async Task RefreshWeatherAsync(bool force = false)
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
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (!disposed && !request.IsCancellationRequested)
            {
                weatherFailed = true; UpdateWeatherState();
                if (lastWeather is null) { weatherDetails!.Text = "天氣查詢失敗；檢查網路後再試。"; weatherUpdated!.Text = "尚無成功更新 · 更新失敗，稍後重試"; }
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
        ToolTip.SetTip(weatherUpdated, $"地點：{reading.Location}\n最後成功查詢：{reading.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n服務資料時間（地點當地時間）：{reading.SourceTime}");
        UpdateResponsiveLayout();
    }
    private void PauseWeatherRequests()
    {
        // Clear the generation before cancelling so a late continuation cannot reset a newer request.
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
        eventSettingsButton = IconButton(Glyphs.Settings, "設定倒數日", () =>
        {
            eventEditor!.IsVisible = !eventEditor.IsVisible;
            eventDisplay!.IsVisible = !eventEditor.IsVisible;
        });
        eventEditor = new StackPanel { IsVisible = Settings.EventDate.Length == 0, Margin = new Thickness(0, 36, 0, 0) };
        var title = new TextBox { Text = Settings.EventTitle, MaxLength = 80, MinHeight = 36 };
        AutomationProperties.SetName(title, "倒數日名稱");
        eventEditor.Children.Add(Label("事件名稱")); eventEditor.Children.Add(title);
        var date = new TextBox { Text = Settings.EventDate, MaxLength = 10, MinHeight = 36 };
        AutomationProperties.SetName(date, "目標日期，西元年-月-日");
        eventEditor.Children.Add(Label("目標日期 · YYYY-MM-DD")); eventEditor.Children.Add(date);
        eventEditor.Children.Add(ActionButton("儲存日期", () => SaveEvent(title.Text, date.Text)));
        eventEditorStatus = Label("", 11); eventEditor.Children.Add(eventEditorStatus);
        body.Children.Add(eventEditor);
        eventDisplay = new StackPanel { IsVisible = Settings.EventDate.Length != 0 };
        eventReading = Label("—", 56); eventReading.FontFamily = new FontFamily("fonts:Inter#Inter");
        eventReading.TextAlignment = TextAlignment.Center; eventReading.Margin = new Thickness(0, 38, 0, 6);
        eventDescription = Label("", 13); eventDescription.TextAlignment = TextAlignment.Center; Muted(eventDescription);
        eventDisplay.Children.Add(eventReading); eventDisplay.Children.Add(eventDescription);
        body.Children.Add(eventDisplay);
        UpdateEventCountdown();
    }
    public bool SaveEvent(string? title, string? date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
        { eventEditorStatus!.Text = "日期格式請填 YYYY-MM-DD。"; return false; }
        if ((title ?? "").Trim().Length == 0) { eventEditorStatus!.Text = "請輸入事件名稱。"; return false; }
        if (!Commit(Settings with { EventTitle = title!, EventDate = target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }))
        { eventEditorStatus!.Text = "儲存失敗，請重試。"; return false; }
        eventEditorStatus!.Text = "";
        UpdateEventCountdown();
        eventEditor!.IsVisible = false; eventDisplay!.IsVisible = true;
        return true;
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
