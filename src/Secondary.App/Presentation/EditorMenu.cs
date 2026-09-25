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
        if ((bool)e.NewValue) { box.ContextMenu = new ContextMenu(); box.ContextMenuOpening += Opening; }
        else box.ClearValue(FrameworkElement.ContextMenuProperty);
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
