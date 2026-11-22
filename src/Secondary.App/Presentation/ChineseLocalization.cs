using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Secondary.Core;

namespace Secondary.App.Presentation;

/// <summary>
/// Applies the active Chinese writing system to already-materialised UI. Source strings are
/// authored in Traditional Chinese; the original wording is cached on each element so switching
/// back is lossless. CJK-capable controls are also switched to a single matching font family,
/// which avoids the mixed-font metrics that caused clipped/uneven Simplified Chinese labels.
/// </summary>
public static class ChineseLocalization
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.RegisterAttached(
        "SourceText", typeof(string), typeof(ChineseLocalization), new PropertyMetadata(null));

    public static string? GetSourceText(DependencyObject element) => (string?)element.GetValue(SourceTextProperty);
    public static void SetSourceText(DependencyObject element, string? value) => element.SetValue(SourceTextProperty, value);

    internal static string FontStack => ChineseLocale.IsSimplified
        ? "Segoe UI Variable, Microsoft YaHei UI, Microsoft JhengHei UI"
        : "Segoe UI Variable, Microsoft JhengHei UI, Microsoft YaHei UI";

    // Fonts chosen for digits/icons must not be replaced by the CJK stack.
    private static readonly HashSet<string> PreserveFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bahnschrift", "Consolas", "Segoe MDL2 Assets", "Segoe Fluent Icons", "Segoe UI Symbol", "monospace"
    };

    public static void Apply(DependencyObject root)
    {
        Visit(root);
    }

    private static void Visit(DependencyObject node)
    {
        ApplyNode(node);
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is not null) Visit(child);
        }
    }

    private static void ApplyNode(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock text:
                Translate(text, TextBlock.TextProperty, text.Text);
                ApplyFont(text, TextBlock.FontFamilyProperty, text.FontFamily);
                break;
            case TextBox box:
                // TextBox.Text is user data (e.g. sticky notes) - never translate it.
                ApplyFont(box, Control.FontFamilyProperty, box.FontFamily);
                break;
            case ButtonBase button when button.Content is string buttonText:
                Translate(button, ButtonBase.ContentProperty, buttonText);
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
            case ToolTip toolTip when toolTip.Content is string tip:
                Translate(toolTip, ToolTip.ContentProperty, tip);
                break;
        }
    }

    private static void Translate(DependencyObject element, DependencyProperty property, string? current)
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

    private static void ApplyFont(DependencyObject element, DependencyProperty property, FontFamily? family)
    {
        var name = family?.Source;
        if (!string.IsNullOrEmpty(name) && PreserveFonts.Contains(name)) return;
        element.SetValue(property, new FontFamily(FontStack));
    }
}
