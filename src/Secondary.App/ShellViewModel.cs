using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Secondary.Core;

namespace Secondary.App;

public sealed class ShellViewModel : INotifyPropertyChanged, IDisposable
{
    public Presentation.ShellPresentation Presentation { get; } = new();
    private readonly DisplayService service;
    private readonly SettingsStore store;
    public AppSettings Settings { get; private set; }
    public ModeController Modes { get; }
    public Monitoring.SystemDashboardModel SystemDashboard { get; } = new();
    public int GameSamplingIndex
    {
        get => Settings.GameHighSampling ? 1 : 0;
        set { if (value is < 0 or > 1 || ((value == 1) == Settings.GameHighSampling && !store.NeedsSave)) return; SaveRuntime(highSampling: value == 1); Notify(); }
    }
    public ObservableCollection<DisplayInfo> Displays { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TopologyChanged;
    private DisplayInfo? selected;
    public DisplayInfo? Selected { get => selected; set { selected = value; Notify(); } }
    private string status = "選擇顯示器，開始副屏全螢幕。";
    public string Status { get => status; set { status = value; Notify(); } }
    public bool HasUnsavedSettings => store.NeedsSave;
    public bool RetrySaveSettings()
    {
        if (!store.NeedsSave) return true;
        if (!store.Save(Settings)) { Status = "設定重試儲存失敗：" + store.LastError; return false; }
        Status = "設定已儲存。";
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    public ShellViewModel(DisplayService service, SettingsStore store)
    {
        this.service = service;
        this.store = store;
        Settings = store.Load();
        Modes = new ModeController(Settings);
        if (store.LastError is not null) Status = "設定無法讀取，請重新選擇顯示器。原始設定已保留。";
        Refresh();
        service.Changed += OnDisplayChange;
    }
    private void OnDisplayChange() => Application.Current.Dispatcher.BeginInvoke(() => { Refresh(); TopologyChanged?.Invoke(); });
    public void Refresh()
    {
        var previous = Selected?.Id ?? Settings.DisplayId;
        Displays.Clear();
        foreach (var d in service.GetDisplays()) Displays.Add(d);
        Selected = DisplayPolicy.FindSaved(Displays, previous) ?? Displays.FirstOrDefault(d => !d.IsPrimary) ?? DisplayPolicy.Fallback(Displays);
    }
    public void SaveSelection(DisplayInfo display)
    {
        Settings = Settings with { DisplayId = display.Id };
        if (!store.Save(Settings)) Status = "顯示器已切換，但設定儲存失敗：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void SaveRuntime(string? theme = null, string? hotkey = null, bool? highSampling = null, bool? gameAnimation = null, string? navigationSize = null, string? navigationPosition = null)
    {
        var next = Modes.Snapshot(Settings) with { Theme = theme ?? Settings.Theme, Hotkey = hotkey ?? Settings.Hotkey, GameHighSampling = highSampling ?? Settings.GameHighSampling, GameAnimation = gameAnimation ?? Settings.GameAnimation, NavigationSize = navigationSize ?? Settings.NavigationSize, NavigationPosition = navigationPosition ?? Settings.NavigationPosition };
        if (next == Settings && !store.NeedsSave) return;
        Settings = next;
        if (!store.Save(Settings)) Status = "設定儲存失敗：" + store.LastError;
        Notify(nameof(HasUnsavedSettings));
    }
    public void SetMonitoringVisible(bool visible)
    {
        // Real monitoring returns in the data-source phase.
    }
    public bool SaveHome(HomeSettings home)
    {
        var next = Modes.Snapshot(Settings) with { Home = home.Normalize() };
        if (!store.Save(next)) { Status = "首頁設定儲存失敗：" + store.LastError; Notify(nameof(HasUnsavedSettings)); return false; }
        Settings = next;
        Notify(nameof(HasUnsavedSettings));
        return true;
    }
    public void Dispose() { service.Changed -= OnDisplayChange; Modes.Dispose(); }
}
