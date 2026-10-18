using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Presentation;
using Secondary.Core;
using Secondary.Shared;

internal static class WidgetOptimizationChecks
{
    internal static void Run(MainWindow window, ShellViewModel model, Action<bool, string> check, string? outputDirectory)
    {
        foreach (var kind in HomeWidgetSettings.Kinds)
        {
            var settings = new HomeWidgetSettings("resize-" + kind, kind, Text: "已儲存便箋", Tasks: [new TodoItem("完成檢查")],
                City: "台北", EventTitle: "旅行", EventDate: "2026-12-31", Location: new WeatherLocation("台北", "台灣", "台北市", 25.03, 121.56));
            using var widget = new HomeUtilityWidget(settings) { Resources = window.Resources };
            widget.SaveRequested += _ => true;
            Layout(widget, 320, 280);
            var smallElements = Descendants(widget).OfType<FrameworkElement>().ToArray();
            var boxes = smallElements.OfType<TextBox>().ToArray();
            foreach (var box in boxes) { box.Text = box.AcceptsReturn ? "尚未保存的草稿\n第二行" : "草稿"; box.Select(1, 1); }
            var smallCalendar = smallElements.OfType<UniformGrid>().SingleOrDefault();
            var gridHeight = smallCalendar?.ActualHeight;
            var calendarFont = smallCalendar is null ? 0 : Descendants(smallCalendar).OfType<TextBlock>().Max(t => t.FontSize);
            var editorHeight = boxes.FirstOrDefault()?.ActualHeight;
            var mainNumber = smallElements.OfType<Viewbox>().SingleOrDefault();
            var numberHeight = mainNumber?.ActualHeight;
            var primaryFonts = smallElements.OfType<TextBlock>().Select(t => t.FontSize).Max();
            Layout(widget, 640, 520);
            var largeElements = Descendants(widget).OfType<FrameworkElement>().ToArray();
            check(smallElements.All(largeElements.Contains), kind + " resize preserves existing controls and runtime state");
            check(boxes.All(b => b.Text.Contains("草稿") && b.SelectionStart == 1 && b.SelectionLength == 1), kind + " resize preserves draft text and selection");
            if (smallCalendar is not null)
            {
                check(smallCalendar.ActualHeight > gridHeight + 150, "Calendar date grid uses added height");
                check(Descendants(smallCalendar).OfType<TextBlock>().Max(t => t.FontSize) > calendarFont + 3, "Calendar date font grows with cells");
                check(largeElements.OfType<Button>().Where(b => b.Content as string is "‹" or "›").All(b => AutomationProperties.GetName(b).Length > 0), "Calendar month controls have explicit accessible names");
            }
            if (kind == "Note") check(boxes[0].ActualHeight > editorHeight + 150, "Note editor uses added height");
            if (mainNumber is not null) check(mainNumber.ActualHeight > numberHeight + 15, kind + " primary number expands with available height");
            if (kind is "Media" or "Weather" or "EventCountdown" or "TimeProgress")
                check(largeElements.OfType<TextBlock>().Select(t => t.FontSize).Max() > primaryFonts + 4, kind + " primary typography responds to enlargement");
            if (kind == "Todo")
            {
                var draft = boxes.Single();
                largeElements.OfType<Button>().Single(b => b.Content is TextBlock).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(widget.Settings.Tasks![0].Done && draft.Text == "草稿", "Todo toggling preserves an in-progress new item");
                var inner = largeElements.OfType<ScrollViewer>().Single(s => s.Content is StackPanel p && p.Children.OfType<DockPanel>().Any() && !p.Children.OfType<TextBlock>().Any());
                check(inner.ActualHeight > 300, "Todo list uses remaining enlarged height");
            }
            if (kind == "Counter")
            {
                var plus = largeElements.OfType<Button>().Single(b => b.Content as string == "＋ 1");
                plus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(widget.Settings.Count == 1 && Descendants(widget).Contains(plus), "Counter updates without rebuilding its buttons");
            }
            if (kind == "Weather")
            {
                var links = largeElements.OfType<TextBlock>().SelectMany(t => t.Inlines.OfType<Hyperlink>()).ToArray();
                check(links.Any(l => l.NavigateUri.Host == "open-meteo.com") && links.Any(l => l.NavigateUri.Host == "creativecommons.org"), "Weather displays clickable source and license attribution");
            }
            if (outputDirectory is not null)
            {
                Directory.CreateDirectory(outputDirectory);
                Save(widget, Path.Combine(outputDirectory, "optimized-widget-" + kind + ".png"));
            }
            Layout(widget, 216, 178);
            check(Descendants(widget).OfType<Button>().All(b => b.MinHeight >= 28), kind + " compact layout retains usable button targets");
            widget.SetActive(false); check(!widget.IsRefreshing, kind + " inactive widget stops its UI timer");
        }
        CheckPlacement(check);
        Task.Run(() => CheckWeatherAsync(check)).GetAwaiter().GetResult();
        CheckWeatherUi(window, check);
        var location = new WeatherLocation("台北", "台灣", "台北市", 25.03, 121.56);
        var oldData = new HomeWidgetSettings("old", "Weather", City: "台北").Normalize();
        check(oldData.City == "台北" && oldData.Location is null, "Legacy weather city is retained for explicit location selection");
        check(new HomeWidgetSettings("new", "Weather", Location: location).Normalize().Location == location, "Chosen weather coordinates survive settings normalization");
        check(new HomeWidgetSettings("invalid", "Weather", Location: location with { Latitude = double.NaN }).Normalize().Location is null, "Invalid weather coordinates are rejected safely");
    }
    private static void CheckPlacement(Action<bool, string> check)
    {
        var size = new Size(1920, 1400);
        var occupied = new List<Rect>();
        var placements = new List<WidgetPlacement>();
        for (var i = 0; i < 12; i++)
        {
            var placement = HomeWidgetLayout.FindNewPlacement(size, occupied, i, out var crowded);
            var width = Math.Max(240, 320 * placement.Scale + 24);
            var height = Math.Max(230, 300 * placement.Scale + 24);
            var rectangle = new Rect((size.Width - width) * placement.X, (size.Height - height) * placement.Y, width, height);
            check(!crowded && !occupied.Any(rectangle.IntersectsWith), "New widget " + (i + 1) + " occupies free geometry when sufficient space exists");
            occupied.Add(rectangle); placements.Add(placement);
        }
        check(placements.Distinct().Count() == 12, "Widgets 10 through 12 never repeat the first three placements");
        var canvas = new Canvas(); canvas.Measure(new Size(800, 600)); canvas.Arrange(new Rect(0, 0, 800, 600));
        using var content = new HomeUtilityWidget(new HomeWidgetSettings("minimum", "Note"));
        var frame = new HomeWidget(); frame.Attach(canvas, content, "便箋"); frame.Configure(new WidgetPlacement(), new Size(320, 300));
        frame.ResizeTo(1, 1, new Point(0, 0)); canvas.UpdateLayout();
        check(frame.Width >= 240 && frame.Height >= 230, "Utility resize maintains a readable minimum in available canvas space");
        frame.Detach();
        var fallback1 = HomeWidgetLayout.FindNewPlacement(new Size(500, 400), [new Rect(0, 0, 500, 400)], 9, out var crowded1);
        var fallback2 = HomeWidgetLayout.FindNewPlacement(new Size(500, 400), [new Rect(0, 0, 500, 400)], 10, out var crowded2);
        check(crowded1 && crowded2 && fallback1 != fallback2, "Crowded canvas reports fallback and keeps new placements distinct");
    }
    private static async Task CheckWeatherAsync(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var requests = 0;
        using var client = new HttpClient(new FakeHttp((request, token) =>
        {
            Interlocked.Increment(ref requests);
            var json = request.RequestUri!.Host.StartsWith("geocoding", StringComparison.Ordinal)
                ? "{\"results\":[{\"name\":\"Taipei\",\"country\":\"Taiwan\",\"admin1\":\"Taipei\",\"latitude\":25.03,\"longitude\":121.56},{\"name\":\"Taipei\",\"country\":\"Other\",\"latitude\":1,\"longitude\":1}]}"
                : "{\"current\":{\"temperature_2m\":25.5,\"apparent_temperature\":27,\"relative_humidity_2m\":75,\"wind_speed_10m\":8.2,\"weather_code\":2,\"time\":\"2026-10-13T20:00\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }));
        var source = new WeatherDataService(client, () => now);
        var places = await source.SearchAsync("Taipei", CancellationToken.None);
        await source.SearchAsync("Taipei", CancellationToken.None);
        check(places.Length == 2 && requests == 1, "Weather search presents multiple locations and caches geocoding by city");
        var first = await source.ReadAsync(places[0], false, CancellationToken.None);
        var second = await source.ReadAsync(places[0], false, CancellationToken.None);
        check(requests == 2 && first == second && first.SourceTime.Length > 0, "Weather result cache avoids repeat requests and distinguishes source time");
        var alias = places[0] with { Name = "台北" };
        check((await source.ReadAsync(alias, false, CancellationToken.None)).Location == alias && source.Cached(alias)!.Location == alias && requests == 2,
            "Shared coordinate cache retains each widget's chosen location name");
        now += TimeSpan.FromMinutes(16); await source.ReadAsync(places[0], false, CancellationToken.None);
        check(requests == 3, "Expired weather cache is refreshed");
        using var oversized = new HttpClient(new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', WeatherDataService.MaximumResponseBytes + 1)) })));
        var bounded = false;
        try { await new WeatherDataService(oversized).SearchAsync("Taipei", CancellationToken.None); } catch (InvalidDataException) { bounded = true; }
        check(bounded, "Weather rejects oversized responses before parsing");
        using var unknownLength = new HttpClient(new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[WeatherDataService.MaximumResponseBytes + 1]) })));
        bounded = false;
        try { await new WeatherDataService(unknownLength).SearchAsync("Taipei", CancellationToken.None); } catch (InvalidDataException) { bounded = true; }
        check(bounded, "Weather enforces the response limit even without Content-Length");
        var completed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayedClient = new HttpClient(new FakeHttp((_, _) => completed.Task));
        var shared = new WeatherDataService(delayedClient);
        using var cancel = new CancellationTokenSource();
        var cancelledWait = shared.SearchAsync("Taipei", cancel.Token);
        var otherWait = shared.SearchAsync("Taipei", CancellationToken.None);
        cancel.Cancel();
        var cancelled = false;
        try { await cancelledWait; } catch (OperationCanceledException) { cancelled = true; }
        completed.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") });
        var other = await otherWait;
        check(cancelled && other.Length == 0, "Cancelling one weather widget preserves another widget's shared query");
        CancellationToken networkToken = default;
        using var stoppingClient = new HttpClient(new FakeHttp(async (_, token) =>
        {
            networkToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var sole = new CancellationTokenSource();
        var soleWait = new WeatherDataService(stoppingClient).SearchAsync("Taipei", sole.Token);
        sole.Cancel();
        try { await soleWait; } catch (OperationCanceledException) { }
        check(networkToken.IsCancellationRequested, "Cancelling the last weather widget stops the underlying network request");
    }
    private static void CheckWeatherUi(MainWindow window, Action<bool, string> check)
    {
        var sourceTime = DateTimeOffset.UtcNow;
        var forecastRequests = 0;
        using var citiesClient = new HttpClient(new FakeHttp((request, _) =>
        {
            if (request.RequestUri!.Host.StartsWith("geocoding", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(CityJson));
            Interlocked.Increment(ref forecastRequests);
            return Task.FromResult(JsonResponse(ForecastJson));
        }));
        var source = new WeatherDataService(citiesClient, () => sourceTime);
        using var widget = new HomeUtilityWidget(new HomeWidgetSettings("weather-ui", "Weather"), source) { Resources = window.Resources };
        var saved = 0; var saveAllowed = false;
        widget.SaveRequested += _ => { saved++; return saveAllowed; };
        Layout(widget, 320, 280);
        var input = Descendants(widget).OfType<TextBox>().Single();
        var candidates = Descendants(widget).OfType<ComboBox>().Single();
        var search = Descendants(widget).OfType<Button>().Single(b => b.Content as string == "查詢");
        var select = Descendants(widget).OfType<Button>().Single(b => b.Content as string == "使用此地點");
        Dispatch(() => { widget.SetActive(true); input.Text = "Taipei"; Click(search); });
        PumpUntil(() => candidates.Items.Count == 2);
        check(saved == 0 && widget.Settings.Location is null && candidates.Visibility == Visibility.Visible,
            "City search displays candidates without silently saving the first location");
        candidates.SelectedIndex = 1;
        Dispatch(() => Click(select));
        check(saved == 1 && widget.Settings.Location is null && candidates.Visibility == Visibility.Visible && input.Text == "Taipei",
            "Failed location save preserves the selected candidate and city draft for retry");
        saveAllowed = true; Dispatch(() => Click(select));
        PumpUntil(() => TextContains(widget, "25.5°C"));
        check(saved == 2 && widget.Settings.Location is { Country: "Other", Latitude: 1, Longitude: 1 } && forecastRequests == 1,
            "Confirming a city saves exactly the selected candidate and fetches its coordinates");
        check(input.Text == "Taipei" && candidates.SelectedIndex == 1, "Confirmed weather location retains the editor draft and selection");
        widget.SetActive(false);

        using var timeoutClient = new HttpClient(new FakeHttp((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated provider timeout"))));
        using var timedOut = new HomeUtilityWidget(new HomeWidgetSettings("timeout-search", "Weather"), new WeatherDataService(timeoutClient)) { Resources = window.Resources };
        Layout(timedOut, 320, 280);
        Dispatch(() =>
        {
            Descendants(timedOut).OfType<TextBox>().Single().Text = "Taipei";
            Click(Descendants(timedOut).OfType<Button>().Single(b => b.Content as string == "查詢"));
        });
        PumpUntil(() => TextContains(timedOut, "城市查詢失敗"));
        check(!TextContains(timedOut, "正在搜尋城市"), "Provider search timeout leaves a retryable error instead of a permanent searching state");
        using var forecastTimeout = new HomeUtilityWidget(new HomeWidgetSettings("timeout-forecast", "Weather", City: "Taipei", Location: Taipei), new WeatherDataService(timeoutClient)) { Resources = window.Resources };
        Layout(forecastTimeout, 320, 280); Dispatch(() => forecastTimeout.SetActive(true));
        PumpUntil(() => TextContains(forecastTimeout, "天氣查詢失敗"));
        check(TextContains(forecastTimeout, "更新失敗"), "Provider forecast timeout displays an explicit failed-update state");
        forecastTimeout.SetActive(false);

        var firstResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeRequests = 0;
        using var reentryClient = new HttpClient(new FakeHttp((_, _) => Interlocked.Increment(ref resumeRequests) == 1 ? firstResponse.Task : Task.FromResult(JsonResponse(ForecastJson))));
        using var reentry = new HomeUtilityWidget(new HomeWidgetSettings("reentry", "Weather", City: "Taipei", Location: Taipei), new WeatherDataService(reentryClient)) { Resources = window.Resources };
        Layout(reentry, 320, 280);
        Dispatch(() => { reentry.SetActive(true); reentry.SetActive(false); reentry.SetActive(true); });
        PumpUntil(() => resumeRequests == 2 && TextContains(reentry, "25.5°C"));
        firstResponse.TrySetResult(JsonResponse(ForecastJson.Replace("25.5", "99.9", StringComparison.Ordinal)));
        PumpDispatcher();
        check(resumeRequests == 2 && !TextContains(reentry, "99.9°C"), "Immediate return restarts a cancelled forecast and ignores its late response");
        reentry.SetActive(false);

        var olderSearch = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var orderClient = new HttpClient(new FakeHttp((request, _) => request.RequestUri!.Query.Contains("name=Old", StringComparison.Ordinal)
            ? olderSearch.Task : Task.FromResult(JsonResponse(CityJson.Replace("Taipei", "NewCity", StringComparison.Ordinal)))));
        using var ordered = new HomeUtilityWidget(new HomeWidgetSettings("ordered", "Weather"), new WeatherDataService(orderClient)) { Resources = window.Resources };
        Layout(ordered, 320, 280);
        var orderInput = Descendants(ordered).OfType<TextBox>().Single();
        var orderCandidates = Descendants(ordered).OfType<ComboBox>().Single();
        var orderSearch = Descendants(ordered).OfType<Button>().Single(b => b.Content as string == "查詢");
        Dispatch(() => { orderInput.Text = "Old"; Click(orderSearch); orderInput.Text = "New"; Click(orderSearch); });
        PumpUntil(() => orderCandidates.Items.Count == 2);
        olderSearch.TrySetResult(JsonResponse(CityJson)); PumpDispatcher();
        check(orderCandidates.Items.Cast<WeatherLocation>().All(p => p.Name == "NewCity") && orderInput.Text == "New",
            "A cancelled old city search cannot overwrite newer candidates or the city draft");

        var failedRefreshes = 0;
        using var staleClient = new HttpClient(new FakeHttp((_, _) => Interlocked.Increment(ref failedRefreshes) == 1
            ? Task.FromResult(JsonResponse(ForecastJson)) : Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline"))));
        var staleSource = new WeatherDataService(staleClient, () => sourceTime);
        using var stale = new HomeUtilityWidget(new HomeWidgetSettings("stale", "Weather", City: "Taipei", Location: Taipei), staleSource) { Resources = window.Resources };
        Layout(stale, 320, 280); Dispatch(() => stale.SetActive(true)); PumpUntil(() => TextContains(stale, "25.5°C"));
        sourceTime += TimeSpan.FromMinutes(16); Dispatch(() => stale.SetActive(true));
        PumpUntil(() => TextContains(stale, "更新失敗"));
        check(TextContains(stale, "25.5°C") && TextContains(stale, "資料已過期"), "Failed refresh retains the last successful temperature with stale and failure labels");
        stale.SetActive(false);
    }
    private const string CityJson = "{\"results\":[{\"name\":\"Taipei\",\"country\":\"Taiwan\",\"admin1\":\"Taipei\",\"latitude\":25.03,\"longitude\":121.56},{\"name\":\"Taipei\",\"country\":\"Other\",\"latitude\":1,\"longitude\":1}]}";
    private const string ForecastJson = "{\"current\":{\"temperature_2m\":25.5,\"apparent_temperature\":27,\"relative_humidity_2m\":75,\"wind_speed_10m\":8.2,\"weather_code\":2,\"time\":\"2026-10-14T08:00\"}}";
    private static readonly WeatherLocation Taipei = new("Taipei", "Taiwan", "Taipei", 25.03, 121.56);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool TextContains(DependencyObject root, string text) => Descendants(root).OfType<TextBlock>().Any(t => t.Text.Contains(text, StringComparison.Ordinal));
    private static void Dispatch(Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { action(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void PumpUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Timed out waiting for deterministic weather UI transition.");
            PumpDispatcher(); Thread.Sleep(2);
        }
    }
    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Width = width; element.Height = height;
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
    }
}
