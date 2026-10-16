using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Secondary.App.Presentation;

// Keep WPF's text editing/IME and routed commands; replace only its native-looking chrome.
public static class EditorMenu
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(EditorMenu), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty,value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not TextBox box) return;
        box.ContextMenuOpening -= Opening;
        CommandManager.RemovePreviewCanExecuteHandler(box, CanExecute);
        CommandManager.RemovePreviewExecutedHandler(box, Executed);
        if ((bool)e.NewValue)
        {
            box.ContextMenu = new ContextMenu(); box.ContextMenuOpening += Opening;
            CommandManager.AddPreviewCanExecuteHandler(box, CanExecute);
            CommandManager.AddPreviewExecutedHandler(box, Executed);
        }
        else box.ClearValue(FrameworkElement.ContextMenuProperty);
    }
    private static void CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is not TextBox box || e.Command != ApplicationCommands.Paste) return;
        // WPF's default paste query can throw while the clipboard is denied or
        // owned by another process. Handle the preview so that query cannot escape.
        e.Handled = true; e.CanExecute = false;
        if (!box.IsEnabled || box.IsReadOnly) return;
        try { e.CanExecute = Clipboard.ContainsText(); }
        catch (Exception error) when (error is UnauthorizedAccessException or ExternalException) { }
    }
    private static void Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not TextBox box ||
            e.Command != ApplicationCommands.Paste && e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
        e.Handled = true;
        if (!box.IsEnabled || box.IsReadOnly && e.Command != ApplicationCommands.Copy) return;
        try
        {
            // These methods enter WPF's native text editor directly; no command
            // recursion, manual text replacement, or custom undo/IME handling.
            if (e.Command == ApplicationCommands.Paste) box.Paste();
            else if (e.Command == ApplicationCommands.Copy) box.Copy();
            else box.Cut();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or ExternalException) { }
    }
    private static void Opening(object sender, ContextMenuEventArgs e)
    {
        var box = (TextBox)sender;
        Populate(box,box.ContextMenu);
    }
    public static void Populate(TextBox box, ContextMenu menu)
    {
        menu.Items.Clear(); menu.Style = (Style)box.FindResource("WidgetMenu");
        if (Window.GetWindow(box) is { } window) menu.Resources = window.Resources;
        foreach (var (title, gesture, command) in new[] {
            ("復原", "Ctrl+Z", ApplicationCommands.Undo), ("剪下", "Ctrl+X", ApplicationCommands.Cut),
            ("複製", "Ctrl+C", ApplicationCommands.Copy), ("貼上", "Ctrl+V", ApplicationCommands.Paste),
            ("全選", "Ctrl+A", ApplicationCommands.SelectAll) })
        {
            menu.Items.Add(new MenuItem { Header = title, InputGestureText = gesture, Command = command,
                CommandTarget = box, Style = (Style)box.FindResource("WidgetMenuItem") });
        }
    }
}
