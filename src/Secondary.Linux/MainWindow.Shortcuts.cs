using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Secondary.Core;
using Secondary.Linux.Shortcuts;

namespace Secondary.Linux;

public partial class MainWindow
{
    private TaskCompletionSource<ShortcutEntry?>? shortcutEdit;
    private TaskCompletionSource<AppSelection?>? appPick;
    private AppPicker? appPicker;
    public AppPicker? OpenAppPicker => appPicker;
    public bool AppPickerOpen => AppPickerOverlay.IsVisible;
    // Tests substitute a catalog built from fixture directories.
    public Platform.AppCatalog AppCatalog { get; set; } = Platform.AppCatalog.Current;
    private ShortcutEditor? shortcutEditor;
    public LaunchpadView Launchpad { get; private set; } = null!;
    public bool ShortcutEditorOpen => ShortcutEditorOverlay.IsVisible;
    public ShortcutEditor? OpenShortcutEditor => shortcutEditor;

    private void InitializeShortcuts(string? shortcutsPath)
    {
        // Without an explicit path (tests, designer) the launchpad uses a private throwaway file.
        Launchpad = new LaunchpadView(shortcutsPath ?? Path.Combine(Path.GetTempPath(), "secondary-shortcuts-" + Guid.NewGuid().ToString("N") + ".json"), this)
        {
            CanRun = () => model.Modes.Mode != AppMode.Game && IsVisible && WindowState != WindowState.Minimized,
            ViewportHeight = () => Frame.Bounds.Height
        };
        ShortcutsPage.Children.Add(Launchpad);
        // Drops on empty space around the launchpad still import, as the WPF content area does.
        DragDrop.SetAllowDrop(Frame, true);
        Frame.AddHandler(DragDrop.DragOverEvent, (_, e) => { if (CurrentPage == "shortcuts") Launchpad.HandleExternalDrag(e, false); });
        Frame.AddHandler(DragDrop.DropEvent, (_, e) => { if (CurrentPage == "shortcuts") Launchpad.HandleExternalDrag(e, true); });
        Frame.SizeChanged += (_, _) => Launchpad.UpdateHorizontalExtent();
    }

    public Task<ShortcutEntry?> EditShortcutAsync(ShortcutEntry entry, ShortcutRules rules)
    {
        CloseShortcutEditor(null);
        shortcutEdit = new TaskCompletionSource<ShortcutEntry?>();
        // Windows rules mean a main-computer shortcut: this device's files are not on the main computer, so no file picker.
        shortcutEditor = new ShortcutEditor(entry, rules, rules == ShortcutRules.Windows ? null : StorageProvider);
        ShortcutEditorHost.Content = shortcutEditor;
        ShortcutEditorError.Content = shortcutEditor.Error;
        ShortcutEditorHost.Offset = default;
        ShortcutEditorOverlay.IsVisible = true;
        shortcutEditor.NameInput.Focus();
        return shortcutEdit.Task;
    }
    public async Task<(ShortcutEntry Entry, bool Main)?> EditPlacedShortcutAsync(ShortcutEntry entry, string mainName, bool main, bool canChoose)
    {
        CloseShortcutEditor(null);
        var pending = shortcutEdit = new TaskCompletionSource<ShortcutEntry?>();
        var editor = shortcutEditor = new ShortcutEditor(entry, ShortcutRules.Linux, StorageProvider, mainName, main, canChoose);
        ShortcutEditorHost.Content = editor;
        ShortcutEditorError.Content = editor.Error;
        ShortcutEditorHost.Offset = default;
        ShortcutEditorOverlay.IsVisible = true;
        editor.NameInput.Focus();
        return await pending.Task is { } edited ? (edited, editor.Main) : null;
    }
    public void CloseShortcutEditor(ShortcutEntry? result)
    {
        ShortcutEditorOverlay.IsVisible = false;
        ShortcutEditorHost.Content = null; ShortcutEditorError.Content = null;
        shortcutEditor = null;
        var pending = shortcutEdit; shortcutEdit = null;
        pending?.TrySetResult(result);
    }
    public bool SaveShortcutEditor()
    {
        if (shortcutEditor?.TryBuild() is not { } built) return false;
        CloseShortcutEditor(built);
        return true;
    }
    public async Task<AppSelection?> PickAppsAsync(System.Collections.Generic.ISet<string> existing, System.Collections.Generic.IReadOnlyList<string> groups, string group)
    {
        CloseAppPicker(false);
        var pending = appPick = new TaskCompletionSource<AppSelection?>();
        AppPickerStatus.Text = "正在讀取已安裝的 App…"; AddAppsButton.IsEnabled = false;
        AppPickerOverlay.IsVisible = true;
        var catalog = AppCatalog;
        var apps = await Task.Run(catalog.Load);
        if (!ReferenceEquals(appPick, pending)) return await pending.Task;
        appPicker = new AppPicker(apps, catalog, existing, groups, group);
        appPicker.SelectionChanged += UpdateAppPickerStatus;
        AppPickerHost.Content = appPicker;
        UpdateAppPickerStatus();
        appPicker.FocusSearch();
        return await pending.Task;
    }
    private void UpdateAppPickerStatus()
    {
        var count = appPicker?.Selected.Count ?? 0;
        AppPickerStatus.Text = $"共 {appPicker?.Count ?? 0} 個 App · 已選 {count} 個";
        AddAppsButton.IsEnabled = count > 0;
        AddAppsButton.Content = count > 0 ? $"加入 {count} 個" : "加入";
    }
    public void CloseAppPicker(bool accept)
    {
        var chosen = accept && appPicker is not null ? new AppSelection(appPicker.Selected.Select(a => a.DesktopFile).ToArray(), appPicker.Group) : null;
        AppPickerOverlay.IsVisible = false; AppPickerHost.Content = null; appPicker = null;
        var pending = appPick; appPick = null;
        pending?.TrySetResult(chosen);
    }
    private void OnAcceptAppPicker(object? sender, RoutedEventArgs e) => CloseAppPicker(true);
    private void OnCancelAppPicker(object? sender, RoutedEventArgs e) => CloseAppPicker(false);
    private void OnSaveShortcutEditor(object? sender, RoutedEventArgs e) => SaveShortcutEditor();
    private void OnCancelShortcutEditor(object? sender, RoutedEventArgs e) => CloseShortcutEditor(null);
}
