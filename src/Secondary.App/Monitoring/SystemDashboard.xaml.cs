using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Secondary.App.Monitoring;

public partial class SystemDashboard : UserControl
{
    public static readonly DependencyProperty IsGameProperty = DependencyProperty.Register(nameof(IsGame), typeof(bool), typeof(SystemDashboard), new PropertyMetadata(false));
    public bool IsGame { get => (bool)GetValue(IsGameProperty); private set => SetValue(IsGameProperty, value); }
    public SystemDashboard() { InitializeComponent(); SetGame(false); }
    public void SetGame(bool game)
    {
        IsGame = game;
        Metrics.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(game ? "SystemDashboard.Cards" : "SystemDashboard.FullCards"));
        GameSamplingOptions.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
    }
}
