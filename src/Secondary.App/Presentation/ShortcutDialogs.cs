using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Secondary.Core;

namespace Secondary.App.Presentation;

public static class ShortcutDialogs
{
    public static Button Button(string text, Action click)
    { var button = new Button { Content = text, Margin = new Thickness(0,0,8,8) }; button.Click += (_, _) => click(); return button; }
    public static TextBox Field(Panel panel, string label, string value, int max = 4096)
    {
        var caption = new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0,10,0,5) }; caption.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); panel.Children.Add(caption);
        var input = new TextBox { Text = value, MaxLength = max, MinHeight = 40 }; System.Windows.Automation.AutomationProperties.SetName(input,label); panel.Children.Add(input); return input;
    }
    private static bool Show(Window owner, string title, FrameworkElement content, string accept, Func<bool> validate, TextBlock? validation = null)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = Math.Max(360,Math.Min(800,owner.ActualWidth-40)), Height = Math.Max(320,Math.Min(840,owner.ActualHeight-40)), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResize, MinWidth = 360, MinHeight = 320, FontFamily = owner.FontFamily, FontSize = 14, Resources = owner.Resources };
        dialog.SetResourceReference(Window.BackgroundProperty,"SheetBrush"); dialog.SetResourceReference(Window.ForegroundProperty,"TextBrush");
        var grid = new Grid { Margin = new Thickness(24) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0,0,0,20) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = validation is null ? "检查操作内容后再继续" : "自订入口与执行步骤 · 保存不会启动任何操作", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,0) }; subtitle.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); header.Children.Add(subtitle); grid.Children.Add(header);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0,0,0,16) }; Grid.SetRow(scroll,1); grid.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消",() => dialog.DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        var ok = Button(accept,() => { if (validate()) dialog.DialogResult = true; }); ok.SetResourceReference(Control.BackgroundProperty,"SelectedBrush"); actions.Children.Add(ok);
        var footer = new StackPanel(); if (validation is not null) footer.Children.Add(validation); footer.Children.Add(actions); Grid.SetRow(footer,2); grid.Children.Add(footer);
        dialog.Content = grid; return dialog.ShowDialog() == true;
    }
    public static bool Confirm(Window owner, string title, string details, string accept = "确认") => Show(owner,title,new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontSize = 15 },accept,() => true);
    public static ShortcutEntry? Edit(Window owner, ShortcutEntry entry) => Edit(owner, entry, null, false, false)?.Entry;
    // In connection mode a shortcut is stored and run either here or on the main computer (mainName). A new one can
    // choose; an existing one shows where it is.
    public static (ShortcutEntry Entry, bool Main)? Edit(Window owner, ShortcutEntry entry, string? mainName, bool main, bool canChoose)
    {
        var panel = new StackPanel();
        var basic = new StackPanel();
        if (mainName is not null) basic.Children.Add(Placement(mainName, main, canChoose, value => main = value));
        basic.Children.Add(new TextBlock { Text = "基本数据", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var name = Field(basic,"名称",entry.Name,80);
        var metadata = new WrapPanel(); var groupPanel = new StackPanel { Width = 200, Margin = new Thickness(0,0,16,0) }; var iconPanel = new StackPanel { Width = 120 };
        var group = Field(groupPanel,"分组",entry.Group,40); var icon = Field(iconPanel,"图标／符号",entry.Icon,8); metadata.Children.Add(groupPanel); metadata.Children.Add(iconPanel); basic.Children.Add(metadata);
        panel.Children.Add(Surface(basic));
        var stepHeading = new TextBlock { Text = "执行步骤", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,22,0,6) }; panel.Children.Add(stepHeading);
        var help = new TextBlock { Text = "由上往下依序执行；简单快捷方式只需一个「开启」步骤。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14) }; help.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); panel.Children.Add(help);
        var stepsPanel = new StackPanel(); var steps = new List<StepEditor>();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0,0,0,12) }; error.SetResourceReference(TextBlock.ForegroundProperty,"AccentBrush");
        void Render()
        {
            stepsPanel.Children.Clear();
            stepHeading.Text = $"执行步骤  ·  {steps.Count} / 20";
            foreach (var step in steps)
            {
                var wrapper = new StackPanel();
                var controls = new WrapPanel(); controls.Children.Add(new TextBlock { Text = $"{steps.IndexOf(step)+1:00}   {step.Caption}", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,16,8) });
                controls.Children.Add(Button("↑",() => { var i = steps.IndexOf(step); if (i > 0) { steps.RemoveAt(i); steps.Insert(i-1,step); Render(); } }));
                controls.Children.Add(Button("↓",() => { var i = steps.IndexOf(step); if (i < steps.Count-1) { steps.RemoveAt(i); steps.Insert(i+1,step); Render(); } }));
                controls.Children.Add(Button("移除此步",() => { steps.Remove(step); Render(); })); wrapper.Children.Add(controls);
                var buttons = controls.Children.OfType<Button>().ToArray();
                foreach (var button in buttons) { button.MinHeight = 32; button.Height = 32; button.Padding = new Thickness(10,4,10,4); button.FontSize = 12; }
                buttons[0].IsEnabled = steps.IndexOf(step) > 0; buttons[1].IsEnabled = steps.IndexOf(step) < steps.Count-1;
                if (step.Parent is Panel old) old.Children.Remove(step);
                wrapper.Children.Add(step); stepsPanel.Children.Add(Surface(wrapper));
            }
        }
        foreach (var step in entry.Steps ?? [new ShortcutStep()]) steps.Add(new StepEditor(step,owner));
        var add = new WrapPanel();
        foreach (var (kind,title) in new[] { ("Open","＋ 开启"),("Wait","＋ 等待"),("Keys","＋ 按键"),("Script","＋ 脚本") })
            add.Children.Add(Button(title,() => { if (steps.Count == 20) { error.Text = "最多 20 个步骤。"; error.Visibility = Visibility.Visible; return; } steps.Add(new StepEditor(new ShortcutStep(kind),owner)); Render(); }));
        panel.Children.Add(stepsPanel); panel.Children.Add(add); Render();
        (ShortcutEntry, bool)? result = null;
        Show(owner,"编辑快捷方式",panel,"保存",() =>
        {
            var candidate = entry with { Name = name.Text.Trim(), Group = string.IsNullOrWhiteSpace(group.Text) ? "未分组" : group.Text.Trim(), Icon = icon.Text, Steps = steps.Select(s => s.Value).ToArray() };
            var problem = ShortcutPolicy.Validate(candidate); if (problem is not null) { error.Text = problem; error.Visibility = Visibility.Visible; return false; }
            result = (candidate, main); return true;
        },error);
        return result;
    }
    private static StackPanel Placement(string mainName, bool main, bool canChoose, Action<bool> changed)
    {
        var panel = new StackPanel { Margin = new Thickness(0,0,0,18) };
        panel.Children.Add(new TextBlock { Text = "保存与执行位置", FontSize = 16, FontWeight = FontWeights.SemiBold });
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,0) }; hint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        void Describe(bool onMain) => hint.Text = onMain
            ? "存在主机上并在主机执行；路径请填主机上的位置。第一次执行或内容变更后，需要在主机上确认。"
            : "存在这台设备并在这台设备执行。";
        if (canChoose)
        {
            var choices = new WrapPanel { Margin = new Thickness(0,8,0,0) };
            var local = new RadioButton { Content = "本机", IsChecked = !main, GroupName = "ShortcutPlacement", Margin = new Thickness(0,0,20,0), MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
            var remote = new RadioButton { Content = "主机「" + mainName + "」", IsChecked = main, GroupName = "ShortcutPlacement", MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
            local.SetResourceReference(Control.ForegroundProperty,"TextBrush"); remote.SetResourceReference(Control.ForegroundProperty,"TextBrush");
            local.Checked += (_, _) => { changed(false); Describe(false); };
            remote.Checked += (_, _) => { changed(true); Describe(true); };
            choices.Children.Add(local); choices.Children.Add(remote); panel.Children.Add(choices);
        }
        else panel.Children.Add(new TextBlock { Text = main ? "主机「" + mainName + "」" : "本机", FontSize = 14, Margin = new Thickness(0,8,0,0) });
        Describe(main); panel.Children.Add(hint);
        return panel;
    }
    private static Border Surface(UIElement child)
    {
        var border = new Border { Child = child, Padding = new Thickness(16), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0,0,0,12) };
        border.SetResourceReference(Border.BackgroundProperty,"InsetBrush"); border.SetResourceReference(Border.BorderBrushProperty,"LineBrush"); return border;
    }
    private sealed class StepEditor : StackPanel
    {
        private readonly string kind;
        public string Caption => kind switch { "Open" => "开启目标", "Wait" => "等待", "Keys" => "窗口快捷键", _ => "外部脚本" };
        private readonly TextBox? target, args, working, seconds, process, title;
        public StepEditor(ShortcutStep step, Window owner)
        {
            kind = step.Kind;
            if (kind == "Wait") { seconds = Field(this,"等待秒数 · 1–3600",step.Seconds.ToString(),4); return; }
            target = Field(this,kind == "Keys" ? "快捷键 · 例如 Ctrl+S" : kind == "Script" ? "本机脚本路径 · ps1／bat／cmd" : "程序、文件、文件夹、.lnk 或 HTTPS 网址",step.Target);
            if (kind == "Keys")
            {
                process = Field(this,"程序名称 · 不含 .exe，例如 notepad",step.ProcessName,260);
                title = Field(this,"完整窗口标题 · 必须唯一吻合",step.WindowTitle,512);
                return;
            }
            var browse = Button("浏览文件…",() => { var picker = new OpenFileDialog { Filter = kind == "Script" ? "脚本|*.ps1;*.bat;*.cmd" : "所有文件|*.*", CheckFileExists = true }; if (picker.ShowDialog(Window.GetWindow(this) ?? owner) == true) target.Text = picker.FileName; }); browse.HorizontalAlignment = HorizontalAlignment.Right; browse.Margin = new Thickness(0,8,0,0); Children.Add(browse);
            var advanced = new StackPanel { Visibility = step.Arguments.Length > 0 || step.WorkingDirectory.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
            var expand = Button("进阶选项  ⌄",() => advanced.Visibility = advanced.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible); expand.HorizontalAlignment = HorizontalAlignment.Left; expand.Margin = new Thickness(0,12,0,0); Children.Add(expand); Children.Add(advanced);
            if (kind == "Open") args = Field(advanced,"启动参数 · 仅程序适用",step.Arguments);
            working = Field(advanced,"工作目录 · 可留空",step.WorkingDirectory);
            if (kind == "Script") Children.Add(new TextBlock { Text = "一般权限执行；不绕过 PowerShell 原则。不接受额外参数。取消只停止此脚本程序，不保证能撤销脚本已做的修改。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) });
        }
        public ShortcutStep Value => new(kind,target?.Text.Trim() ?? "",args?.Text ?? "",working?.Text.Trim() ?? "",int.TryParse(seconds?.Text,out var s) ? s : kind == "Wait" ? 0 : 1,process?.Text.Trim() ?? "",title?.Text ?? "");
    }
}
