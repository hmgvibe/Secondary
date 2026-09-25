namespace Secondary.Core;

// Value 2 remains Game so existing numeric settings cannot become Normal accidentally.
public enum AppMode { Normal = 0, Game = 2 }
public static class ModePolicy
{
    public static IReadOnlyList<string> Sections(AppMode mode) => mode == AppMode.Game
        ? Array.AsReadOnly(new[] { "game" }) : Array.AsReadOnly(new[] { "home", "shortcuts" });
    public static string Validate(AppMode mode, string? section)
        => Sections(mode).Contains(section) ? section! : Sections(mode)[0];
}
public interface ISectionModule : IDisposable
{
    void Activate();
    void Suspend();
}
public sealed class PlaceholderModule : ISectionModule
{
    public bool IsActive { get; private set; }
    public void Activate() => IsActive = true;
    public void Suspend() => IsActive = false;
    public void Dispose() => IsActive = false;
}
public sealed class ModeController : IDisposable
{
    private readonly Func<string, ISectionModule> factory;
    private readonly Dictionary<string, ISectionModule> modules = new();
    private string lastNormal;
    public AppMode Mode { get; private set; }
    public AppMode PreviousMode => AppMode.Normal;
    public string Current { get; private set; }
    public ModeController(AppSettings settings, Func<string, ISectionModule>? factory = null)
    {
        this.factory = factory ?? (_ => new PlaceholderModule());
        Mode = settings.Mode == AppMode.Game ? AppMode.Game : AppMode.Normal;
        lastNormal = ModePolicy.Validate(AppMode.Normal, settings.NormalSection);
        Current = Mode == AppMode.Game ? "game" : lastNormal;
        Activate(Current);
    }
    private void Activate(string key)
    {
        if (!modules.TryGetValue(key, out var module)) modules.Add(key, module = factory(key));
        module.Activate();
    }
    public bool Navigate(string key)
    {
        if (!ModePolicy.Sections(Mode).Contains(key)) return false;
        if (Current == key) return true;
        if (modules.TryGetValue(Current, out var active)) active.Suspend();
        Current = key;
        if (Mode == AppMode.Normal) lastNormal = key;
        Activate(key);
        return true;
    }
    public void Switch(AppMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (Mode == mode) return;
        if (modules.TryGetValue(Current, out var active)) active.Suspend();
        foreach (var key in modules.Keys.Where(k => !ModePolicy.Sections(mode).Contains(k)).ToArray())
        { modules[key].Dispose(); modules.Remove(key); }
        Mode = mode;
        Current = mode == AppMode.Game ? "game" : lastNormal;
        Activate(Current);
    }
    public AppSettings Snapshot(AppSettings settings) => settings with
    { Mode = Mode, NormalSection = lastNormal };
    public void Dispose() { foreach (var module in modules.Values) module.Dispose(); modules.Clear(); }
}
