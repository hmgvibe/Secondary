using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using Secondary.App.Monitoring;
using Secondary.Core;

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
        SensorStatus.Text = "正在以一般權限讀取硬體…完成後助手自動退出。";
        SensorResults.Text = "";
        try
        {
            await GameMonitoringCompletion;
            capture.Token.ThrowIfCancellationRequested();
            var snapshot = await SensorHelperClient.ReadAsync(capture.Token);
            capture.Token.ThrowIfCancellationRequested();
            var available = snapshot.Readings.Count(r => r.State == ReadingState.Available);
            SensorStatus.Text = $"助手已停止 · {available} 項有效讀數 · 僅本次快照，不持續採樣";
            SensorResults.Text = string.Join("\n\n", snapshot.Readings.Select(r =>
                r.Metric + "\n" + (r.Value is double value ? value.ToString("0.##", CultureInfo.CurrentCulture) + " " + r.Unit : "— " + r.Reason) +
                "\n" + r.Identity + " · " + r.Time.ToLocalTime().ToString("HH:mm:ss")));
        }
        catch (OperationCanceledException) { SensorStatus.Text = "已停止；再次讀取需要手動啟用。"; }
        catch (Exception error) { SensorStatus.Text = "無法取得感測資料：" + error.Message; }
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
