using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;

namespace Secondary.Shared;

internal sealed record WeatherReading(WeatherLocation Location, double Temperature, double ApparentTemperature, int Humidity,
    double WindSpeed, int Code, DateTimeOffset FetchedAt, string SourceTime, string Provider = "Open-Meteo");

// Shared by all weather widgets. A widget may cancel its wait without cancelling
// another widget's request; the last cancelled waiter stops the network request.
// Each shared network operation also has a deadline.
internal sealed class WeatherDataService
{
    internal const int MaximumResponseBytes = 256 * 1024;
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    // Several providers are reachable directly from mainland China. The primary client
    // bypasses the system proxy: an always-on VPN otherwise routes these hosts through
    // an unstable node and weather silently goes stale. If direct fails, a second route
    // through the system proxy is available for proxy-only networks.
    private static HttpClient DirectClient() => new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(9) };
    private static HttpClient ProxiedClient() => new() { Timeout = TimeSpan.FromSeconds(9) };
    // The live service rotates through free, key-less providers so one unreachable host
    // never leaves the widget stuck. Injected test clients keep a single provider for
    // deterministic request counts; pass rotation: true to exercise the rotation path.
    internal static readonly WeatherDataService Shared = new(DirectClient(), null, ProxiedClient(), rotation: true);
    private readonly HttpClient client;
    private readonly HttpClient fallback;
    private readonly Func<DateTimeOffset> clock;
    private readonly bool rotation;
    private readonly object sync = new();
    private readonly Dictionary<string, WeatherLocation[]> places = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedRequest<WeatherLocation[]>> searches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeatherReading> readings = new();
    private readonly Dictionary<string, SharedRequest<WeatherReading>> requests = new();
    internal WeatherDataService(HttpClient client, Func<DateTimeOffset>? clock = null, HttpClient? fallback = null, bool rotation = false)
    { this.client = client; this.fallback = fallback ?? client; this.clock = clock ?? (() => DateTimeOffset.UtcNow); this.rotation = rotation; }
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
    // QWeather (和风天气) is an optional upgrade in mainland China: direct, no VPN, and
    // Simplified-Chinese city names. It requires a free developer key the user supplies in
    // Settings. When no key is configured we rotate through key-less providers instead.
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

    // Key-less read path. The live service tries Baidu (Moji data) first because it is
    // fastest from mainland China and accepts Chinese city names, then Open-Meteo, then
    // wttr.in. Each provider has a short budget so a dead host cannot stall the widget.
    private async Task<WeatherReading> ReadCoreAsync(WeatherLocation location, CancellationToken token)
    {
        var key = qweatherKey;
        if (key is not null) return await ReadQWeatherAsync(location, key, token).ConfigureAwait(false);
        if (!rotation) return await ReadOpenMeteoAsync(location, token).ConfigureAwait(false);

        var providers = new (string Name, Func<CancellationToken, Task<WeatherReading>> Read)[]
        {
            ("墨迹天气", ct => ReadBaiduAsync(location, ct)),
            ("Open-Meteo", ct => ReadOpenMeteoAsync(location, ct)),
            ("wttr.in", ct => ReadWttrAsync(location, ct))
        };
        Exception? last = null;
        foreach (var provider in providers)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Cap each provider so the rotation as a whole stays under the widget's wait budget.
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                return await provider.Read(budget.Token).WaitAsync(TimeSpan.FromSeconds(5), budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidDataException("没有可用的天气数据源。");
    }

    private async Task<WeatherReading> ReadQWeatherAsync(WeatherLocation location, string key, CancellationToken token)
    {
        var coords = Key(location).Split(',');
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
        Validate(temperature, apparent, humidity, wind);
        return new WeatherReading(location, temperature, apparent, humidity, wind, code, clock(), String(now, "obsTime"), "和风天气");
    }

    private async Task<WeatherReading> ReadOpenMeteoAsync(WeatherLocation location, CancellationToken token)
    {
        var coords = Key(location).Split(',');
        var openUrl = $"https://api.open-meteo.com/v1/forecast?latitude={coords[0]}&longitude={coords[1]}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&timezone=auto";
        using var openJson = await ReadJsonAsync(openUrl, token).ConfigureAwait(false);
        var current = openJson.RootElement.GetProperty("current");
        var t = current.GetProperty("temperature_2m").GetDouble();
        var a = current.GetProperty("apparent_temperature").GetDouble();
        var h = current.GetProperty("relative_humidity_2m").GetInt32();
        var w = current.GetProperty("wind_speed_10m").GetDouble();
        Validate(t, a, h, w);
        return new WeatherReading(location, t, a, h, w, current.GetProperty("weather_code").GetInt32(), clock(), String(current, "time"), "Open-Meteo");
    }

    // Baidu's weather page embeds a JSON object in <script>window.tplData = {...}</script>.
    // The data originates from Moji (site: moji), giving Simplified-Chinese conditions
    // and direct mainland access without a key.
    private async Task<WeatherReading> ReadBaiduAsync(WeatherLocation location, CancellationToken token)
    {
        var city = string.IsNullOrWhiteSpace(location.Name) ? location.Region : location.Name;
        if (string.IsNullOrWhiteSpace(city)) throw new InvalidDataException("缺少城市名称。");
        var url = "https://weathernew.pae.baidu.com/weathernew/pc?query=" + Uri.EscapeDataString(city.Trim() + "天气") + "&srcid=4982";
        var html = await ReadTextAsync(url, token).ConfigureAwait(false);
        var jsonText = ExtractJsonObject(html, "window.tplData");
        if (jsonText is null) throw new InvalidDataException("百度天气返回格式无法识别。");
        using var document = JsonDocument.Parse(jsonText, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!root.TryGetProperty("weather", out var now)) throw new InvalidDataException("百度天气缺少 weather 节点。");
        var temperature = Number(now, "temperature");
        var apparent = Number(now, "real_feel_temperature");
        var humidity = (int)Number(now, "humidity");
        var level = Number(now, "wind_power_num");
        var wind = double.IsFinite(level) ? BeaufortToKmh((int)Math.Round(level)) : double.NaN;
        var code = BaiduWeatherCode(String(now, "weather"));
        Validate(temperature, apparent, humidity, wind);
        var observed = String(now, "publish_time");
        if (string.IsNullOrWhiteSpace(observed)) observed = String(now, "update_time");
        return new WeatherReading(location, temperature, apparent, humidity, wind, code, clock(), observed, "墨迹天气");
    }

    private async Task<WeatherReading> ReadWttrAsync(WeatherLocation location, CancellationToken token)
    {
        var coords = Key(location).Split(',');
        var url = $"https://wttr.in/{coords[0]},{coords[1]}?format=j1";
        using var json = await ReadJsonAsync(url, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("current_condition", out var conditions) || conditions.GetArrayLength() == 0)
            throw new InvalidDataException("wttr.in 返回格式无法识别。");
        var now = conditions[0];
        var temperature = Number(now, "temp_C");
        var apparent = Number(now, "FeelsLikeC");
        var humidity = (int)Number(now, "humidity");
        var wind = Number(now, "windspeedKmph");
        var code = WttrWeatherCode((int)Number(now, "weatherCode"));
        Validate(temperature, apparent, humidity, wind);
        return new WeatherReading(location, temperature, apparent, humidity, wind, code, clock(), String(now, "observation_time"), "wttr.in");
    }

    private static void Validate(double temperature, double apparent, int humidity, double wind)
    {
        if (!double.IsFinite(temperature) || !double.IsFinite(apparent) || !double.IsFinite(wind) || humidity is < 0 or > 100)
            throw new InvalidDataException("Invalid weather values.");
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
    private static int BaiduWeatherCode(string text) => text switch
    {
        _ when text.Contains("雷", StringComparison.Ordinal) => 95,
        _ when text.Contains("暴雨", StringComparison.Ordinal) => 65,
        _ when text.Contains("大雨", StringComparison.Ordinal) => 65,
        _ when text.Contains("中雨", StringComparison.Ordinal) => 63,
        _ when text.Contains("雪", StringComparison.Ordinal) => 71,
        _ when text.Contains("雨", StringComparison.Ordinal) => 61,
        _ when text.Contains("雾", StringComparison.Ordinal) || text.Contains("霾", StringComparison.Ordinal) => 45,
        _ when text.Contains("阴", StringComparison.Ordinal) => 3,
        _ when text.Contains("多云", StringComparison.Ordinal) => 2,
        _ when text.Contains("晴", StringComparison.Ordinal) => 0,
        _ => 3
    };
    private static int WttrWeatherCode(int code) => code switch
    {
        113 => 0, 116 => 2, 119 or 122 => 3, 248 or 260 => 45,
        176 or 263 or 266 or 293 or 296 or 299 or 302 or 305 or 308 => 61,
        185 or 311 or 314 or 317 => 66,
        200 or 386 or 389 or 392 => 95,
        179 or 182 or 227 or 230 or 323 or 326 or 329 or 332 or 335 or 338 => 71,
        _ => 3
    };
    private static double BeaufortToKmh(int level) => level switch
    {
        < 0 => 0, 0 => 1, 1 => 4, 2 => 8, 3 => 16, 4 => 25, 5 => 35,
        6 => 45, 7 => 58, 8 => 70, 9 => 83, 10 => 96, 11 => 110, _ => 125
    };
    private static string? ExtractJsonObject(string text, string marker)
    {
        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0) return null;
        var start = text.IndexOf('{', markerIndex + marker.Length);
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token)
    {
        try { return await FetchJsonAsync(client, url, token).ConfigureAwait(false); }
        catch (Exception ex) when (ShouldFallback(ex, token))
        {
            return await FetchJsonAsync(fallback, url, token).ConfigureAwait(false);
        }
    }
    private async Task<string> ReadTextAsync(string url, CancellationToken token)
    {
        try { return await FetchTextAsync(client, url, token).ConfigureAwait(false); }
        catch (Exception ex) when (ShouldFallback(ex, token))
        {
            return await FetchTextAsync(fallback, url, token).ConfigureAwait(false);
        }
    }
    private bool ShouldFallback(Exception ex, CancellationToken token)
        => !token.IsCancellationRequested && !ReferenceEquals(client, fallback)
           && ex is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException;
    private async Task<JsonDocument> FetchJsonAsync(HttpClient http, string url, CancellationToken token)
    {
        using var buffer = await FetchBytesAsync(http, url, token).ConfigureAwait(false);
        return JsonDocument.Parse(buffer, new JsonDocumentOptions { MaxDepth = 16 });
    }
    private async Task<string> FetchTextAsync(HttpClient http, string url, CancellationToken token)
    {
        using var buffer = await FetchBytesAsync(http, url, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    private async Task<MemoryStream> FetchBytesAsync(HttpClient http, string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("Weather response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) { buffer.Dispose(); throw new InvalidDataException("Weather response too large."); }
            await buffer.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
        }
        buffer.Position = 0;
        return buffer;
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
