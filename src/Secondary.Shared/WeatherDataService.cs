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
    internal static readonly WeatherDataService Shared = new(new HttpClient { Timeout = TimeSpan.FromSeconds(12) });
    private readonly HttpClient client;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new();
    private readonly Dictionary<string, WeatherLocation[]> places = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SharedRequest<WeatherLocation[]>> searches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeatherReading> readings = new();
    private readonly Dictionary<string, SharedRequest<WeatherReading>> requests = new();
    internal WeatherDataService(HttpClient client, Func<DateTimeOffset>? clock = null) { this.client = client; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }
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
    private async Task<WeatherLocation[]> SearchCoreAsync(string query, CancellationToken token)
    {
        // The geocoder expects the city as the search term. Region/country text
        // after a comma is used to rank the visible candidates, never silently saved.
        var segments = query.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var name = segments.FirstOrDefault() ?? query;
        var url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=5&language=zh&format=json";
        using var json = await ReadJsonAsync(url, token).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("results", out var results)) return [];
        var locations = results.EnumerateArray().Select(place => new WeatherLocation(
            String(place, "name"), String(place, "country"), String(place, "admin1"),
            place.GetProperty("latitude").GetDouble(), place.GetProperty("longitude").GetDouble()).Normalize())
            .OfType<WeatherLocation>().ToArray();
        return segments.Length <= 1 ? locations : locations.OrderByDescending(p => (p.Region + " " + p.Country).Contains(segments[^1], StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    private async Task<WeatherReading> ReadCoreAsync(WeatherLocation location, CancellationToken token)
    {
        var coords = Key(location).Split(',');
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={coords[0]}&longitude={coords[1]}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m&timezone=auto";
        using var json = await ReadJsonAsync(url, token).ConfigureAwait(false);
        var current = json.RootElement.GetProperty("current");
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        var apparent = current.GetProperty("apparent_temperature").GetDouble();
        var humidity = current.GetProperty("relative_humidity_2m").GetInt32();
        var wind = current.GetProperty("wind_speed_10m").GetDouble();
        if (!double.IsFinite(temperature) || !double.IsFinite(apparent) || !double.IsFinite(wind) || humidity is < 0 or > 100) throw new InvalidDataException("Invalid weather values.");
        return new WeatherReading(location, temperature, apparent, humidity, wind, current.GetProperty("weather_code").GetInt32(), clock(), String(current, "time"));
    }
    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
    private static string String(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
