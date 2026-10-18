using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Secondary.Core;

namespace Secondary.Linux.Shortcuts;

// The shortcut editor's content, shown inside the window's own dialog layer.
// Same sections and wording as the WPF ShortcutDialogs.Edit; Linux offers no key step.
public sealed class ShortcutEditor : StackPanel
{
    private readonly ShortcutEntry entry;
    private readonly ShortcutRules localRules;
    private readonly IStorageProvider? localStorage;
    private readonly TextBlock? placementHint, keysNote;
    // In connection mode a shortcut is stored and run here or on the Windows main computer; the rules follow that choice.
    public bool Main { get; private set; }
    public RadioButton? LocalChoice { get; }
    public RadioButton? MainChoice { get; }
    private ShortcutRules rules => Main ? ShortcutRules.Windows : localRules;
    // This device's files are not on the main computer, so the file picker is off for main-computer shortcuts.
    private IStorageProvider? Storage() => Main ? null : localStorage;
    private readonly TextBox name, group, icon;
    private readonly StackPanel stepsPanel = new();
    private readonly List<StepEditor> steps = new();
    private readonly TextBlock stepHeading;
    public TextBlock Error { get; } = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 12) };
    public IReadOnlyList<StepEditor> Steps => steps;
    public TextBox NameInput => name;
    public TextBox GroupInput => group;

    public ShortcutEditor(ShortcutEntry entry, ShortcutRules rules, IStorageProvider? storage, string? mainName = null, bool main = false, bool canChoose = false)
    {
        this.entry = entry; localRules = rules; localStorage = storage; Main = main;
        Error.Bind(TextBlock.ForegroundProperty, Error.GetResourceObservable("AccentBrush"));
        var basic = new StackPanel();
        if (mainName is not null)
        {
            var placement = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            placement.Children.Add(new TextBlock { Text = "儲存與執行位置", FontSize = 16, FontWeight = FontWeight.SemiBold });
            if (canChoose)
            {
                var choices = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                LocalChoice = new RadioButton { Content = "本機", IsChecked = !main, GroupName = "ShortcutPlacement", Margin = new Thickness(0, 0, 20, 0), MinHeight = 44 };
                MainChoice = new RadioButton { Content = "主電腦「" + mainName + "」", IsChecked = main, GroupName = "ShortcutPlacement", MinHeight = 44 };
                LocalChoice.IsCheckedChanged += (_, _) => { if (LocalChoice.IsChecked == true) SetMain(false); };
                MainChoice.IsCheckedChanged += (_, _) => { if (MainChoice.IsChecked == true) SetMain(true); };
                choices.Children.Add(LocalChoice); choices.Children.Add(MainChoice);
                placement.Children.Add(choices);
            }
            else placement.Children.Add(new TextBlock { Text = main ? "主電腦「" + mainName + "」" : "本機", FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
            placementHint = new TextBlock { Classes = { "label" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            placement.Children.Add(placementHint);
            basic.Children.Add(placement);
        }
        basic.Children.Add(new TextBlock { Text = "基本資料", FontSize = 16, FontWeight = FontWeight.SemiBold });
        name = Field(basic, "名稱", entry.Name, 80);
        var metadata = new WrapPanel();
        var groupPanel = new StackPanel { Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        var iconPanel = new StackPanel { Width = 120 };
        group = Field(groupPanel, "分組", entry.Group, 40);
        icon = Field(iconPanel, "圖示／符號", entry.Icon, 8);
        metadata.Children.Add(groupPanel); metadata.Children.Add(iconPanel);
        basic.Children.Add(metadata);
        Children.Add(Surface(basic));
        stepHeading = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 6) };
        Children.Add(stepHeading);
        Children.Add(new TextBlock { Text = "由上往下依序執行；簡單捷徑只需一個「開啟」步驟。", Classes = { "label" }, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var step in entry.Steps ?? [new ShortcutStep()]) steps.Add(new StepEditor(step, Storage));
        var add = new WrapPanel();
        foreach (var (kind, title) in new[] { ("Open", "＋ 開啟"), ("Wait", "＋ 等待"), ("Script", "＋ 腳本") })
            add.Children.Add(Button(title, () => AddStep(kind)));
        Children.Add(stepsPanel); Children.Add(add);
        keysNote = new TextBlock { Classes = { "label" }, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        Children.Add(keysNote);
        Describe();
        Render();
    }
    public void SetMain(bool main)
    {
        Main = main;
        Error.IsVisible = false;
        Describe();
    }
    private void Describe()
    {
        if (placementHint is not null) placementHint.Text = Main
            ? "存在主電腦上並在主電腦執行，套用 Windows 規則；路徑請填主電腦上的位置（例如 C:\\Games\\game.exe）。第一次執行或內容變更後，需要在主電腦上確認。"
            : "存在這臺設備並在這臺設備執行。";
        keysNote!.Text = Main ? "Linux 版編輯器沒有「視窗快捷鍵」步驟；需要時請在 Windows 設備編輯。" : localRules.KeysUnsupported ?? "";
        keysNote.IsVisible = keysNote.Text.Length > 0;
    }
    public void AddStep(string kind)
    {
        if (steps.Count == 20) { ShowError("最多 20 個步驟。"); return; }
        steps.Add(new StepEditor(new ShortcutStep(kind), Storage)); Render();
    }
    public void MoveStep(int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= steps.Count || target < 0 || target >= steps.Count) return;
        var step = steps[index]; steps.RemoveAt(index); steps.Insert(target, step); Render();
    }
    public void RemoveStep(int index) { if (index >= 0 && index < steps.Count) { steps.RemoveAt(index); Render(); } }
    private void ShowError(string text) { Error.Text = text; Error.IsVisible = true; }

    private void Render()
    {
        stepsPanel.Children.Clear();
        stepHeading.Text = $"執行步驟  ·  {steps.Count} / 20";
        for (var i = 0; i < steps.Count; i++)
        {
            var index = i; var step = steps[i];
            var wrapper = new StackPanel();
            var controls = new WrapPanel();
            controls.Children.Add(new TextBlock { Text = $"{i + 1:00}   {step.Caption}", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 8) });
            var up = Button("↑", () => MoveStep(index, -1)); var down = Button("↓", () => MoveStep(index, 1)); var remove = Button("移除此步", () => RemoveStep(index));
            AutomationProperties.SetName(up, $"第 {i + 1} 步往上移"); AutomationProperties.SetName(down, $"第 {i + 1} 步往下移");
            up.IsEnabled = i > 0; down.IsEnabled = i < steps.Count - 1;
            foreach (var button in new[] { up, down, remove }) { button.FontSize = 12; controls.Children.Add(button); }
            wrapper.Children.Add(controls);
            if (step.Parent is Panel old) old.Children.Remove(step);
            wrapper.Children.Add(step);
            stepsPanel.Children.Add(Surface(wrapper));
        }
    }
    // Returns the edited entry, or null with the reason shown when the policy rejects it.
    public ShortcutEntry? TryBuild()
    {
        var candidate = entry with
        {
            Name = (name.Text ?? "").Trim(), Group = string.IsNullOrWhiteSpace(group.Text) ? "未分組" : group.Text.Trim(),
            Icon = icon.Text ?? "", Steps = steps.Select(s => s.Value).ToArray()
        };
        if (ShortcutPolicy.Validate(candidate, rules) is { } problem) { ShowError(problem); return null; }
        Error.IsVisible = false;
        return candidate;
    }

    internal static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (_, _) => click();
        return button;
    }
    internal static TextBox Field(Panel panel, string label, string value, int max = 4096)
    {
        panel.Children.Add(new TextBlock { Text = label, Classes = { "label" }, FontSize = 13, Margin = new Thickness(0, 10, 0, 5) });
        var input = new TextBox { Text = value, MaxLength = max, MinHeight = 44 };
        AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }
    private static Border Surface(Control child)
    {
        var border = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("InsetBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("LineBrush"));
        return border;
    }

    public sealed class StepEditor : StackPanel
    {
        private readonly string kind;
        private readonly TextBox? target, args, working, seconds;
        public string Caption => kind switch { "Open" => "開啟目標", "Wait" => "等待", "Keys" => "視窗快捷鍵（Linux 不支援）", _ => "外部腳本" };
        public TextBox? Target => target;
        public TextBox? Arguments => args;
        public TextBox? Seconds => seconds;
        public StepEditor(ShortcutStep step, Func<IStorageProvider?> storage)
        {
            kind = step.Kind;
            if (kind == "Wait") { seconds = Field(this, "等待秒數 · 1–3600", step.Seconds.ToString(CultureInfo.InvariantCulture), 4); return; }
            target = Field(this, kind == "Script" ? "本機腳本路徑 · .sh（以 /bin/sh 執行）" : "程式、AppImage、檔案、資料夾、.desktop 啟動器或 HTTPS 網址", step.Target);
            if (kind == "Keys") return;
            var browse = Button("瀏覽檔案…", async () =>
            {
                if (storage() is not { } picker) return;
                var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = kind == "Script" ? "選擇腳本" : "選擇目標", AllowMultiple = false,
                    FileTypeFilter = kind == "Script" ? [new FilePickerFileType("Shell 腳本") { Patterns = ["*.sh"] }] : null
                });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) target.Text = path;
            });
            browse.HorizontalAlignment = HorizontalAlignment.Right; browse.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(browse);
            var advanced = new StackPanel { IsVisible = step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0 };
            var expand = Button("進階選項  ⌄", () => advanced.IsVisible = !advanced.IsVisible);
            expand.HorizontalAlignment = HorizontalAlignment.Left; expand.Margin = new Thickness(0, 12, 0, 0);
            Children.Add(expand); Children.Add(advanced);
            if (kind == "Open") args = Field(advanced, "啟動參數 · 僅可執行程式與 AppImage 適用", step.Arguments);
            working = Field(advanced, "工作目錄 · 可留空", step.WorkingDirectory);
            if (kind == "Script") Children.Add(new TextBlock
            {
                Text = "一般權限以 /bin/sh 執行，不使用 sudo 或 pkexec。執行的是你確認時的內容；之後若檔案變更會要求重新確認。不接受額外參數。取消只停止此腳本程序，不保證能撤銷腳本已做的修改。",
                Classes = { "label" }, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }
        public ShortcutStep Value => new(kind, (target?.Text ?? "").Trim(), args?.Text ?? "", (working?.Text ?? "").Trim(),
            int.TryParse(seconds?.Text, out var s) ? s : kind == "Wait" ? 0 : 1);
    }
}
