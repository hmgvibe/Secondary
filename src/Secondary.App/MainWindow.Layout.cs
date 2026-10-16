using System.Windows;

namespace Secondary.App;

public partial class MainWindow
{
    private double shellHeight = 820;
    public bool CompactGameLayout => CompactGameSummary.Visibility == Visibility.Visible;
    private void ApplyGameLayout(bool compact)
    {
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
            GamePage.Children.Insert(GamePage.Children.IndexOf(GameFooterDivider), TelemetryLayout);
        }
        CompactGameSummary.Visibility = CompactGameOptions.Visibility = CompactGameDetails.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        FpsOverview.Visibility = GameNotice.Visibility = GameHeaderDivider.Visibility = GameFooterDivider.Visibility = TelemetryStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact) { CompactGameOptions.IsExpanded = false; CompactGameDetails.IsExpanded = false; }
    }
    private void OnRetrySaveSettings(object sender, RoutedEventArgs e)
    {
        if (model.RetrySaveSettings()) model.Status = "設定已重新儲存。";
    }
}
