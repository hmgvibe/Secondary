namespace Secondary.Core;

public sealed record TodoItem(string Text, bool Done = false);

public sealed record WeatherLocation(string Name, string Country, string Region, double Latitude, double Longitude)
{
    public WeatherLocation? Normalize()
    {
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude) || Latitude is < -90 or > 90 || Longitude is < -180 or > 180 || string.IsNullOrWhiteSpace(Name)) return null;
        static string Limit(string? text) => (text ?? "").Trim()[..Math.Min((text ?? "").Trim().Length, 100)];
        return this with { Name = Limit(Name), Country = Limit(Country), Region = Limit(Region) };
    }
    public override string ToString() => string.Join(" · ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public sealed record HomeWidgetSettings(string Id, string Kind, WidgetPlacement? Placement = null, string Text = "", int Minutes = 5,
    TodoItem[]? Tasks = null, int Count = 0, string City = "", string EventTitle = "", string EventDate = "", WeatherLocation? Location = null,
    string MediaSource = "Local")
{
    public static readonly string[] Kinds = ["Calendar", "Note", "Countdown", "Stopwatch", "Todo", "Counter", "TimeProgress", "Media", "Weather", "EventCountdown"];
    public static bool Supported(string? kind) => Kinds.Contains(kind);
    public HomeWidgetSettings Normalize() => this with
    {
        Placement = (Placement ?? new()).Normalize(),
        Text = (Text ?? "")[..Math.Min((Text ?? "").Length, 4000)],
        Minutes = Math.Clamp(Minutes, 1, 1440),
        Count = Math.Clamp(Count, -999999, 999999),
        City = (City ?? "").Trim()[..Math.Min((City ?? "").Trim().Length, 100)],
        Location = Location?.Normalize(),
        // Media widgets show this computer's player, or the main computer's in connection mode.
        MediaSource = MediaSource == "Main" ? "Main" : "Local",
        EventTitle = (EventTitle ?? "").Trim()[..Math.Min((EventTitle ?? "").Trim().Length, 80)],
        EventDate = DateOnly.TryParseExact(EventDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd") : "",
        Tasks = Tasks?.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Text)).Take(20)
            .Select(t => t with { Text = t.Text.Trim()[..Math.Min(t.Text.Trim().Length, 120)] }).ToArray()
    };
}
