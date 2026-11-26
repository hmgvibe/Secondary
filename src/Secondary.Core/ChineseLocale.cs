using System;
using System.Globalization;

namespace Secondary.Core;

// Simplified Chinese is now the single supported writing system. Source literals are
// authored directly in Simplified Chinese, so conversion is the identity function.
// This shim keeps the historical API surface so existing call sites remain source-compatible;
// the writing-system preference and its change notification are no-ops.
public enum ChineseLanguage
{
    System = 0,
    Simplified = 1,
    Traditional = 2
}

public static class ChineseLocale
{
    private static ChineseLanguage preference = ChineseLanguage.Simplified;
    public static ChineseLanguage Preference
    {
        get => preference;
        set { preference = value; Changed?.Invoke(); }
    }
    public static event Action? Changed;
    public static bool IsSimplified => true;
    public static CultureInfo DateCulture => CultureInfo.GetCultureInfo("zh-CN");
    public static string Convert(string? text) => text ?? string.Empty;
    public static string Original(string? displayed) => displayed ?? string.Empty;
}
