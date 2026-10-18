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
    private string status = "選擇顯示器，開始副屏全螢幕。";
    public string Status { get => status; set { status = value; Notify(); } }
    public bool HasUnsavedSettings => store.NeedsSave;

    public ShellModel(SettingsStore store)
    {
        this.store = store;
        Settings = store.Load();
        Modes = new ModeController(Settings);
        if (store.LastError is not null) Status = "設定無法讀取，請重新選擇顯示器。原始設定已保留。";
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
        if (!store.Save(Settings)) Status = "顯示器已切換，但設定儲存失敗：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    public bool RetrySaveSettings()
    {
        if (!store.NeedsSave) return true;
        if (!store.Save(Settings)) { Status = "設定重試儲存失敗：" + store.LastError; return false; }
        Status = "設定已儲存。";
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    public void SaveRuntime(string? theme = null, string? hotkey = null, bool? gameAnimation = null, string? navigationSize = null, string? navigationPosition = null, bool? highSampling = null)
    {
        var next = Modes.Snapshot(Settings) with { Theme = theme ?? Settings.Theme, Hotkey = hotkey ?? Settings.Hotkey, GameAnimation = gameAnimation ?? Settings.GameAnimation, GameHighSampling = highSampling ?? Settings.GameHighSampling, NavigationSize = navigationSize ?? Settings.NavigationSize, NavigationPosition = navigationPosition ?? Settings.NavigationPosition };
        if (next == Settings && !store.NeedsSave) return;
        Settings = next;
        if (!store.Save(Settings)) Status = "設定儲存失敗：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    public bool SaveHome(HomeSettings home)
    {
        var next = Modes.Snapshot(Settings) with { Home = home.Normalize() };
        if (!store.Save(next)) { Status = "首頁設定儲存失敗：" + store.LastError; Notify(nameof(HasUnsavedSettings)); return false; }
        Settings = next;
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() => Modes.Dispose();
}
