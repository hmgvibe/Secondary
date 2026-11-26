using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Shared;

internal sealed record WeatherReading(WeatherLocation Location, double Temperature, double ApparentTemperature, int Humidity,
    double WindSpeed, int Code, DateTimeOffset FetchedAt, string SourceTime);

// Shared by all weather widgets. A widget may cancel its wait without cancelling
// another widget's request; the last cancelled waiter stops the network request.
// Each shared network operation also has a 12-second deadline.
internal sealed class WeatherDataService
{
    internal const int MaximumResponseBytes = 256 * 1024;
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    // Open-Meteo is reachable directly in mainland China, so the primary client bypasses the
    // system proxy. A user's always-on VPN otherwise routes this foreign host through an
    // unstable node and weather silently goes stale. If direct fails (e.g. a network that
    // requires a proxy), retry the same host through the system proxy as a fallback.
    private static HttpClient DirectClient() => new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(9) };
    private static HttpClient ProxiedClient() => new() { Timeout = TimeSpan.FromSeconds(9) };
    internal static readonly WeatherDataService Shared = new(DirectClient(), null, ProxiedClient());
    private readonly HttpClient client;
    private readonly HttpClient fallback;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new();
    private readonly Dictionary<string, WeatherLocation[]> places = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedRequest<WeatherLocation[]>> searches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeatherReading> readings = new();
    private readonly Dictionary<string, SharedRequest<WeatherReading>> requests = new();
    internal WeatherDataService(HttpClient client, Func<DateTimeOffset>? clock = null, HttpClient? fallback = null) { this.client = client; this.fallback = fallback ?? client; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }
    internal DateTimeOffset UtcNow => clock();
    internal async Task<WeatherLocation[]> SearchAsync(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        query = query.Trim();
        SharedRequest<WeatherLocation[]> request;
        lock (sync)
        {
            if (places.TryGetValue(query, out var known)) return known;
            if (!searches.TryGetValue(query, out request!)) searches[query] = request = new SharedRequest<WeatherLocation[]>(token => SearchCoreAsync(query, token));
            request.Waiters++;
        }
        var released = 0;
        void Release() { if (Interlocked.Exchange(ref released, 1) == 0) ReleaseWaiter(query, searches, request); }
        using var cancellation = token.Register(Release);
        try
        {
            var result = await request.Task.WaitAsync(token).ConfigureAwait(false);
            lock (sync) { if (places.Count >= 64) places.Remove(places.Keys.First()); places[query] = result; }
            return result;
        }
        finally { Release(); }
    }
    internal WeatherReading? Cached(WeatherLocation location)
    {
        lock (sync) return readings.TryGetValue(Key(location), out var cached) ? cached with { Location = location } : null;
    }
    internal async Task<WeatherReading> ReadAsync(WeatherLocation location, bool force, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = Key(location);
        SharedRequest<WeatherReading> request;
        lock (sync)
        {
            if (!force && readings.TryGetValue(key, out var known) && clock() - known.FetchedAt < Freshness) return known with { Location = location };
            if (!requests.TryGetValue(key, out request!)) requests[key] = request = new SharedRequest<WeatherReading>(token => ReadCoreAsync(location, token));
            request.Waiters++;
        }
        var released = 0;
        void Release() { if (Interlocked.Exchange(ref released, 1) == 0) ReleaseWaiter(key, requests, request); }
        using var cancellation = token.Register(Release);
        try
        {
            var result = await request.Task.WaitAsync(token).ConfigureAwait(false);
            lock (sync) { if (readings.Count >= 64) readings.Remove(readings.OrderBy(p => p.Value.FetchedAt).First().Key); readings[key] = result; }
            return result with { Location = location };
        }
        finally { Release(); }
    }
    private void ReleaseWaiter<T>(string key, Dictionary<string, SharedRequest<T>> pending, SharedRequest<T> request)
    {
        var cancel = false;
        lock (sync)
        {
            if (--request.Waiters == 0)
            {
                if (pending.TryGetValue(key, out var current) && ReferenceEquals(request, current)) pending.Remove(key);
                cancel = !request.Task.IsCompleted;
            }
        }
        if (cancel) request.Cancel();
    }
    private static string Key(WeatherLocation location) => location.Latitude.ToString("R", CultureInfo.InvariantCulture) + "," + location.Longitude.ToString("R", CultureInfo.InvariantCulture);
    // QWeather (和风天气) is the preferred source in mainland China: direct, no VPN, and
    // Simplified-Chinese city names. It requires a free developer key the user supplies in
    // Settings. When no key is configured we transparently fall back to Open-Meteo, which also
    // needs no key. The key is held in a static slot so every widget shares one configuration.
    private static string? qweatherKey;
    internal static void Configure(string? apiKey) => qweatherKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    internal static bool UsesQWeather => qweatherKey is not null;

    private async Task<WeatherLocation[]> SearchCoreAsync(string query, CancellationToken token)
    {
        // The geocoder expects the city as the search term. Region/country text
        // after a comma is used to rank the visible candidates, never silently saved.
        var segments = query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var name = segments.FirstOrDefault() ?? query;
        WeatherLocation[] locations;
        var key = qweatherKey;
        if (key is not null)
        {
            var qwUrl = "https://geoapi.qweather.com/v2/city/lookup?location=" + Uri.EscapeDataString(name) + "&number=10&key=" + Uri.EscapeDataString(key);
            using var qw = await ReadJsonAsync(qwUrl, token).ConfigureAwait(false);
            if (String(qw.RootElement, "code") != "200") return [];
            locations = qw.RootElement.TryGetProperty("location", out var places)
                ? places.EnumerateArray().Select(place => new WeatherLocation(
                    String(place, "name"), String(place, "country"), String(place, "adm1"),
                    Number(place, "lat"), Number(place, "lon")).Normalize())
                    .OfType<WeatherLocation>().ToArray()
                : [];
        }
        else
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=5&language=zh&format=json";
            using var json = await ReadJsonAsync(url, token).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("results", out var results)) return [];
            locations = results.EnumerateArray().Select(place => new WeatherLocation(
                String(place, "name"), String(place, "country"), String(place, "admin1"),
                place.GetProperty("latitude").GetDouble(), place.GetProperty("longitude").GetDouble()).Normalize())
                .OfType<WeatherLocation>().ToArray();
        }
        return segments.Length <= 1 ? locations : locations.OrderByDescending(p => (p.Region + " " + p.Country).Contains(segments[^1], StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private async Task<WeatherReading> ReadCoreAsync(WeatherLocation location, CancellationToken token)
    {
        var coords = Key(location).Split(',');
        var key = qweatherKey;
        if (key is not null)
        {
            // lon,lat order for QWeather; coordinates limited to two decimals by the API.
            var lat = Math.Round(double.Parse(coords[0], CultureInfo.InvariantCulture), 2);
            var lon = Math.Round(double.Parse(coords[1], CultureInfo.InvariantCulture), 2);
            var url = FormattableString.Invariant($"https://devapi.qweather.com/v7/weather/now?location={lon},{lat}&key={key}");
            using var json = await ReadJsonAsync(url, token).ConfigureAwait(false);
            if (String(json.RootElement, "code") != "200") throw new InvalidDataException("和风天气返回错误代码 " + String(json.RootElement, "code"));
            var now = json.RootElement.GetProperty("now");
            var temperature = Number(now, "temp");
            var apparent = Number(now, "feelsLike");
            var humidity = (int)Number(now, "humidity");
            var wind = Number(now, "windSpeed");
            var code = QWeatherCode((int)Number(now, "icon"));
            if (!double.IsFinite(temperature) || !double.IsFinite(apparent) || !double.IsFinite(wind) || humidity is < 0 or > 100) throw new InvalidDataException("Invalid weather values.");
            return new WeatherReading(location, temperature, apparent, humidity, wind, code, clock(), String(now, "obsTime"));
        }
        var openUrl = $"https://api.open-meteo.com/v1/forecast?latitude={coords[0]}&longitude={coords[1]}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&timezone=auto";
        using var openJson = await ReadJsonAsync(openUrl, token).ConfigureAwait(false);
        var current = openJson.RootElement.GetProperty("current");
        var t = current.GetProperty("temperature_2m").GetDouble();
        var a = current.GetProperty("apparent_temperature").GetDouble();
        var h = current.GetProperty("relative_humidity_2m").GetInt32();
        var w = current.GetProperty("wind_speed_10m").GetDouble();
        if (!double.IsFinite(t) || !double.IsFinite(a) || !double.IsFinite(w) || h is < 0 or > 100) throw new InvalidDataException("Invalid weather values.");
        return new WeatherReading(location, t, a, h, w, current.GetProperty("weather_code").GetInt32(), clock(), String(current, "time"));
    }
    // Map QWeather icon codes to the WMO weather codes used by the widget glyphs.
    private static int QWeatherCode(int icon) => icon switch
    {
        100 or 150 => 0, 101 or 102 or 103 => 2, 104 => 3,
        200 or 201 or 202 => 3, 300 or 301 or 302 or 303 => 61,
        400 or 401 or 402 or 403 or 404 or 405 => 95,
        500 or 501 or 502 or 503 or 507 or 508 => 61, 504 or 506 => 65,
        511 => 66, 600 or 601 or 602 or 603 or 604 => 71, 605 or 606 or 607 => 75,
        700 or 701 or 702 or 703 => 45, 800 or 801 => 51, 802 => 53, 803 => 55,
        900 => 95, 901 => 96, _ => 3
    };
    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token)
    {
        try { return await FetchJsonAsync(client, url, token).ConfigureAwait(false); }
        catch (Exception ex) when (ShouldFallback(ex, token))
        {
            // Direct request failed (timeout / DNS / connection). Retry the same host through
            // the alternate route so a proxy-only network still works.
            return await FetchJsonAsync(fallback, url, token).ConfigureAwait(false);
        }
    }
    private bool ShouldFallback(Exception ex, CancellationToken token)
        => !token.IsCancellationRequested && !ReferenceEquals(client, fallback)
           && ex is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException;
    private async Task<JsonDocument> FetchJsonAsync(HttpClient http, string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Weather response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("Weather response too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 16 }, token).ConfigureAwait(false);
    }
    private sealed class SharedRequest<T>
    {
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        internal readonly Task<T> Task;
        internal int Waiters;
        internal SharedRequest(Func<CancellationToken, Task<T>> read) => Task = RunAsync(read);
        private async Task<T> RunAsync(Func<CancellationToken, Task<T>> read)
        {
            try { return await read(deadline.Token).ConfigureAwait(false); }
            finally { deadline.Dispose(); }
        }
        internal void Cancel() { try { deadline.Cancel(); } catch (ObjectDisposedException) { } }
    }
    private static string String(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }
    private static double Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
}
