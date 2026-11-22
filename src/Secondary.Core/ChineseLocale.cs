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
    private static readonly Dictionary<char, List<KeyValuePair<string, string>>> PhraseIndex;
    private static readonly Dictionary<string, string> Reverse = new(StringComparer.Ordinal);
    private static ChineseLanguage preference;

    // Static field initializers run in text order, so MainlandPhrases (declared below) is not
    // yet assigned when PhraseIndex would be built at its declaration. Build it in the cctor
    // instead, which runs after all field initializers.
    static ChineseLocale() => PhraseIndex = BuildPhraseIndex();

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

    // Traditional wording that differs from Mainland Chinese vocabulary, not just glyph shape.
    // OpenCC's TS table only converts glyphs (設定 -> 设定); these give the natural Mainland term
    // (設定 -> 设置). Keys are the authored Traditional wording, values the Mainland Simplified term.
    private static readonly string[,] MainlandPhrases =
    {
        { "設定", "设置" }, { "設定值", "设置值" }, { "儲存", "保存" }, { "連線", "连接" },
        { "已連線", "已连接" }, { "離線", "离线" }, { "中斷連線", "断开连接" },
        { "網路", "网络" }, { "軟體", "软件" }, { "硬體", "硬件" }, { "資訊", "信息" },
        { "專案", "项目" }, { "介面", "界面" }, { "畫面", "界面" }, { "螢幕", "屏幕" },
        { "滑鼠", "鼠标" }, { "印表機", "打印机" }, { "程式", "程序" }, { "檔案", "文件" },
        { "目錄", "目录" }, { "資料", "数据" }, { "資料庫", "数据库" }, { "記憶體", "内存" },
        { "記憶體用量", "内存占用" }, { "視窗", "窗口" }, { "工具列", "工具栏" },
        { "圖示", "图标" }, { "驅動程式", "驱动程序" }, { "驅動", "驱动" },
        { "支援", "支持" }, { "預設", "默认" }, { "預設值", "默认值" },
        { "選項", "选项" }, { "選擇", "选择" }, { "服務", "服务" }, { "品質", "质量" },
        { "效能", "性能" }, { "解析度", "分辨率" },         { "更新率", "刷新率" }, { "登入", "登录" }, { "登出", "退出" },
        { "登入時啟動", "登录时启动" }, { "系統匣", "系统托盘" }, { "托盤", "托盘" },
        { "快捷鍵", "快捷键" }, { "通知訊息", "通知消息" }, { "警示", "警告" },
        { "便箋", "便签" }, { "喚醒", "唤醒" },
        { "顯示器", "显示器" }, { "副螢幕", "副屏" }, { "主螢幕", "主屏" },
        { "語言", "语言" }, { "繁體中文", "繁体中文" }, { "簡體中文", "简体中文" },
        { "跟隨系統", "跟随系统" }, { "外觀", "外观" },
        { "重新連接", "重新连接" }, { "開機", "开机" }, { "啟動", "启动" },
        { "關閉", "关闭" }, { "離開", "离开" }, { "重新啟動", "重启" },
        { "裝置", "设备" }, { "效能監測", "性能监测" }, { "資源", "资源" },
        { "內容", "内容" }, { "開始", "开始" }, { "暫停", "暂停" },
        { "繼續", "继续" }, { "結束", "结束" }, { "進度", "进度" },
        { "狀態", "状态" }, { "設定檔", "配置文件" }, { "備份", "备份" },
        { "還原", "恢复" }, { "新增", "添加" },
        { "編輯", "编辑" }, { "刪除", "删除" }, { "搜尋", "搜索" },
        { "連線模式", "连接模式" }, { "一般模式", "普通模式" }, { "遊戲模式", "游戏模式" },
        { "導航欄", "导航栏" }, { "快捷導航", "快捷导航" }, { "首頁", "首页" },
    };

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
        // Mainland vocabulary overrides take precedence (longer entries matched first).
        for (var i = 0; i < MainlandPhrases.GetLength(0); i++)
        {
            var key = MainlandPhrases[i, 0];
            if (!map.TryGetValue(key[0], out var list)) map[key[0]] = list = new List<KeyValuePair<string, string>>();
            list.Add(new KeyValuePair<string, string>(key, MainlandPhrases[i, 1]));
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
