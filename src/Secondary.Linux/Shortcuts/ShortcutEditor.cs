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
            placement.Children.Add(new TextBlock { Text = "保存与执行位置", FontSize = 16, FontWeight = FontWeight.SemiBold });
            if (canChoose)
            {
                var choices = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                LocalChoice = new RadioButton { Content = "本机", IsChecked = !main, GroupName = "ShortcutPlacement", Margin = new Thickness(0, 0, 20, 0), MinHeight = 44 };
                MainChoice = new RadioButton { Content = "主机「" + mainName + "」", IsChecked = main, GroupName = "ShortcutPlacement", MinHeight = 44 };
                LocalChoice.IsCheckedChanged += (_, _) => { if (LocalChoice.IsChecked == true) SetMain(false); };
                MainChoice.IsCheckedChanged += (_, _) => { if (MainChoice.IsChecked == true) SetMain(true); };
                choices.Children.Add(LocalChoice); choices.Children.Add(MainChoice);
                placement.Children.Add(choices);
            }
            else placement.Children.Add(new TextBlock { Text = main ? "主机「" + mainName + "」" : "本机", FontSize = 14, Margin = new Thickness(0, 8, 0, 0) });
            placementHint = new TextBlock { Classes = { "label" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            placement.Children.Add(placementHint);
            basic.Children.Add(placement);
        }
        basic.Children.Add(new TextBlock { Text = "基本数据", FontSize = 16, FontWeight = FontWeight.SemiBold });
        name = Field(basic, "名称", entry.Name, 80);
        var metadata = new WrapPanel();
        var groupPanel = new StackPanel { Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        var iconPanel = new StackPanel { Width = 120 };
        group = Field(groupPanel, "分组", entry.Group, 40);
        icon = Field(iconPanel, "图标／符号", entry.Icon, 8);
        metadata.Children.Add(groupPanel); metadata.Children.Add(iconPanel);
        basic.Children.Add(metadata);
        Children.Add(Surface(basic));
        stepHeading = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 6) };
        Children.Add(stepHeading);
        Children.Add(new TextBlock { Text = "由上往下依序执行；简单快捷方式只需一个「开启」步骤。", Classes = { "label" }, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) });
        foreach (var step in entry.Steps ?? [new ShortcutStep()]) steps.Add(new StepEditor(step, Storage));
        var add = new WrapPanel();
        foreach (var (kind, title) in new[] { ("Open", "＋ 开启"), ("Wait", "＋ 等待"), ("Script", "＋ 脚本") })
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
            ? "存在主机上并在主机执行，套用 Windows 规则；路径请填主机上的位置（例如 C:\\Games\\game.exe）。第一次执行或内容变更后，需要在主机上确认。"
            : "存在这台设备并在这台设备执行。";
        keysNote!.Text = Main ? "Linux 版编辑器没有「窗口快捷键」步骤；需要时请在 Windows 设备编辑。" : localRules.KeysUnsupported ?? "";
        keysNote.IsVisible = keysNote.Text.Length > 0;
    }
    public void AddStep(string kind)
    {
        if (steps.Count == 20) { ShowError("最多 20 个步骤。"); return; }
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
        stepHeading.Text = $"执行步骤  ·  {steps.Count} / 20";
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
            Name = (name.Text ?? "").Trim(), Group = string.IsNullOrWhiteSpace(group.Text) ? "未分组" : group.Text.Trim(),
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
        public string Caption => kind switch { "Open" => "开启目标", "Wait" => "等待", "Keys" => "窗口快捷键（Linux 不支持）", _ => "外部脚本" };
        public TextBox? Target => target;
        public TextBox? Arguments => args;
        public TextBox? Seconds => seconds;
        public StepEditor(ShortcutStep step, Func<IStorageProvider?> storage)
        {
            kind = step.Kind;
            if (kind == "Wait") { seconds = Field(this, "等待秒数 · 1–3600", step.Seconds.ToString(CultureInfo.InvariantCulture), 4); return; }
            target = Field(this, kind == "Script" ? "本机脚本路径 · .sh（以 /bin/sh 执行）" : "程序、AppImage、文件、文件夹、.desktop 启动器或 HTTPS 网址", step.Target);
            if (kind == "Keys") return;
            var browse = Button("浏览文件…", async () =>
            {
                if (storage() is not { } picker) return;
                var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = kind == "Script" ? "选择脚本" : "选择目标", AllowMultiple = false,
                    FileTypeFilter = kind == "Script" ? [new FilePickerFileType("Shell 脚本") { Patterns = ["*.sh"] }] : null
                });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) target.Text = path;
            });
            browse.HorizontalAlignment = HorizontalAlignment.Right; browse.Margin = new Thickness(0, 8, 0, 0);
            Children.Add(browse);
            var advanced = new StackPanel { IsVisible = step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0 };
            var expand = Button("进阶选项  ⌄", () => advanced.IsVisible = !advanced.IsVisible);
            expand.HorizontalAlignment = HorizontalAlignment.Left; expand.Margin = new Thickness(0, 12, 0, 0);
            Children.Add(expand); Children.Add(advanced);
            if (kind == "Open") args = Field(advanced, "启动参数 · 仅可执行程序与 AppImage 适用", step.Arguments);
            working = Field(advanced, "工作目录 · 可留空", step.WorkingDirectory);
            if (kind == "Script") Children.Add(new TextBlock
            {
                Text = "一般权限以 /bin/sh 执行，不使用 sudo 或 pkexec。执行的是你确认时的内容；之后若文件变更会要求重新确认。不接受额外参数。取消只停止此脚本程序，不保证能撤销脚本已做的修改。",
                Classes = { "label" }, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }
        public ShortcutStep Value => new(kind, (target?.Text ?? "").Trim(), args?.Text ?? "", (working?.Text ?? "").Trim(),
            int.TryParse(seconds?.Text, out var s) ? s : kind == "Wait" ? 0 : 1);
    }
}
