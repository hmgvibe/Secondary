using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Secondary.Core;

/// <summary>User preference for the Chinese writing system used by the UI.</summary>
public enum ChineseLanguage
{
    /// <summary>Follow the OS UI culture (zh-CN / zh-SG / Hans => simplified, otherwise traditional).</summary>
    System = 0,
    /// <summary>Always show Simplified Chinese.</summary>
    Simplified = 1,
    /// <summary>Always show Traditional Chinese - the app's original wording.</summary>
    Traditional = 2
}

/// <summary>
/// Runtime Chinese writing-system conversion. Source strings throughout the app are authored in
/// Traditional Chinese; when Simplified is active they are converted on display using the bundled
/// OpenCC table. The reverse mapping lets the UI recover original wording when switching back.
/// </summary>
public static class ChineseLocale
{
    private static readonly object Gate = new();
    private static readonly Dictionary<char, List<KeyValuePair<string, string>>> PhraseIndex = BuildPhraseIndex();
    private static readonly Dictionary<string, string> Reverse = new(StringComparer.Ordinal);
    private static ChineseLanguage preference;

    public static ChineseLanguage Preference
    {
        get { lock (Gate) return preference; }
        set
        {
            lock (Gate) preference = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised whenever the active preference changes so open UI can refresh.</summary>
    public static event Action? Changed;

    public static bool IsSimplified
    {
        get
        {
            lock (Gate)
            {
                return preference switch
                {
                    ChineseLanguage.Simplified => true,
                    ChineseLanguage.Traditional => false,
                    // Follow the UI language, not the numeric/date region: a zh-CN locale with
                    // an English UI should keep the app's authored (Traditional) wording.
                    _ => IsSimplifiedCulture(CultureInfo.CurrentUICulture)
                };
            }
        }
    }

    private static bool IsSimplifiedCulture(CultureInfo culture)
    {
        var name = culture.Name;
        return name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The culture to use for Chinese date/day formatting.</summary>
    public static CultureInfo DateCulture => IsSimplified
        ? CultureInfo.GetCultureInfo("zh-CN")
        : CultureInfo.GetCultureInfo("zh-TW");

    /// <summary>
    /// Convert authored (Traditional) text to the active writing system. Records the reverse
    /// mapping so already-rendered simplified text can be traced back to its source wording.
    /// </summary>
    public static string Convert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!IsSimplified) return text;
        var result = ToSimplified(text);
        if (!string.Equals(result, text, StringComparison.Ordinal)) RememberReverse(result, text);
        return result;
    }

    /// <summary>Resolve the authored Traditional wording for a displayed string, if known.</summary>
    public static string Original(string? displayed)
    {
        if (string.IsNullOrEmpty(displayed)) return displayed ?? string.Empty;
        lock (Gate) return Reverse.TryGetValue(displayed, out var source) ? source : displayed;
    }

    internal static string ToSimplified(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        var index = 0;
        while (index < text.Length)
        {
            if (PhraseIndex.TryGetValue(text[index], out var phrases))
            {
                var matched = false;
                foreach (var phrase in phrases) // stored longest-first
                {
                    if (index + phrase.Key.Length > text.Length) continue;
                    if (string.CompareOrdinal(text, index, phrase.Key, 0, phrase.Key.Length) != 0) continue;
                    builder.Append(phrase.Value);
                    index += phrase.Key.Length;
                    matched = true;
                    break;
                }
                if (matched) continue;
            }
            var mapped = MapChar(text[index]);
            builder.Append(mapped != '\0' ? mapped : text[index]);
            index++;
        }
        return builder.ToString();
    }

    private static char MapChar(char c)
    {
        var keys = ChineseData.CharKeys;
        var low = 0;
        var high = keys.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var cmp = keys[mid].CompareTo(c);
            if (cmp == 0) return ChineseData.CharValues[mid];
            if (cmp < 0) low = mid + 1; else high = mid - 1;
        }
        return '\0';
    }

    private static Dictionary<char, List<KeyValuePair<string, string>>> BuildPhraseIndex()
    {
        var map = new Dictionary<char, List<KeyValuePair<string, string>>>();
        for (var i = 0; i < ChineseData.PhraseKeys.Length; i++)
        {
            var key = ChineseData.PhraseKeys[i];
            if (string.IsNullOrEmpty(key)) continue;
            if (!map.TryGetValue(key[0], out var list)) map[key[0]] = list = new List<KeyValuePair<string, string>>();
            list.Add(new KeyValuePair<string, string>(key, ChineseData.PhraseValues[i]));
        }
        foreach (var pair in map) pair.Value.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
        return map;
    }

    private static void RememberReverse(string simplified, string traditional)
    {
        lock (Gate)
        {
            if (Reverse.Count > 8192) Reverse.Clear();
            Reverse[simplified] = traditional;
        }
    }
}
