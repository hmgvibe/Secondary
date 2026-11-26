using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Linux.Monitoring;
using Secondary.Shared;
using Secondary.Shared.Connection;

namespace Secondary.Linux;

public partial class MainWindow
{
    private CancellationTokenSource? gameCancellation, transition;
    private Task gameCompletion = Task.CompletedTask;
    private IGameSource? gameSession;
    // Connection mode: the main computer's process to follow, and why this device fell back to its own data.
    private ConnectionController? connection;
    private RemoteProcess? remoteTarget;
    private bool lastGameRemote;
    private string? connectionNote;
    private Flyout? remoteProcessMenu;
    public bool GameRemote => gameSession is RemoteGameSource;
    public RemoteProcess[] RemoteProcessChoices { get; private set; } = [];
    public bool RemoteProcessMenuOpen => remoteProcessMenu?.IsOpen == true;
    private string? gameConfiguration;
    private DispatcherTimer? gameTimer;
    private bool gameDisposed;
    private int displayedFrameGeneration = -1;
    private readonly Stopwatch gameClock = Stopwatch.StartNew();
    private TelemetryHistory cpuHistory = new(), gpuHistory = new(), frameHistory = new(), downloadHistory = new(), uploadHistory = new();
    private sealed record GpuChoice(string Identity, string Name) { public override string ToString() => Name; }
    public bool GameMonitoringActive => gameSession is not null;
    public Task GameMonitoringCompletion => gameCompletion;
    public long GameMonitoringUpdateCount { get; private set; }
    public GameSnapshot? LastGameSnapshot { get; private set; }
    public bool TransitionRunning => GameTransition.IsVisible;
    private TranslateTransform ScanTransform => (TranslateTransform)ScanLine.RenderTransform!;
    public bool CompactGameLayout => CompactGameSummary.IsVisible;
    // Tests point these at fixture /proc, /sys and log folders before entering game mode.
    public string TelemetryRoot { get; set; } = "/";
    public string MangoHudFolder { get; set; } = MangoHudLog.DefaultFolder(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void InitializeGame()
    {
        SamplingPicker.SelectedIndex = model.Settings.GameHighSampling ? 1 : 0;
        ClearGameReadings();
    }
    private void OnGameSamplingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        model.SaveRuntime(highSampling: SamplingPicker.SelectedIndex == 1);
        ConfigureGameTelemetry();
    }
    private void OnGameGpuChanged(object? sender, SelectionChangedEventArgs e) => gpuHistory = new();

