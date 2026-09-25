using System.Text.Json;

namespace Secondary.Core;

public sealed record AppSettings(int Version = 3, string? DisplayId = null,
    AppMode Mode = AppMode.Normal, string NormalSection = "home",
    string Theme = "System", string Hotkey = "Ctrl+Alt+G", bool GameHighSampling = false,
    bool GameAnimation = true, HomeSettings? Home = null, string NavigationSize = "Standard", string NavigationPosition = "Bottom");

public sealed class SettingsStore(string path)
{
    public string? LastError { get; private set; }
    private bool preserveOriginal;
    public bool NeedsSave { get; private set; }
    public AppSettings Load()
    {
        LastError = null;
        NeedsSave = false;
        try
        {
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (settings is null || settings.Version is < 1 or > 3) throw new InvalidDataException("不支援的設定版本");
            preserveOriginal = settings.Version < 3;
            NeedsSave = preserveOriginal;
            return settings with { Version = 3,
                Mode = settings.Mode == AppMode.Game ? AppMode.Game : AppMode.Normal,
                NormalSection = settings.Version < 3 ? "home" : ModePolicy.Validate(AppMode.Normal, settings.NormalSection),
                Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System",
                Hotkey = HotkeyGesture.TryParse(settings.Hotkey, out var gesture) ? gesture!.ToString() : "Ctrl+Alt+G",
                Home = settings.Home?.Normalize(),
                NavigationSize = settings.NavigationSize == "Small" ? "Small" : "Standard",
                NavigationPosition = settings.NavigationPosition is "Top" or "Left" or "Right" ? settings.NavigationPosition : "Bottom" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LastError = e.Message;
            preserveOriginal = true;
            return new();
        }
    }

    public bool Save(AppSettings settings)
    {
        LastError = null;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (preserveOriginal && File.Exists(path))
            { File.Copy(path, path + "." + Guid.NewGuid().ToString("N") + ".bak"); preserveOriginal = false; }
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            NeedsSave = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = e.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
