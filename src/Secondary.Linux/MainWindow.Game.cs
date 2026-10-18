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
        if (!remote && remoteTarget is not null) { remoteTarget = null; RemoteProcessPicker.Content = "選擇主電腦遊戲程序  ⌄"; }
        RemoteProcessPicker.IsVisible = RemoteSensorsToggle.IsVisible = remote;
        MangoHudCard.IsVisible = !remote;
        GameSourceCaption.Text = remote ? "主電腦「" + connection!.ConnectedName + "」/ PresentMon 幀率" : "整機監測 / MangoHud 幀率";
        var configuration = active ? $"{remote}:{SamplingPicker.SelectedIndex}:{TelemetryRoot}:{MangoHudFolder}:{RemoteSensorsToggle.IsChecked}:{remoteTarget?.Id}:{remoteTarget?.Started}" : null;
        if (configuration == gameConfiguration) return;
        gameConfiguration = configuration;
        var old = gameCancellation;
        gameCancellation = null;
        old?.Cancel();
        gameTimer?.Stop();
        gameSession = null;
        ClearGameReadings();
        if (!active) { connectionNote = null; GameSourceState.Text = "PAUSED"; GameNotice.Text = "監測已暫停；返回可見遊戲頁後重新建立採樣基準。"; return; }
        connectionNote = remote ? null : lastGameRemote ? "已與主電腦中斷連線（" + connection?.Status + "），改用這臺設備的資料。" : connectionNote;
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
        GameNotice.Text = remote ? $"正在等待主電腦「{connection!.ConnectedName}」的資料。" : (connectionNote is { } note ? note + "\n" : "") + "正在建立採樣基準；CPU、記憶體與網路為整機數據。";
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
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(gameSession, session)) GameNotice.Text = "監測工作已停止；切換頁面可重試。"; });
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = "無法建立記錄資料夾：" + error.Message; }
        var installed = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, "mangohud")));
        MangoHudStatus.Text = problem ?? (installed ? "已安裝 MangoHud · 記錄資料夾：" + MangoHudFolder
            : "找不到 mangohud 指令；請先安裝（Ubuntu：sudo apt install mangohud）。記錄資料夾：" + MangoHudFolder);
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
            var search = new TextBox { Width = 320, PlaceholderText = "搜尋名稱或 PID" };
            AutomationProperties.SetName(search, "搜尋主電腦程序名稱或 PID");
            var list = new ListBox { Width = 320, MaxHeight = 360 };
            AutomationProperties.SetName(list, "主電腦的程序");
            void Populate()
            {
                var text = search.Text ?? "";
                list.ItemsSource = new[] { new ProcessChoice(null, "不追蹤程序（只監測整機）") }.Concat(RemoteProcessChoices
                    .Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal))
                    .Take(80).Select(p => new ProcessChoice(p, p.Name + " · PID " + p.Id))).ToArray();
            }
            search.TextChanged += (_, _) => Populate();
            list.SelectionChanged += (_, _) => { if (list.SelectedItem is ProcessChoice choice) ChooseRemoteProcess(choice.Process); };
            Populate();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "主電腦「" + link.ConnectedName + "」的程序 · 最多顯示 80 筆", FontSize = 12 });
            panel.Children.Add(search);
            panel.Children.Add(list);
            remoteProcessMenu = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            remoteProcessMenu.ShowAt(RemoteProcessPicker);
            search.Focus();
        }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException) { GameNotice.Text = "無法取得主電腦的程序：" + error.Message; }
        finally { RemoteProcessPicker.IsEnabled = true; }
    }
    private sealed record ProcessChoice(RemoteProcess? Process, string Label) { public override string ToString() => Label; }
    public void ChooseRemoteProcess(RemoteProcess? process)
    {
        remoteProcessMenu?.Hide();
        remoteTarget = process;
        RemoteProcessPicker.Content = process is null ? "選擇主電腦遊戲程序  ⌄" : process.Name + " · " + process.Id;
        ConfigureGameTelemetry();
    }

    private async void OnCopyMangoHud(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is null || MangoHudOption.Text is not { } option) return;
        await Clipboard.SetTextAsync(option);
        MangoHudStatus.Text = "已複製啟動選項。貼到 Steam 遊戲的「內容 → 啟動選項」後開啟遊戲。";
    }

    private void ClearGameReadings()
    {
        FpsValue.Text = LowValue.Text = FrameValue.Text = CpuValue.Text = GpuValue.Text = MemoryValue.Text = VramValue.Text = "—";
        CpuGauge.Value = GpuGauge.Value = MemoryGauge.Value = VramGauge.Value = double.NaN;
        CpuDetails.Text = "整機 CPU · 等待基準";
        GpuDetails.Text = "GPU · 等待基準";
        MemoryDetails.Text = VramDetails.Text = "— / —";
        VramCaption.Text = "等待硬體採樣";
        DownloadValue.Text = "━  ↓ —"; UploadValue.Text = "┄  ↑ —";
        cpuHistory = new(); gpuHistory = new(); frameHistory = new(); downloadHistory = new(); uploadHistory = new();
        CpuTrace.Update([]); GpuTrace.Update([]); FrameTrace.Update([]); DownloadTrace.Update([]); UploadTrace.Update([]);
        LowCaption.Text = "最近 60 秒 · 至少 100 幀";
        FrameScale.Text = "幀間隔 · ms"; NetworkScale.Text = "整機網路 · 共用刻度";
        displayedFrameGeneration = -1;
        TelemetryStatus.Text = "無有效樣本";
        LastGameSnapshot = null;
    }
    private static string Number(double? value, string format = "0.0") => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";
    private static string Rate(double? value) => value.HasValue ? value >= 1048576 ? Number(value / 1048576) + " MiB/s" : Number(value / 1024) + " KiB/s" : "—";
    private static string State(TelemetrySourceState state) => state switch
    {
        TelemetrySourceState.Available => "正常", TelemetrySourceState.Stale => "過期", TelemetrySourceState.Failed => "失敗",
        TelemetrySourceState.Stopped => "停止", TelemetrySourceState.Disabled => "未啟用", _ => "等待"
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
        LowCaption.Text = snapshot.PerFrame == false ? "需要逐幀記錄（log_interval=0）" : frames.Truncated ? "緩衝已滿，1% Low 暫不可用" : $"最近 60 秒 · {frames.Count} 幀";
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
        VramCaption.Text = vram.HasValue ? "以上 GPU 的專用顯存" : GpuReading("memory-used")?.Reason ?? (gpuRoot is null ? snapshot.HardwareStatus : "此 GPU 未提供顯存資料");
        MonitoringReading? Cpu(string name) => readings.FirstOrDefault(r => r.Identity == "/cpu/0/" + name);
        CpuDetails.Text = readings.Any(r => r.Identity.StartsWith("/cpu/", StringComparison.Ordinal))
            ? "整機 CPU · 感測最高值\n" + Number(Cpu("temperature")?.Value) + " °C · " + Number(Cpu("clock")?.Value, "0") + " MHz\n" + Number(Cpu("power")?.Value) + " W"
            : "整機 CPU\n" + snapshot.HardwareStatus;
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
        CompactSourceText.Text = (remote is not null ? "主電腦「" + remote.LastView.HelperName + "」· " : "") + (snapshot.CpuState is TelemetrySourceState.Failed or TelemetrySourceState.Stale ? snapshot.SystemStatus : snapshot.FrameStatus);
        GameNotice.Text = remote is not null
            ? remote.LastView.Status + "\n" + snapshot.FrameStatus + (frames.SwapChain is { } chain ? " · " + chain : "") + "\nCPU／記憶體／網路為主電腦的整機數據；FPS 為主電腦上應用程式的幀率（PresentMon）。"
            : (connectionNote is { } fallback ? fallback + "\n" : "") + snapshot.FrameStatus + "\nCPU／記憶體／網路為整機數據；FPS 為遊戲本身的幀率（MangoHud 記錄），非顯示器刷新率。";
        TelemetryStatus.Text = (remote is not null
            ? $"{sample?.Time.ToLocalTime():HH:mm:ss} / 主電腦 · 基本 {(high ? "1" : "3")}s · GPU／硬體 {(high ? "2" : "10")}s · 傳送 1 Hz\n{snapshot.HardwareStatus}"
            : $"{sample?.Time.ToLocalTime():HH:mm:ss} / 基本 {(high ? "1" : "3")}s · 硬體 {(high ? "2" : "10")}s · 幀記錄 {(high ? "0.5" : "1")}s · UI 1 Hz\n{snapshot.HardwareStatus}") +
            $"\nCPU：{State(snapshot.CpuState)} · 記憶體：{State(snapshot.MemoryState)} · 網路：{State(snapshot.NetworkState)} · 硬體：{State(snapshot.HardwareState)} · 幀：{State(snapshot.FrameState)}";
        ToolTip.SetTip(CpuValue, snapshot.SystemStatus);
        ToolTip.SetTip(FpsValue, snapshot.FrameStatus);
        ToolTip.SetTip(FrameTrace, frames.Milliseconds is double interval ? $"目前平均幀間隔 {interval:0.00} ms；最近 60 秒曲線" : snapshot.FrameStatus);
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
