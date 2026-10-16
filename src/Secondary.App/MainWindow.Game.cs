using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Secondary.App.Monitoring;
using Secondary.Core;

namespace Secondary.App;

public partial class MainWindow
{
    private CancellationTokenSource? gameCancellation;
    private Task gameCompletion = Task.CompletedTask;
    private GameTelemetrySession? gameSession;
    private GameTarget? gameTarget;
    private string? gameConfiguration;
    private DispatcherTimer? gameTimer;
    private bool gameDisposed;
    public bool GameMonitoringActive => gameSession is not null;
    public Task GameMonitoringCompletion => gameCompletion;
    public long GameMonitoringUpdateCount { get; private set; }
    private readonly Stopwatch gameClock = Stopwatch.StartNew();
    private sealed record GpuChoice(string Identity, string Name) { public override string ToString() => Name; }
    private string? displayedSwapChain;
    private void OnGameGpuChanged(object sender, SelectionChangedEventArgs e) => gpuHistory = new();
    private TelemetryHistory cpuHistory = new(), gpuHistory = new(), frameHistory = new(), downloadHistory = new(), uploadHistory = new();

    private void OnGameSamplingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        model.GameSamplingIndex = SamplingPicker.SelectedIndex;
        ConfigureGameTelemetry();
    }
    private void OnGameSensorsChanged(object sender, RoutedEventArgs e) { if (!initializing) ConfigureGameTelemetry(); }

    private void ConfigureGameTelemetry()
    {
        if (initializing || gameDisposed || GameSensorsToggle is null) return;
        var active = IsLoaded && IsVisible && WindowState != WindowState.Minimized && CurrentPage == "game" && !sessionLocked && !suspended;
        var configuration = active ? $"{SamplingPicker.SelectedIndex}:{GameSensorsToggle.IsChecked}:{gameTarget?.Id}:{gameTarget?.StartedUtcTicks}" : null;
        if (configuration == gameConfiguration) return;
        gameConfiguration = configuration;
        var old = gameCancellation;
        gameCancellation = null;
        old?.Cancel();
        if (old is not null) _ = gameCompletion.ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        gameTimer?.Stop();
        gameSession = null;
        ClearGameReadings();
        if (!active) { GameSourceState.Text = "PAUSED"; GameNotice.Text = "監測已暫停；返回可見遊戲頁後重新建立採樣基準。"; return; }
        var previous = gameCompletion;
        var owner = new CancellationTokenSource();
        gameCancellation = owner;
        var session = new GameTelemetrySession(SamplingPicker.SelectedIndex == 1, GameSensorsToggle.IsChecked == true, gameTarget);
        gameSession = session;
        GameSourceState.Text = "LIVE / WARMUP";
        GameNotice.Text = "正在建立採樣基準；CPU、記憶體與網路為整機數據。";
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
                if (!owner.IsCancellationRequested && !Dispatcher.HasShutdownStarted)
                    await Dispatcher.InvokeAsync(() => { if (ReferenceEquals(gameSession, session)) GameNotice.Text = "監測工作已停止；切換頁面可重試。"; });
            }
        });
        // Dispose a previous generation only after it has stopped touching its token.
        gameTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        gameTimer.Tick -= OnGameTick;
        gameTimer.Tick += OnGameTick;
        gameTimer.Start();
    }

    private void ClearGameReadings()
    {
        FpsValue.Text = LowValue.Text = FrameValue.Text = CpuValue.Text = GpuValue.Text = MemoryValue.Text = VramValue.Text = "—";
        CpuGauge.Value = GpuGauge.Value = MemoryGauge.Value = VramGauge.Value = double.NaN;
        CpuDetails.Text = "整機 CPU · 等待基準";
        GpuDetails.Text = "所有 GPU 最忙引擎 · 等待基準";
        MemoryDetails.Text = VramDetails.Text = "— / —";
        VramCaption.Text = "需手動啟用硬體感測";
        DownloadValue.Text = "━  ↓ —"; UploadValue.Text = "┄  ↑ —";
        cpuHistory = new(); gpuHistory = new(); frameHistory = new(); downloadHistory = new(); uploadHistory = new();
        CpuTrace.Update([]); GpuTrace.Update([]); FrameTrace.Update([]); DownloadTrace.Update([]); UploadTrace.Update([]);
        LowCaption.Text = "最近 60 秒 · 至少 100 幀";
        FrameScale.Text = "幀間隔 · ms"; NetworkScale.Text = "整機網路 · 共用刻度";
        displayedSwapChain = null;
        TelemetryStatus.Text = "無有效樣本";
    }
    private static string Number(double? value, string format = "0.0") => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";
    private static string Rate(double? value) => value.HasValue ? value >= 1048576 ? Number(value / 1048576) + " MiB/s" : Number(value / 1024) + " KiB/s" : "—";
    private void OnGameTick(object? sender, EventArgs e)
    {
        if (gameSession is null) return;
        GameMonitoringUpdateCount++;
        var snapshot = gameSession.Read();
        var sample = snapshot.System;
        var time = gameClock.Elapsed.TotalSeconds;
        var memory = sample is { TotalMemory: > 0 } && sample.AvailableMemory <= sample.TotalMemory
            ? (double?)(sample.TotalMemory - sample.AvailableMemory) / sample.TotalMemory * 100 : null;
        CpuValue.Text = Number(sample?.Cpu, "0") + (sample?.Cpu is not null ? "%" : "");
        CpuGauge.Value = sample?.Cpu ?? double.NaN;
        MemoryValue.Text = Number(memory, "0") + (memory.HasValue ? "%" : "");
        MemoryGauge.Value = memory ?? double.NaN;
        MemoryDetails.Text = memory.HasValue ? $"{Number((double)(sample!.TotalMemory - sample.AvailableMemory) / 1073741824)} / {Number((double)sample.TotalMemory / 1073741824)} GiB" : "— / —";
        FpsValue.Text = Number(snapshot.Frames.Fps, "0");
        LowValue.Text = Number(snapshot.Frames.LowOnePercent, "0");
        FrameValue.Text = Number(snapshot.Frames.Milliseconds, "0.00");
        LowCaption.Text = snapshot.Frames.Truncated ? "緩衝已滿，1% Low 暫不可用" : $"最近 60 秒 · {snapshot.Frames.Count} 幀";
        DownloadValue.Text = "━  ↓ " + Rate(sample?.Download);
        UploadValue.Text = "┄  ↑ " + Rate(sample?.Upload);
        var readings = snapshot.Sensors?.Readings ?? [];
        var choices = readings.Where(r => r.Identity.StartsWith("/gpu-", StringComparison.Ordinal))
            .GroupBy(r => string.Join('/', r.Identity.Split('/').Take(3))).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GpuChoice(g.Key, g.First().Metric.Split(" / ")[0])).ToArray();
        if (!GameGpuPicker.Items.Cast<GpuChoice>().SequenceEqual(choices))
        {
            var selected = (GameGpuPicker.SelectedItem as GpuChoice)?.Identity;
            GameGpuPicker.ItemsSource = choices;
            GameGpuPicker.SelectedItem = choices.FirstOrDefault(c => c.Identity == selected) ?? choices.FirstOrDefault();
        }
        GameGpuPicker.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        var gpuRoot = (GameGpuPicker.SelectedItem as GpuChoice)?.Identity;
        var gpuReadings = readings.Where(r => gpuRoot is not null && r.Identity.StartsWith(gpuRoot + "/", StringComparison.Ordinal)).ToArray();
        double? GpuMetric(string name, string unit) => gpuReadings.FirstOrDefault(r => r.Unit == unit && r.Metric.EndsWith(" / " + name, StringComparison.Ordinal))?.Value;
        var gpuUsage = gpuRoot is not null ? GpuMetric("GPU Core", "%") : snapshot.Gpu;
        GpuGauge.Value = gpuUsage ?? double.NaN;
        GpuValue.Text = Number(gpuUsage, "0") + (gpuUsage.HasValue ? "%" : "");
        if (gpuRoot is null) GpuDetails.Text = "所有 GPU 中最忙引擎（整機）\n" + snapshot.GpuStatus;
        else GpuDetails.Text = gpuReadings[0].Metric.Split(" / ")[0] + "\n" + Number(GpuMetric("GPU Core", "°C")) + " °C · " + Number(GpuMetric("GPU Core", "MHz"), "0") + " MHz\n" + Number(GpuMetric("GPU Package", "W")) + " W";
        GpuDetails.ToolTip = gpuRoot;
        var used = GpuMetric("GPU Memory Used", "MB");
        var total = GpuMetric("GPU Memory Total", "MB");
        var vram = total > 0 && used >= 0 && used <= total ? used / total * 100 : null;
        VramGauge.Value = vram ?? double.NaN;
        VramValue.Text = Number(vram, "0") + (vram.HasValue ? "%" : "");
        VramDetails.Text = vram.HasValue ? Number(used / 1024) + " / " + Number(total / 1024) + " GiB" : "— / —";
        VramCaption.Text = vram.HasValue ? "以上 GPU 的專用顯存" : gpuRoot is null ? snapshot.SensorStatus : "此 GPU 未提供可辨識的專用顯存資料";
        var cpu = readings.Where(r => r.Identity.StartsWith("/intelcpu/", StringComparison.Ordinal) || r.Identity.StartsWith("/amdcpu/", StringComparison.Ordinal)).ToArray();
        double? CpuMax(string unit) => cpu.Where(r => r.Unit == unit).Select(r => r.Value).Max();
        CpuDetails.Text = cpu.Length > 0 ? "整機 CPU · 感測最高值\n" + Number(CpuMax("°C")) + " °C · " + Number(CpuMax("MHz"), "0") + " MHz\n" + Number(CpuMax("W")) + " W" :
            "整機 CPU\n" + (readings.FirstOrDefault(r => r.Identity == "cpu/pawnio")?.Reason ?? snapshot.SensorStatus);
        if (snapshot.Frames.SwapChain is { } activeChain && activeChain != displayedSwapChain)
        { displayedSwapChain = activeChain; frameHistory = new(); }
        cpuHistory.Add(time, sample?.Cpu); gpuHistory.Add(time, gpuUsage); frameHistory.Add(time, snapshot.Frames.Milliseconds);
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
        static string State(TelemetrySourceState state) => state switch { TelemetrySourceState.Available => "正常", TelemetrySourceState.Stale => "過期", TelemetrySourceState.Failed => "失敗", TelemetrySourceState.Stopped => "停止", TelemetrySourceState.Disabled => "未啟用", _ => "等待" };
        GameSourceState.Text = (snapshot.CpuState == TelemetrySourceState.Available && snapshot.MemoryState == TelemetrySourceState.Available
            ? "LIVE / " : snapshot.SystemState == TelemetrySourceState.Stale ? "STALE / " : "PARTIAL / ") + (SamplingPicker.SelectedIndex == 1 ? "HIGH" : "LOW");
        CompactSourceText.Text = snapshot.SystemState is TelemetrySourceState.Failed or TelemetrySourceState.Stale ? snapshot.SystemStatus : snapshot.FrameStatus;
        GameNotice.Text = snapshot.FrameStatus + (snapshot.Frames.SwapChain is { } chain ? " · " + chain : "") + "\nCPU／記憶體／網路為整機數據；FPS 為應用程式幀率，非顯示器刷新率。";
        TelemetryStatus.Text = $"{sample?.Time.ToLocalTime():HH:mm:ss} / 基本 {(SamplingPicker.SelectedIndex == 1 ? "1" : "3")}s · GPU／硬體 {(SamplingPicker.SelectedIndex == 1 ? "2" : "10")}s · UI 1 Hz\n{snapshot.SensorStatus}";
        TelemetryStatus.Text += $"\nCPU：{State(snapshot.CpuState)} · 記憶體：{State(snapshot.MemoryState)} · 網路：{State(snapshot.NetworkState)} · GPU：{State(snapshot.GpuState)} · 幀：{State(snapshot.FrameState)}";
        CpuValue.ToolTip = snapshot.SystemStatus;
        GpuValue.ToolTip = snapshot.GpuStatus + "\n" + snapshot.SensorStatus;
        FpsValue.ToolTip = snapshot.FrameStatus;
        FrameTrace.ToolTip = snapshot.Frames.Milliseconds is double interval ? $"目前平均幀間隔 {interval:0.00} ms；最近 60 秒曲線" : snapshot.FrameStatus;
        if (snapshot.TargetExited && gameTarget is not null)
        { GameProcessPicker.Content = gameTarget.Name + " · 已退出"; gameTarget = null; }
    }

    private async void OnPickGameProcess(object sender, RoutedEventArgs e)
    {
        GameProcessPicker.IsEnabled = false;
        try
        {
            var targets = await Task.Run(() => Process.GetProcesses().Select(process =>
            {
                using (process)
                {
                    try { return !process.HasExited && process.Id != Environment.ProcessId
                        ? new GameTarget(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName) : null; }
                    catch (Exception) { return null; }
                }
            }).Where(p => p is not null).Cast<GameTarget>().OrderBy(p => p.Name).ThenBy(p => p.Id).Take(1000).ToArray());
            if (CurrentPage != "game" || !IsVisible || gameDisposed) return;
            var menu = new ContextMenu { PlacementTarget = GameProcessPicker };
            menu.Resources = Resources;
            var search = new TextBox { Width = 260, MinHeight = 38, Padding = new Thickness(8) };
            System.Windows.Automation.AutomationProperties.SetName(search, "搜尋遊戲程序名稱或 PID");
            var searchPanel = new StackPanel();
            searchPanel.Children.Add(new TextBlock { Text = "搜尋名稱 / PID · 最多顯示 80 筆", FontSize = 12, Margin = new Thickness(0,0,0,6) });
            searchPanel.Children.Add(search);
            menu.Items.Add(new MenuItem { Header = searchPanel, StaysOpenOnClick = true, Focusable = false });
            var none = new MenuItem { Header = "不追蹤程序（只監測整機）" };
            none.Click += (_, _) => { gameTarget = null; GameProcessPicker.Content = "選擇遊戲程序  ⌄"; gameConfiguration = "restart"; ConfigureGameTelemetry(); };
            menu.Items.Add(none);
            void Populate()
            {
                while (menu.Items.Count > 2) menu.Items.RemoveAt(2);
                foreach (var target in targets.Where(t => t.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || t.Id.ToString().Contains(search.Text, StringComparison.Ordinal)).Take(80))
                {
                    var item = new MenuItem { Header = target.Name + " · PID " + target.Id };
                    item.Click += (_, _) => { gameTarget = target; GameProcessPicker.Content = target.Name + " · " + target.Id; gameConfiguration = "restart"; ConfigureGameTelemetry(); };
                    menu.Items.Add(item);
                }
                if (menu.Items.Count == 2) menu.Items.Add(new MenuItem { Header = "沒有符合條件且可讀取的程序", IsEnabled = false });
            }
            search.TextChanged += (_, _) => Populate();
            Populate();
            GameProcessPicker.ContextMenu = menu;
            menu.IsOpen = true;
            search.Focus();
        }
        catch (Exception error) { GameNotice.Text = "無法列舉程序：" + error.Message; }
        finally { GameProcessPicker.IsEnabled = true; }
    }
    internal void DisposeGameTelemetry()
    {
        gameDisposed = true;
        gameTimer?.Stop();
        var owner = gameCancellation;
        gameCancellation = null;
        owner?.Cancel();
        if (owner is not null) _ = gameCompletion.ContinueWith(_ => owner.Dispose(), TaskScheduler.Default);
    }
}
