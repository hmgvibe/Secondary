using System;
using System.Collections.Generic;
using System.Linq;
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

    // Only stacks that explicitly name a CJK font are switched to the unified CJK stack.
    // Everything else (icons, digits, symbols — including combined stacks such as
    // "Segoe Fluent Icons, Segoe MDL2 Assets") is left untouched. Matching individual
    // stack entries instead of the whole string is essential: icon glyphs live in private
    // use area code points that CJK fonts cannot render, so replacing their font shows tofu.
    private static readonly string[] CjkFontMarkers =
    {
        "Microsoft JhengHei", "Microsoft YaHei", "Segoe UI Variable", "SimSun", "SimHei",
        "DengXian", "Noto Sans CJK", "Noto Sans SC", "Noto Sans TC", "微软正黑体", "微软雅黑"
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
            case ComboBox combo:
                // Drop-down items live in a Popup, outside the window's visual tree, so the
                // recursive walk never reaches them. Walk the logical Items collection here.
                foreach (var entry in combo.Items)
                    if (entry is ComboBoxItem ci && ci.Content is string ciText)
                        Translate(ci, ComboBoxItem.ContentProperty, ciText);
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
        var target = (source);
        if (!string.Equals(current, target, StringComparison.Ordinal)) element.SetValue(property, target);
    }

    /// <summary>True only when a font stack explicitly names a CJK font and should be unified.</summary>
    internal static bool ShouldReplaceFont(string? source)
    {
        if (string.IsNullOrEmpty(source)) return false;
        // A FontFamily.Source may be a comma-separated fallback list. Replace only when at
        // least one entry is a CJK font; icon/symbol/digit stacks never contain one.
        return source.Split(',').Any(part =>
            CjkFontMarkers.Any(marker => part.Trim().Contains(marker, StringComparison.OrdinalIgnoreCase)));
    }

    private static void ApplyFont(DependencyObject element, DependencyProperty property, FontFamily? family)
    {
        if (ShouldReplaceFont(family?.Source)) element.SetValue(property, new FontFamily(FontStack));
    }
}
