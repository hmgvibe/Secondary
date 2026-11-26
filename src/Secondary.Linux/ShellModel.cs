using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Secondary.Core;

namespace Secondary.Linux;

// Mirrors the WPF ShellViewModel: one settings writer, mode state and display choice.
public sealed class ShellModel : INotifyPropertyChanged, IDisposable
{
    private readonly SettingsStore store;
    public AppSettings Settings { get; private set; }
    public ModeController Modes { get; }
    public ObservableCollection<DisplayInfo> Displays { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    private DisplayInfo? selected;
    public DisplayInfo? Selected { get => selected; set { selected = value; Notify(); } }
    private string status = "选择显示器，开始副屏全屏幕。";
    public string Status { get => status; set { status = value; Notify(); } }
    public bool HasUnsavedSettings => store.NeedsSave;

    public ShellModel(SettingsStore store)
    {
        this.store = store;
        Settings = store.Load();
        Modes = new ModeController(Settings);
        if (store.LastError is not null) Status = "设置无法读取，请重新选择显示器。原始设置已保留。";
    }
    public void SetDisplays(DisplayInfo[] displays)
    {
        var previous = Selected?.Id ?? Settings.DisplayId;
        Displays.Clear();
        foreach (var d in displays) Displays.Add(d);
        Selected = DisplayPolicy.FindSaved(Displays, previous) ?? Displays.FirstOrDefault(d => !d.IsPrimary) ?? DisplayPolicy.Fallback(Displays);
    }
    public void SaveSelection(DisplayInfo display)
    {
        Settings = Settings with { DisplayId = display.Id };
        if (!store.Save(Settings)) Status = "显示器已切换，但设置保存失败：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    public bool RetrySaveSettings()
    {
        if (!store.NeedsSave) return true;
        if (!store.Save(Settings)) { Status = "设置重试保存失败：" + store.LastError; return false; }
        Status = "设置已保存。";
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    public void SaveRuntime(string? theme = null, string? hotkey = null, bool? gameAnimation = null, string? navigationSize = null, string? navigationPosition = null, bool? highSampling = null, string? language = null)
    {
        var next = Modes.Snapshot(Settings) with { Theme = theme ?? Settings.Theme, Hotkey = hotkey ?? Settings.Hotkey, GameAnimation = gameAnimation ?? Settings.GameAnimation, GameHighSampling = highSampling ?? Settings.GameHighSampling, NavigationSize = navigationSize ?? Settings.NavigationSize, NavigationPosition = navigationPosition ?? Settings.NavigationPosition, Language = language is null ? Settings.Language : LanguagePolicy.Validate(language) };
        if (next == Settings && !store.NeedsSave) return;
        Settings = next;
        if (!store.Save(Settings)) Status = "设置保存失败：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    public bool SaveHome(HomeSettings home)
    {
        var next = Modes.Snapshot(Settings) with { Home = home.Normalize() };
        if (!store.Save(next)) { Status = "首页设置保存失败：" + store.LastError; Notify(nameof(HasUnsavedSettings)); return false; }
        Settings = next;
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() => Modes.Dispose();
}
