using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using Secondary.App.Monitoring;
using Secondary.Core;
using Secondary.Monitoring;

namespace Secondary.App;

public partial class MainWindow
{
    private CancellationTokenSource? sensorCapture;
    private async void OnReadSensors(object sender, RoutedEventArgs e)
    {
        if (sensorCapture is not null || CurrentPage != "settings" || !IsVisible || sessionLocked || suspended) return;
        using var capture = new CancellationTokenSource();
        sensorCapture = capture;
        SensorStart.IsEnabled = false;
        SensorStop.IsEnabled = true;
        SensorStatus.Text = "正在以一般权限读取硬件…完成后助手自动退出。";
        SensorResults.Text = "";
        try
        {
            await GameMonitoringCompletion;
            capture.Token.ThrowIfCancellationRequested();
            var snapshot = await SensorHelperClient.ReadAsync(capture.Token);
            capture.Token.ThrowIfCancellationRequested();
            var available = snapshot.Readings.Count(r => r.State == ReadingState.Available);
            SensorStatus.Text = $"助手已停止 · {available} 项有效读数 · 仅本次快照，不持续采样";
            SensorResults.Text = string.Join("\n\n", snapshot.Readings.Select(r =>
                r.Metric + "\n" + (r.Value is double value ? value.ToString("0.##", CultureInfo.CurrentCulture) + " " + r.Unit : "— " + r.Reason) +
                "\n" + r.Identity + " · " + r.Time.ToLocalTime().ToString("HH:mm:ss")));
        }
        catch (OperationCanceledException) { SensorStatus.Text = "已停止；再次读取需要手动启用。"; }
        catch (Exception error) { SensorStatus.Text = "无法取得感测数据：" + error.Message; }
        finally
        {
            sensorCapture = null;
            SensorStart.IsEnabled = true;
            SensorStop.IsEnabled = false;
        }
    }
    private void OnStopSensors(object sender, RoutedEventArgs e) => StopSensorCapture();
    private void StopSensorCapture() => sensorCapture?.Cancel();
}
