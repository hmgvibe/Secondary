using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Secondary.App;
using Secondary.App.Monitoring;
using Secondary.Core;

internal static class RootOptimizationChecks
{
    public static void Run(MainWindow window, ShellViewModel model, Action<bool,string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "Secondary.Measure-parse-" + Guid.NewGuid().ToString("N") + ".json");
        var request = PerformanceRun.Parse(["--measure",path,"GameHigh","--seconds=5","--warmup=0"]);
        check(request.Scene == "GameHigh" && request.Seconds == 5 && request.WarmupSeconds == 0, "Measurement accepts current isolated scenes and bounded durations");
        foreach (var args in new[] { new[] { "--measure",path,"Full" }, new[] { "--measure",path,"Home","--seconds=0" }, new[] { "--measure",path,"Video" }, new[] { "--measure",path,"Home","--warmup=1","--warmup=2" } })
        {
            var rejected = false; try { PerformanceRun.Parse(args); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Measurement rejects invalid scenes, durations and duplicate arguments");
        }
        var original = model.Settings;
        var root = (FrameworkElement)window.Content;
        window.SetAppearance(Secondary.App.Presentation.ShellAppearance.Normal);
        foreach (var position in new[] { "Top", "Bottom", "Left", "Right" })
        {
            model.SaveRuntime(navigationSize:"Small",navigationPosition:position);
            window.ApplyResponsiveLayout(560,480);
            root.Measure(new Size(560,480)); root.Arrange(new Rect(0,0,560,480)); root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => {}, DispatcherPriority.ApplicationIdle);
            var item = ((StackPanel)window.FindName("NavigationItems")).Children.OfType<RadioButton>().First();
            var bounds = item.TransformToAncestor(root).TransformBounds(new Rect(0,0,item.ActualWidth,item.ActualHeight));
            check(bounds.Width >= 43.9 && bounds.Height >= 43.9, "Small navigation retains a 44 DIP transformed operation target on every edge");
        }
        model.SaveRuntime(navigationSize:original.NavigationSize,navigationPosition:original.NavigationPosition);
        window.ApplyResponsiveLayout(1200,820);
    }
}
