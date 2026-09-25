using System;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation;
using Windows.Storage.Streams;
using Windows.Media.Control;
using Secondary.Core;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    private static readonly HttpClient weatherClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? weatherRequest;
    private bool disposed, widgetActive, mediaBusy, weatherBusy;
    private TextBlock? mediaTitle, mediaArtist, mediaState, weatherReading, weatherDetails, weatherCity, weatherIcon, weatherMeta, weatherEditorStatus, eventReading, eventDescription, eventEditorStatus;
    private Button? mediaToggle;
    private Border? mediaArtwork, mediaProgress;
    private TextBlock? mediaArtworkIcon, mediaTime;
    private StackPanel? weatherEditor, weatherDisplay, eventEditor, eventDisplay;
    private Button? eventSettingsButton;
    private string? artworkKey;
    private double mediaFraction;

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
        var nowPlaying = Label("NOW PLAYING  /  SYSTEM AUDIO", 10);
        nowPlaying.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(nowPlaying);

        var hero = new Grid { Margin = new Thickness(0, 12, 0, 10) };
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
        var previous = ActionButton("\uE892", () => _ = ControlMediaAsync("previous"));
        var next = ActionButton("\uE893", () => _ = ControlMediaAsync("next"));
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

    private async Task RefreshMediaAsync()
    {
        if (mediaBusy || disposed || !widgetActive) return;
        mediaBusy = true;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (disposed || !widgetActive) return;
            if (session is null)
            {
                mediaTitle!.Text = "尚未播放媒體";
                mediaArtist!.Text = "等待 Windows 媒體工作階段";
                mediaState!.Text = "";
                mediaTime!.Text = "—:—  /  —:—";
                mediaFraction = 0; UpdateMediaProgress();
                if (artworkKey is not null) { artworkKey = null; mediaArtwork!.Child = mediaArtworkIcon; }
                return;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            if (disposed || !widgetActive) return;
            var playback = session.GetPlaybackInfo();
            mediaTitle!.Text = string.IsNullOrWhiteSpace(props.Title) ? "未提供標題" : props.Title;
            mediaArtist!.Text = string.IsNullOrWhiteSpace(props.Artist) ? "未提供演出者" : props.Artist;
            mediaState!.Text = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "播放中" : "已暫停";
            mediaToggle!.Content = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? "\uE769" : "\uE768";
            try
            {
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;
                var position = timeline.Position - timeline.StartTime;
                mediaFraction = duration.TotalSeconds > 0 ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1) : 0;
                mediaTime!.Text = duration.TotalSeconds > 0 ? $"{MediaClock(position)}  /  {MediaClock(duration)}" : "—:—  /  —:—";
                UpdateMediaProgress();
            }
            catch (Exception) { mediaFraction = 0; mediaTime!.Text = "—:—  /  —:—"; UpdateMediaProgress(); }
            var key = session.SourceAppUserModelId + "|" + props.Title + "|" + props.Artist;
            if (key != artworkKey)
            {
                artworkKey = key;
                await UpdateArtworkAsync(props.Thumbnail, key);
            }
        }
        catch (Exception)
        {
            if (!disposed && widgetActive) mediaState!.Text = "無法讀取系統媒體工作階段";
        }
        finally { mediaBusy = false; }
    }

    private static string MediaClock(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";

    private void UpdateMediaProgress()
    {
        if (mediaProgress?.Parent is Grid track) mediaProgress.Width = Math.Max(0, track.ActualWidth * mediaFraction);
    }

    private async Task UpdateArtworkAsync(IRandomAccessStreamReference? thumbnail, string key)
    {
        if (thumbnail is null) { mediaArtwork!.Child = mediaArtworkIcon; return; }
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size is 0 or > 2_000_000) { mediaArtwork!.Child = mediaArtworkIcon; return; }
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var size = (uint)stream.Size;
            if (await reader.LoadAsync(size) != size) return;
            var bytes = new byte[size]; reader.ReadBytes(bytes);
            if (disposed || !widgetActive || artworkKey != key) return;
            using var input = new MemoryStream(bytes);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 144; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
            mediaArtwork!.Child = new Image
            {
                Source = bitmap, Stretch = Stretch.UniformToFill, Width = 72, Height = 72,
                Clip = new RectangleGeometry(new Rect(0, 0, 72, 72), 12, 12)
            };
        }
        catch (Exception) { if (!disposed && artworkKey == key) mediaArtwork!.Child = mediaArtworkIcon; }
    }

    private async Task ControlMediaAsync(string command)
    {
        try
        {
            var session = (await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()).GetCurrentSession();
            if (session is null) { mediaState!.Text = "目前沒有可控制的媒體"; return; }
            var ok = command switch
            {
                "previous" => await session.TrySkipPreviousAsync(),
                "next" => await session.TrySkipNextAsync(),
                _ => await session.TryTogglePlayPauseAsync()
            };
            if (!disposed) mediaState!.Text = ok ? "指令已送出" : "播放器不支援此操作";
            await RefreshMediaAsync();
        }
        catch (Exception) { if (!disposed) mediaState!.Text = "媒體控制失敗"; }
    }

    private void RenderWeather()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var settings = SettingsButton("設定天氣城市", () =>
        {
            weatherEditor!.Visibility = weatherEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            weatherDisplay!.Visibility = weatherEditor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        });
        DockPanel.SetDock(settings, Dock.Right); header.Children.Add(settings);
        weatherCity = Label(Settings.City.Length == 0 ? "設定城市" : Settings.City, 15);
        weatherCity.FontWeight = FontWeights.SemiBold; weatherCity.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(weatherCity); body.Children.Add(header);

        weatherEditor = new StackPanel { Visibility = Settings.City.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        weatherEditor.Children.Add(Label("城市名稱", 12));
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var city = new TextBox { Text = Settings.City, MaxLength = 100, MinHeight = 36 };
        city.SetResourceReference(StyleProperty, "WidgetInput");
        System.Windows.Automation.AutomationProperties.SetName(city, "天氣城市");
        var search = ActionButton("查詢", () =>
        {
            var name = city.Text.Trim();
            if (name.Length < 2) { weatherEditorStatus!.Text = "請輸入至少 2 個字的城市名稱。"; return; }
            if (!Commit(Settings with { City = name })) { weatherEditorStatus!.Text = "儲存城市失敗，請重試。"; return; }
            weatherCity!.Text = name; weatherReading!.Text = "—°";
            weatherDetails!.Text = "正在查詢…"; weatherMeta!.Text = "";
            weatherEditorStatus!.Text = "";
            weatherEditor.Visibility = Visibility.Collapsed; weatherDisplay!.Visibility = Visibility.Visible;
            _ = RefreshWeatherAsync(force: true);
        });
        search.Margin = new Thickness(6,0,0,0);
        DockPanel.SetDock(search, Dock.Right); row.Children.Add(search); row.Children.Add(city);
        weatherEditor.Children.Add(row);
        weatherEditorStatus = Label("可加上國家或地區，例如 Taipei, Taiwan", 11);
        weatherEditorStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        weatherEditor.Children.Add(weatherEditorStatus); body.Children.Add(weatherEditor);

        weatherDisplay = new StackPanel { Visibility = Settings.City.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
        var hero = new Grid();
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
        weatherDetails = Label(Settings.City.Length == 0 ? "" : "正在等待天氣資料", 15);
        weatherDetails.FontWeight = FontWeights.SemiBold;
        weatherDisplay.Children.Add(weatherDetails);
        weatherMeta = Label("", 12);
        weatherMeta.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        weatherDisplay.Children.Add(weatherMeta);
        var attribution = Label("OPEN-METEO  ·  約每 15 分鐘更新", 10);
        attribution.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        attribution.Margin = new Thickness(0, 12, 0, 0);
        weatherDisplay.Children.Add(attribution);
        body.Children.Add(weatherDisplay);
    }

    private async Task RefreshWeatherAsync(bool force = false)
    {
        if (disposed || !widgetActive || Settings.City.Length == 0 || weatherBusy && !force) return;
        weatherBusy = true;
        weatherRequest?.Cancel();
        var request = new CancellationTokenSource(); weatherRequest = request;
        try
        {
            var token = request.Token;
            var city = Settings.City;
            weatherDetails!.Text = "正在查詢…";
            var geoUrl = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(city) + "&count=1&language=zh&format=json";
            using var geo = await weatherClient.GetAsync(geoUrl, token);
            geo.EnsureSuccessStatusCode();
            using var geoJson = JsonDocument.Parse(await geo.Content.ReadAsStreamAsync(token));
            if (!geoJson.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            { weatherDetails.Text = "找不到城市，請加上國家或地區重試。"; return; }
            var place = results[0];
            var latitude = place.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
            var longitude = place.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
            var forecastUrl = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&timezone=auto";
            using var forecast = await weatherClient.GetAsync(forecastUrl, token);
            forecast.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await forecast.Content.ReadAsStreamAsync(token));
            var current = json.RootElement.GetProperty("current");
            if (disposed || !widgetActive || token.IsCancellationRequested || Settings.City != city) return;
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var feels = current.GetProperty("apparent_temperature").GetDouble();
            var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();
            weatherReading!.Text = $"{temp:0.#}°C";
            weatherCity!.Text = place.GetProperty("name").GetString() ?? city;
            weatherIcon!.Text = WeatherSymbol(code);
            weatherDetails!.Text = WeatherDescription(code);
            weatherMeta!.Text = $"體感 {feels:0.#}°   ·   濕度 {humidity}%\n風速 {wind:0.#} km/h";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!disposed && !request.IsCancellationRequested) weatherDetails!.Text = "天氣查詢失敗；檢查網路後再試。"; }
        finally
        {
            if (ReferenceEquals(weatherRequest, request)) { weatherBusy = false; weatherRequest = null; }
            request.Dispose();
        }
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
    }
}
