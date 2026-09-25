namespace Secondary.Core;

public sealed record DisplayInfo(string Id, string Name, bool IsPrimary, int X, int Y, int Width, int Height)
{
    public string Label => $"{Name} · {Width} × {Height}" + (IsPrimary ? " · 主螢幕" : " · 副螢幕");
}

public static class DisplayPolicy
{
    public static DisplayInfo? FindSaved(IEnumerable<DisplayInfo> displays, string? id) =>
        id is null ? null : displays.FirstOrDefault(d => d.Id == id);
    public static DisplayInfo? Fallback(IEnumerable<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
}