    // Sampling runs only while the game page is on screen; leaving it, hiding, minimizing,
    // locking or sleeping stops the workers, and coming back starts a fresh baseline.
    private void UpdateGameActivity()
    {
        if (transition is not null && (model.Modes.Mode != AppMode.Game || settingsOpen || !IsVisible || WindowState == WindowState.Minimized || !AnimationsEnabled)) CancelTransition();
        ConfigureGameTelemetry();
    }
    private void ConfigureGameTelemetry()
    {
        if (initializing || gameDisposed) return;
        var active = IsVisible && WindowState != WindowState.Minimized && CurrentPage == "game" && !sessionLocked && !suspended;
        // While connected the page shows the main computer: its process picker and sensors replace MangoHud.
        var remote = connection?.State == LinkState.Connected;
        if (!remote && remoteTarget is not null) { remoteTarget = null; RemoteProcessPicker.Content = "选择主机游戏程序  ⌄"; }
        RemoteProcessPicker.IsVisible = RemoteSensorsToggle.IsVisible = remote;
        MangoHudCard.IsVisible = !remote;
        GameSourceCaption.Text = remote ? "主机「" + connection!.ConnectedName + "」/ PresentMon 帧率" : "整机监测 / MangoHud 帧率";
        var configuration = active ? $"{remote}:{SamplingPicker.SelectedIndex}:{TelemetryRoot}:{MangoHudFolder}:{RemoteSensorsToggle.IsChecked}:{remoteTarget?.Id}:{remoteTarget?.Started}" : null;
        if (configuration == gameConfiguration) return;
        gameConfiguration = configuration;
        var old = gameCancellation;
        gameCancellation = null;
        old?.Cancel();
        gameTimer?.Stop();
        gameSession = null;
        ClearGameReadings();
        if (!active) { connectionNote = null; GameSourceState.Text = "PAUSED"; GameNotice.Text = "监测已暂停；返回可见游戏页后重新建立采样基准。"; return; }
        connectionNote = remote ? null : lastGameRemote ? "已与主机断开连接（" + connection?.Status + "），改用这台设备的数据。" : connectionNote;
        lastGameRemote = remote;
        if (!remote) PrepareMangoHud();
        var previous = gameCompletion;
        var owner = new CancellationTokenSource();
        gameCancellation = owner;
        IGameSource session = remote
            ? new RemoteGameSource(connection!, SamplingPicker.SelectedIndex == 1, RemoteSensorsToggle.IsChecked == true, remoteTarget)
            : new GameTelemetrySession(SamplingPicker.SelectedIndex == 1, new SystemSampler(TelemetryRoot), new HardwareSampler(TelemetryRoot), new MangoHudLog(MangoHudFolder));
        gameSession = session;
        GameSourceState.Text = remote ? "REMOTE · WARMUP" : "LIVE / WARMUP";
        GameNotice.Text = remote ? $"正在等待主机「{connection!.ConnectedName}」的数据。" : (connectionNote is { } note ? note + "\n" : "") + "正在建立采样基准；CPU、内存与网络为整机数据。";
        // A new generation starts only after the previous one has stopped reading.
        gameCompletion = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                owner.Token.ThrowIfCancellationRequested();
                await session.RunAsync(owner.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                AppDiagnostics.Write("game-worker", error);
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(gameSession, session)) GameNotice.Text = "监测工作已停止；切换页面可重试。"; });
            }
        });
        gameTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        gameTimer.Tick -= OnGameTick;
        gameTimer.Tick += OnGameTick;
        gameTimer.Start();
    }

    // MangoHud writes into an existing folder only, so create it before showing the launch option.
    private void PrepareMangoHud()
    {
        MangoHudOption.Text = MangoHudLog.LaunchOption(MangoHudFolder);
        string? problem = null;
        try { Directory.CreateDirectory(MangoHudFolder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = "无法建立记录文件夹：" + error.Message; }
        var installed = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, "mangohud")));
        MangoHudStatus.Text = problem ?? (installed ? "已安装 MangoHud · 记录文件夹：" + MangoHudFolder
            : "找不到 mangohud 指令；请先安装（Ubuntu：sudo apt install mangohud）。记录文件夹：" + MangoHudFolder);
    }
    private void OnRemoteSensorsChanged(object? sender, RoutedEventArgs e) { if (!initializing) ConfigureGameTelemetry(); }

    // Lists the main computer's processes in a searchable menu; the choice is sampled with PresentMon there.
    private async void OnPickRemoteProcess(object? sender, RoutedEventArgs e)
    {
        if (connection is not { State: LinkState.Connected } link) return;
        RemoteProcessPicker.IsEnabled = false;
        try
        {
            RemoteProcessChoices = await RemoteGame.ListProcessesAsync(link);
            if (CurrentPage != "game" || gameDisposed) return;
            var search = new TextBox { Width = 320, PlaceholderText = "搜索名称或 PID" };
            AutomationProperties.SetName(search, "搜索主机程序名称或 PID");
            var list = new ListBox { Width = 320, MaxHeight = 360 };
            AutomationProperties.SetName(list, "主机的程序");
            void Populate()
            {
                var text = search.Text ?? "";
                list.ItemsSource = new[] { new ProcessChoice(null, "不追踪程序（只监测整机）") }.Concat(RemoteProcessChoices
                    .Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal))
                    .Take(80).Select(p => new ProcessChoice(p, p.Name + " · PID " + p.Id))).ToArray();
            }
            search.TextChanged += (_, _) => Populate();
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is ProcessChoice choice) ChooseRemoteProcess(choice.Process); };
            Populate();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "主机「" + link.ConnectedName + "」的程序 · 最多显示 80 笔", FontSize = 12 });
            panel.Children.Add(search);
            panel.Children.Add(list);
            remoteProcessMenu = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            remoteProcessMenu.ShowAt(RemoteProcessPicker);
            search.Focus();
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException) { GameNotice.Text = "无法取得主机的程序：" + error.Message; }
        finally { RemoteProcessPicker.IsEnabled = true; }
    }
    private sealed record ProcessChoice(RemoteProcess? Process, string Label) { public override string ToString() => Label; }
    public void ChooseRemoteProcess(RemoteProcess? process)
    {
        remoteProcessMenu?.Hide();
        remoteTarget = process;
        RemoteProcessPicker.Content = process is null ? "选择主机游戏程序  ⌄" : process.Name + " · " + process.Id;
        ConfigureGameTelemetry();
    }

    private async void OnCopyMangoHud(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null || MangoHudOption.Text is not { } option) return;
        await Clipboard.SetTextAsync(option);
        MangoHudStatus.Text = "已复制启动选项。贴到 Steam 游戏的「内容 → 启动选项」后开启游戏。";
    }

    private void ClearGameReadings()
    {
        FpsValue.Text = LowValue.Text = FrameValue.Text = CpuValue.Text = GpuValue.Text = MemoryValue.Text = VramValue.Text = "—";
        CpuGauge.Value = GpuGauge.Value = MemoryGauge.Value = VramGauge.Value = double.NaN;
        CpuDetails.Text = "整机 CPU · 等待基准";
        GpuDetails.Text = "GPU · 等待基准";
        MemoryDetails.Text = VramDetails.Text = "— / —";
        VramCaption.Text = "等待硬件采样";
        DownloadValue.Text = "━  ↓ —"; UploadValue.Text = "┄  ↑ —";
        cpuHistory = new(); gpuHistory = new(); frameHistory = new(); downloadHistory = new(); uploadHistory = new();
        CpuTrace.Update([]); GpuTrace.Update([]); FrameTrace.Update([]); DownloadTrace.Update([]); UploadTrace.Update([]);
        LowCaption.Text = "最近 60 秒 · 至少 100 帧";
        FrameScale.Text = "帧间隔 · ms"; NetworkScale.Text = "整机网络 · 共用刻度";
        displayedFrameGeneration = -1;
        TelemetryStatus.Text = "无有效样本";
        LastGameSnapshot = null;
    }
    private static string Number(double? value, string format = "0.0") => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";
    private static string Rate(double? value) => value.HasValue ? value >= 1048576 ? Number(value / 1048576) + " MiB/s" : Number(value / 1024) + " KiB/s" : "—";
    private static string State(TelemetrySourceState state) => state switch
    {
        TelemetrySourceState.Available => "正常", TelemetrySourceState.Stale => "过期", TelemetrySourceState.Failed => "失败",
        TelemetrySourceState.Stopped => "停止", TelemetrySourceState.Disabled => "未启用", _ => "等待"
    };

    // The headless test platform does not run dispatcher timers; tests drive the 1 Hz update directly.
    internal void TickGame() => OnGameTick(null, EventArgs.Empty);
    private void OnGameTick(object? sender, EventArgs e)
    {
        if (gameSession is null) return;
        GameMonitoringUpdateCount++;
        var snapshot = LastGameSnapshot = gameSession.Read();
        var remote = gameSession as RemoteGameSource;
        var sample = snapshot.System;
        var time = gameClock.Elapsed.TotalSeconds;
        var high = SamplingPicker.SelectedIndex == 1;
        var memory = sample is { TotalMemory: > 0 } && sample.AvailableMemory <= sample.TotalMemory
            ? (double?)(sample.TotalMemory - sample.AvailableMemory) / sample.TotalMemory * 100 : null;
        CpuValue.Text = Number(sample?.Cpu, "0") + (sample?.Cpu is not null ? "%" : "");
        CpuGauge.Value = sample?.Cpu ?? double.NaN;
        MemoryValue.Text = Number(memory, "0") + (memory.HasValue ? "%" : "");
        MemoryGauge.Value = memory ?? double.NaN;
        MemoryDetails.Text = memory.HasValue ? $"{Number((double)(sample!.TotalMemory - sample.AvailableMemory) / 1073741824)} / {Number((double)sample.TotalMemory / 1073741824)} GiB" : "— / —";
        var frames = snapshot.Frames;
        FpsValue.Text = Number(frames.Fps, "0");
        LowValue.Text = Number(frames.LowOnePercent, "0");
        FrameValue.Text = Number(frames.Milliseconds, "0.00");
        LowCaption.Text = snapshot.PerFrame == false ? "需要逐帧记录（log_interval=0）" : frames.Truncated ? "缓冲已满，1% Low 暂不可用" : $"最近 60 秒 · {frames.Count} 帧";
        DownloadValue.Text = "━  ↓ " + Rate(sample?.Download);
        UploadValue.Text = "┄  ↑ " + Rate(sample?.Upload);

        var readings = snapshot.Hardware;
        var choices = readings.Where(r => r.Identity.StartsWith("/gpu-", StringComparison.Ordinal))
            .GroupBy(r => string.Join('/', r.Identity.Split('/').Take(3))).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GpuChoice(g.Key, g.First().Metric.Split(" / ")[0])).ToArray();
        if (choices.Length > 0 && !(GameGpuPicker.ItemsSource as GpuChoice[] ?? []).SequenceEqual(choices))
        {
            var selected = (GameGpuPicker.SelectedItem as GpuChoice)?.Identity;
            GameGpuPicker.ItemsSource = choices;
            GameGpuPicker.SelectedItem = choices.FirstOrDefault(c => c.Identity == selected) ?? choices[0];
        }
        GameGpuPicker.IsVisible = (GameGpuPicker.ItemsSource as GpuChoice[])?.Length > 1;
        var gpuRoot = (GameGpuPicker.SelectedItem as GpuChoice)?.Identity;
        var gpuReadings = readings.Where(r => gpuRoot is not null && r.Identity.StartsWith(gpuRoot + "/", StringComparison.Ordinal)).ToArray();
        MonitoringReading? GpuReading(string name) => gpuReadings.FirstOrDefault(r => r.Identity == gpuRoot + "/" + name);
        var gpuUsage = GpuReading("load")?.Value;
        GpuGauge.Value = gpuUsage ?? double.NaN;
        GpuValue.Text = Number(gpuUsage, "0") + (gpuUsage.HasValue ? "%" : "");
        if (gpuReadings.Length == 0)
            GpuDetails.Text = snapshot.HardwareState == TelemetrySourceState.Available ? "找不到 GPU（/sys/class/drm）" : "GPU\n" + snapshot.HardwareStatus;
        else
        {
            var load = GpuReading("load");
            GpuDetails.Text = gpuReadings[0].Metric.Split(" / ")[0] + "\n" + Number(GpuReading("temperature")?.Value) + " °C · " + Number(GpuReading("clock")?.Value, "0") + " MHz\n" +
                Number(GpuReading("power")?.Value) + " W" + (load?.Reason is { } note ? "\n使用率：" + note : "");
        }
        ToolTip.SetTip(GpuDetails, string.Join("\n", gpuReadings.Where(r => !r.Value.HasValue && r.Reason is not null).Select(r => r.Metric.Split(" / ")[^1] + " " + r.Unit + "：" + r.Reason).Distinct()));
        var used = GpuReading("memory-used")?.Value;
        var total = GpuReading("memory-total")?.Value;
        var vram = total > 0 && used >= 0 && used <= total ? used / total * 100 : null;
        VramGauge.Value = vram ?? double.NaN;
        VramValue.Text = Number(vram, "0") + (vram.HasValue ? "%" : "");
        VramDetails.Text = vram.HasValue ? Number(used / 1024) + " / " + Number(total / 1024) + " GiB" : "— / —";
        VramCaption.Text = vram.HasValue ? "以上 GPU 的专用显存" : GpuReading("memory-used")?.Reason ?? (gpuRoot is null ? snapshot.HardwareStatus : "此 GPU 未提供显存数据");
        MonitoringReading? Cpu(string name) => readings.FirstOrDefault(r => r.Identity == "/cpu/0/" + name);
        CpuDetails.Text = readings.Any(r => r.Identity.StartsWith("/cpu/", StringComparison.Ordinal))
            ? "整机 CPU · 感测最高值\n" + Number(Cpu("temperature")?.Value) + " °C · " + Number(Cpu("clock")?.Value, "0") + " MHz\n" + Number(Cpu("power")?.Value) + " W"
            : "整机 CPU\n" + snapshot.HardwareStatus;
        ToolTip.SetTip(CpuDetails, Cpu("power") is { Value: null, Reason: { } reason } ? "功耗：" + reason : null);

        if (snapshot.FrameGeneration != displayedFrameGeneration) { displayedFrameGeneration = snapshot.FrameGeneration; frameHistory = new(); }
        cpuHistory.Add(time, sample?.Cpu); gpuHistory.Add(time, gpuUsage); frameHistory.Add(time, frames.Milliseconds);
        downloadHistory.Add(time, sample?.Download); uploadHistory.Add(time, sample?.Upload);
        CpuTrace.Maximum = GpuTrace.Maximum = 100;
        var framePoints = frameHistory.Snapshot();
        FrameTrace.Maximum = Math.Max(1, framePoints.Max(p => p.Value ?? 0) * 1.1);
        FrameScale.Text = $"0–{Number(FrameTrace.Maximum)} ms";
        CpuTrace.Update(cpuHistory.Snapshot()); GpuTrace.Update(gpuHistory.Snapshot()); FrameTrace.Update(framePoints);
        var down = downloadHistory.Snapshot(); var up = uploadHistory.Snapshot();
        DownloadTrace.Maximum = UploadTrace.Maximum = Math.Max(1, down.Concat(up).Max(p => p.Value ?? 0) * 1.1);
        NetworkScale.Text = "0–" + Rate(DownloadTrace.Maximum);
        DownloadTrace.Update(down); UploadTrace.Update(up);

        GameSourceState.Text = (remote is not null ? "REMOTE · " : "") + (snapshot.CpuState == TelemetrySourceState.Available && snapshot.MemoryState == TelemetrySourceState.Available
            ? "LIVE / " : snapshot.CpuState == TelemetrySourceState.Stale ? "STALE / " : "PARTIAL / ") + (high ? "HIGH" : "LOW");
        CompactSourceText.Text = (remote is not null ? "主机「" + remote.LastView.HelperName + "」· " : "") + (snapshot.CpuState is TelemetrySourceState.Failed or TelemetrySourceState.Stale ? snapshot.SystemStatus : snapshot.FrameStatus);
        GameNotice.Text = remote is not null
            ? remote.LastView.Status + "\n" + snapshot.FrameStatus + (frames.SwapChain is { } chain ? " · " + chain : "") + "\nCPU／内存／网络为主机的整机数据；FPS 为主机上应用程序的帧率（PresentMon）。"
            : (connectionNote is { } fallback ? fallback + "\n" : "") + snapshot.FrameStatus + "\nCPU／内存／网络为整机数据；FPS 为游戏本身的帧率（MangoHud 记录），非显示器刷新率。";
        TelemetryStatus.Text = (remote is not null
            ? $"{sample?.Time.ToLocalTime():HH:mm:ss} / 主机 · 基本 {(high ? "1" : "3")}s · GPU／硬件 {(high ? "2" : "10")}s · 传送 1 Hz\n{snapshot.HardwareStatus}"
            : $"{sample?.Time.ToLocalTime():HH:mm:ss} / 基本 {(high ? "1" : "3")}s · 硬件 {(high ? "2" : "10")}s · 帧记录 {(high ? "0.5" : "1")}s · UI 1 Hz\n{snapshot.HardwareStatus}") +
            $"\nCPU：{State(snapshot.CpuState)} · 内存：{State(snapshot.MemoryState)} · 网络：{State(snapshot.NetworkState)} · 硬件：{State(snapshot.HardwareState)} · 帧：{State(snapshot.FrameState)}";
        ToolTip.SetTip(CpuValue, snapshot.SystemStatus);
        ToolTip.SetTip(FpsValue, snapshot.FrameStatus);
        ToolTip.SetTip(FrameTrace, frames.Milliseconds is double interval ? $"目前平均帧间隔 {interval:0.00} ms；最近 60 秒曲线" : snapshot.FrameStatus);
        if (remote is { TargetExited: true } && remoteTarget is not null)
        { RemoteProcessPicker.Content = remoteTarget.Name + " · 已退出"; remoteTarget = null; }
    }

    // Moves the header and detail sections into expanders on short or narrow screens, as on Windows.
    private void ApplyGameLayout(double contentWidth, double height)
    {
        var narrow = contentWidth < 820;
        TelemetryLayout.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(300);
        TelemetryLayout.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(AnalysisPreview, narrow ? 0 : 1);
        Grid.SetRow(AnalysisPreview, narrow ? 1 : 0);
        HardwarePreview.Margin = new Thickness(0, 0, narrow ? 0 : 28, 0);
        var compact = contentWidth < 700 || height < 900;
        if (compact == CompactGameLayout) return;
        if (compact)
        {
            GamePage.Children.Remove(GameHeader); CompactGameOptions.Content = GameHeader;
            GamePage.Children.Remove(TelemetryLayout); CompactGameDetails.Content = TelemetryLayout;
        }
        else
        {
            CompactGameOptions.Content = null; GamePage.Children.Insert(0, GameHeader);
            CompactGameDetails.Content = null;
            GamePage.Children.Insert(GamePage.Children.IndexOf(MangoHudCard), TelemetryLayout);
        }
        CompactGameSummary.IsVisible = CompactGameOptions.IsVisible = CompactGameDetails.IsVisible = compact;
        FpsOverview.IsVisible = GameNotice.IsVisible = GameHeaderDivider.IsVisible = GameFooterDivider.IsVisible = TelemetryStatus.IsVisible = !compact;
        if (compact) CompactGameOptions.IsExpanded = CompactGameDetails.IsExpanded = false;
    }

    // About 1.2 seconds: a scan line sweeps down, then the dashboard fades and slides in. Nothing keeps running after it.
    public void StartGameTransition()
    {
        CancelTransition();
        if (model.Modes.Mode != AppMode.Game || settingsOpen || !IsVisible || WindowState == WindowState.Minimized || !model.Settings.GameAnimation || !AnimationsEnabled) return;
        var owner = transition = new CancellationTokenSource();
        GameTransition.IsVisible = true;
        _ = RunTransition(owner);
    }
    private async Task RunTransition(CancellationTokenSource owner)
    {
        static Animation Animate(AvaloniaProperty property, double from, double to, int milliseconds, int delay = 0, Easing? easing = null) => new()
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds), Delay = TimeSpan.FromMilliseconds(delay), FillMode = FillMode.Both, Easing = easing ?? new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } }
            }
        };
        // Transform animations run on the visual and drive its TranslateTransform.
        GamePage.RenderTransform = new TranslateTransform();
        try
        {
            await Task.WhenAll(
                Animate(TranslateTransform.YProperty, 0, Math.Max(600, ShellRoot.Bounds.Height), 850).RunAsync(ScanLine, owner.Token),
                Animate(OpacityProperty, 1, 0, 650, 550).RunAsync(GameTransition, owner.Token),
                Animate(TranslateTransform.YProperty, 20, 0, 650, 550, new CubicEaseOut()).RunAsync(GamePage, owner.Token),
                Animate(OpacityProperty, .3, 1, 650, 550).RunAsync(GamePage, owner.Token));
        }
        catch (Exception error) when (error is not OperationCanceledException) { AppDiagnostics.Write("game-transition", error); }
        finally { if (ReferenceEquals(transition, owner)) CancelTransition(); }
    }
    public void CancelTransition()
    {
        var previous = transition;
        transition = null;
        previous?.Cancel();
        GameTransition.IsVisible = false;
        GameTransition.Opacity = 1;
        GamePage.Opacity = 1;
        GamePage.RenderTransform = null;
        ScanTransform.Y = 0;
    }

    internal void DisposeGameTelemetry()
    {
        gameDisposed = true;
        CancelTransition();
        gameTimer?.Stop();
        var owner = gameCancellation;
        gameCancellation = null;
        owner?.Cancel();
        gameSession = null;
    }
}
