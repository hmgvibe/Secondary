using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Secondary.Core;

namespace Secondary.Linux;

/// <summary>
/// Applies the active Chinese writing system to materialised Avalonia UI. Mirrors the WPF
/// helper: authored source is Traditional; original wording is cached so switching back is
/// lossless. TextBox.Text is user data and is never translated.
/// </summary>
public static class ChineseLocalization
{
    public static readonly AttachedProperty<string?> SourceTextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("SourceText", typeof(ChineseLocalization));

    public static string? GetSourceText(Control element) => element.GetValue(SourceTextProperty);
    public static void SetSourceText(Control element, string? value) => element.SetValue(SourceTextProperty, value);

    public static void Apply(Visual root) => Visit(root);

    private static void Visit(Visual node)
    {
        ApplyNode(node);
        foreach (var child in node.GetVisualChildren()) Visit(child);
    }

    private static void ApplyNode(Visual node)
    {
        switch (node)
        {
            case TextBlock text:
                Translate(text, TextBlock.TextProperty, text.Text);
                break;
            case TextBox: // user data (sticky notes) - never translate Text
                break;
            case Button button when button.Content is string buttonText:
                Translate(button, Button.ContentProperty, buttonText);
                break;
            case ComboBoxItem item when item.Content is string itemText:
                Translate(item, ComboBoxItem.ContentProperty, itemText);
                break;
            case MenuItem menuItem when menuItem.Header is string header:
                Translate(menuItem, MenuItem.HeaderProperty, header);
                break;
            case Expander expander when expander.Header is string expanderHeader:
                Translate(expander, Expander.HeaderProperty, expanderHeader);
                break;
        }
    }

    private static void Translate(Control element, AvaloniaProperty property, string? current)
    {
        if (current is null) return;
        var source = GetSourceText(element);
        if (source is null)
        {
            source = ChineseLocale.Original(current);
            SetSourceText(element, source);
        }
        var target = ChineseLocale.Convert(source);
        if (!string.Equals(current, target, StringComparison.Ordinal)) element.SetValue(property, target);
    }
}
