using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Secondary.Core;

namespace Secondary.App.Presentation;

// One wall-clock timer, scheduled at the next displayed time boundary. No frame loop.
public sealed class HomeClock : FrameworkElement, IDisposable
{
    private const double DefaultScale = .7;
    private const double FlipDigitWidth = 140, FlipDigitHeight = 190, FlipDigitGap = 8, FlipGroupGap = 28;
    private static readonly CultureInfo DateCulture = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly Brush FlipFace = FrozenBrush(Color.FromRgb(24, 30, 35));
    private static readonly Brush FlipBottomFace = FrozenBrush(Color.FromRgb(20, 26, 31));
    private static readonly Brush FoldShade = CreateFoldShade();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly Dictionary<(string Font, double Size, double Dpi), TextMetrics> metrics = new();
    private readonly Dictionary<(string Text, string Font, double Size, double Dpi, Brush Ink), FormattedText> textCache = new();
    private HomeSettings options = new();
    private DateTime now = DateTime.Now;
    private string previous = "";
    private Brush? customColor;
    private ClockLayout? layout;
    private bool motionAllowed = true;
    private int flipGeneration;

    private sealed record TextMetrics(double DigitWidth, Rect InkBounds);
    private sealed record ClockLayout(Size Size, double TimeWidth, double TimeHeight, double TimeTop,
        double MeridiemTop, double DateTop, double DigitWidth, double SeparatorWidth);

    public bool IsRunning => timer.IsEnabled;
    public bool MotionAllowed
    {
        get => motionAllowed;
        set { motionAllowed = value; if (!value) StopFlip(); }
    }
    public bool FitWidget { get; set; }
    public HomeSettings Options => options;
    // Format and font metrics determine these bounds; changing time/date does not.
    // The same layout drives the frame and drawing, with the existing saved scales.
    public Size NaturalSize => new(DesignSize.Width * DefaultScale, DesignSize.Height * DefaultScale);
    public Size DesignSize => GetLayout().Size;
    public event Action<HomeSettings>? ConfigurationChanged;
    public string DisplayedTime => options.TimeText(now);
    public static readonly DependencyProperty AutomaticBrushProperty = DependencyProperty.Register(nameof(AutomaticBrush), typeof(Brush), typeof(HomeClock), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush AutomaticBrush { get => (Brush)GetValue(AutomaticBrushProperty); set => SetValue(AutomaticBrushProperty, value); }
    public static readonly DependencyProperty FlipProgressProperty = DependencyProperty.Register(nameof(FlipProgress), typeof(double), typeof(HomeClock), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double FlipProgress { get => (double)GetValue(FlipProgressProperty); set => SetValue(FlipProgressProperty, value); }
    private bool CanAnimate => MotionAllowed && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public HomeClock()
    {
        timer.Tick += OnTick; ClipToBounds = true; SetResourceReference(AutomaticBrushProperty, "TextBrush");
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopFlip(); };
        Unloaded += (_, _) => SetActive(false);
    }
    private static Brush FrozenBrush(Color value) { var brush = new SolidColorBrush(value); brush.Freeze(); return brush; }
    private static Brush CreateFoldShade()
    {
        var brush = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(170, 0, 0, 0), 90);
        brush.Freeze(); return brush;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ClockPeer(this);
    private sealed class ClockPeer(HomeClock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => "HomeClock";
    }
    public void Configure(HomeSettings value)
    {
        options = value.Normalize(); previous = ""; layout = null;
        customColor = options.Color == "Auto" ? null : (Brush)new BrushConverter().ConvertFromString(options.Color)!;
        if (customColor?.CanFreeze == true) customColor.Freeze();
        textCache.Clear(); StopFlip();
        UpdateTime(DateTime.Now, false); InvalidateMeasure(); InvalidateVisual();
        if (timer.IsEnabled) { timer.Stop(); Schedule(); }
        ConfigurationChanged?.Invoke(options);
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        layout = null; metrics.Clear(); textCache.Clear(); InvalidateMeasure(); InvalidateVisual();
        ConfigurationChanged?.Invoke(options);
    }
    public void SetActive(bool active)
    {
        if (!active) { timer.Stop(); StopFlip(); return; }
        if (timer.IsEnabled) return;
        UpdateTime(DateTime.Now, false); Schedule();
    }
    private void StopFlip()
    {
        flipGeneration++;
        BeginAnimation(FlipProgressProperty, null); FlipProgress = 1;
    }
    private void Schedule()
    {
        var current = DateTime.Now;
        timer.Interval = TimeSpan.FromMilliseconds(options.ShowSeconds ? 1000 - current.Millisecond : (60 - current.Second) * 1000 - current.Millisecond);
        timer.Start();
    }
    private void OnTick(object? sender, EventArgs e) { UpdateTime(DateTime.Now, true); Schedule(); }
    public void UpdateTime(DateTime value, bool animate = false)
    {
        var old = DisplayedTime;
        StopFlip(); now = value; previous = old;
        if (animate && old != DisplayedTime && options.ClockStyle == "Flip" && CanAnimate)
        {
            var generation = flipGeneration;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (generation == flipGeneration) StopFlip(); };
            BeginAnimation(FlipProgressProperty, animation);
        }
        AutomationProperties.SetName(this, value.ToString("yyyy年M月d日 ") + DisplayedTime + (!options.Use24Hours ? (value.Hour < 12 ? " 上午" : " 下午") : ""));
        InvalidateVisual();
    }
    private FormattedText Formatted(string text, double size, Brush ink, string font = "Bahnschrift")
    {
        var key = (text, font, size, VisualTreeHelper.GetDpi(this).PixelsPerDip, ink);
        if (!textCache.TryGetValue(key, out var formatted))
        {
            if (textCache.Count > 256) textCache.Clear();
            formatted = new FormattedText(text, DateCulture, FlowDirection.LeftToRight, new Typeface(font), size, ink, key.Item4);
            textCache[key] = formatted;
        }
        return formatted;
    }
    private TextMetrics DigitMetrics(string font, double size)
    {
        var key = (font, size, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (metrics.TryGetValue(key, out var result)) return result;
        double advance = 0; var bounds = Rect.Empty;
        for (var digit = 0; digit <= 9; digit++)
        {
            var text = Formatted(digit.ToString(CultureInfo.InvariantCulture), size, Brushes.White, font);
            advance = Math.Max(advance, text.WidthIncludingTrailingWhitespace);
            bounds.Union(text.BuildGeometry(new Point()).Bounds);
        }
        result = new TextMetrics(advance, bounds); metrics[key] = result; return result;
    }
    private double DateWidth()
    {
        // Independently reserve the longest month/day/weekday so midnight does not
        // move the frame. Small labels retain a stable readable font.
        var month = Enumerable.Range(1, 12).Max(n => Formatted(n + " 月 ", 22, Brushes.White).WidthIncludingTrailingWhitespace);
        var day = Enumerable.Range(1, 31).Max(n => Formatted(n + " 日 · ", 22, Brushes.White).WidthIncludingTrailingWhitespace);
        var weekday = DateCulture.DateTimeFormat.DayNames.Max(n => Formatted(n, 22, Brushes.White).WidthIncludingTrailingWhitespace);
        return month + day + weekday;
    }
    private ClockLayout GetLayout()
    {
        if (layout is not null) return layout;
        var analog = options.ClockStyle == "Analog";
        var flip = options.ClockStyle == "Flip";
        var digits = options.ShowSeconds ? 6 : 4;
        var timeMetrics = DigitMetrics(flip ? "Bahnschrift" : "Segoe UI Light", flip ? 135 : 170);
        var separator = 170 * .28;
        var timeWidth = analog ? 360 : flip
            ? digits * FlipDigitWidth + digits / 2 * FlipDigitGap + (digits / 2 - 1) * FlipGroupGap
            : digits * timeMetrics.DigitWidth + (digits / 2 - 1) * separator;
        var timeHeight = analog ? 360 : flip ? FlipDigitHeight : timeMetrics.InkBounds.Height;
        var timeTop = analog ? 0 : flip ? (FlipDigitHeight - timeMetrics.InkBounds.Height) / 2 - timeMetrics.InkBounds.Top : -timeMetrics.InkBounds.Top;
        var bottom = timeHeight;
        var meridiemTop = -1d;
        if (!options.Use24Hours)
        {
            if (analog) meridiemTop = 256;
            else { meridiemTop = bottom + 10; bottom = meridiemTop + Formatted("AM", 18, Brushes.White).Height; }
        }
        var dateTop = -1d; var width = timeWidth;
        if (options.ShowDate)
        {
            width = Math.Max(width, DateWidth()); dateTop = bottom + 14;
            bottom = dateTop + Formatted("星期一", 22, Brushes.White).Height;
        }
        // A small ink margin prevents clipping; HomeWidget supplies the larger
        // 12 DIP interaction margin around the content.
        layout = new ClockLayout(new Size(Math.Ceiling(width) + 4, Math.Ceiling(bottom) + 4), timeWidth, timeHeight,
            timeTop, meridiemTop, dateTop, timeMetrics.DigitWidth, separator);
        return layout;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var geometry = GetLayout(); var w = geometry.Size.Width; var h = geometry.Size.Height;
        var scale = Math.Min(Math.Min(ActualWidth / w, ActualHeight / h), FitWidget ? double.MaxValue : DefaultScale * options.Size);
        var x = (ActualWidth - w * scale) * (FitWidget ? .5 : options.X);
        var y = (ActualHeight - h * scale) * (FitWidget ? .5 : options.Y);
        dc.PushTransform(new TranslateTransform(x, y)); dc.PushTransform(new ScaleTransform(scale, scale));
        var color = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : customColor ??
            (options.Background == "Static" ? AutomaticBrush : Brushes.White);
        void Text(string text, double size, double cx, double top, Brush? brush = null, string font = "Bahnschrift")
        {
            var formatted = Formatted(text, size, brush ?? color, font);
            dc.DrawText(formatted, new Point(cx - formatted.Width / 2, top));
        }
        if (options.ClockStyle == "Analog")
        {
            var cx = w / 2; const double cy = 182; var center = new Point(cx, cy);
            dc.PushOpacity(.35); dc.DrawEllipse(null, new Pen(color, 1), center, 178, 178); dc.Pop();
            for (var i = 0; i < 60; i++)
            {
                var a = i * Math.PI / 30; var r = i % 5 == 0 ? 148 : 159;
                dc.DrawLine(new Pen(color, i % 5 == 0 ? 3 : 1), new(cx + Math.Sin(a) * r, cy - Math.Cos(a) * r), new(cx + Math.Sin(a) * 166, cy - Math.Cos(a) * 166));
            }
            void Hand(double units, double length, double thickness)
            {
                var angle = units * Math.PI / 30;
                dc.DrawLine(new Pen(color, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, new(cx + Math.Sin(angle) * length, cy - Math.Cos(angle) * length));
            }
            Hand(now.Hour % 12 * 5 + now.Minute / 12d, 89, 7); Hand(now.Minute, 127, 4);
            if (options.ShowSeconds) { dc.PushOpacity(.6); Hand(now.Second, 145, 1.5); dc.Pop(); }
            dc.DrawEllipse(color, null, center, 6, 6);
        }
        else if (options.ClockStyle == "Flip")
        {
            var current = DisplayedTime.Replace(":", ""); var old = previous.Replace(":", "");
            var progress = CanAnimate ? Math.Clamp(FlipProgress, 0, 1) : 1;
            var ink = SystemParameters.HighContrast || customColor is not null ? color : Brushes.White;
            var face = SystemParameters.HighContrast ? SystemColors.WindowBrush : FlipFace;
            var lowerFace = SystemParameters.HighContrast ? SystemColors.WindowBrush : FlipBottomFace;
            for (var i = 0; i < current.Length; i++)
            {
                var left = (w - geometry.TimeWidth) / 2 + i * FlipDigitWidth + i / 2 * FlipGroupGap + (i + 1) / 2 * FlipDigitGap;
                var top = 2d; var hinge = top + FlipDigitHeight / 2;
                void Half(char digit, bool upper, double foldScale = 1, double shade = 0)
                {
                    if (foldScale <= 0) return;
                    dc.PushTransform(new ScaleTransform(1, foldScale, left + FlipDigitWidth / 2, hinge));
                    var half = new Rect(left, upper ? top : hinge, FlipDigitWidth, FlipDigitHeight / 2);
                    dc.PushClip(new RectangleGeometry(half));
                    dc.DrawRoundedRectangle(upper ? face : lowerFace, new Pen(ink, .5), new Rect(left, top, FlipDigitWidth, FlipDigitHeight), 12, 12);
                    Text(digit.ToString(), 135, left + FlipDigitWidth / 2, top + geometry.TimeTop, ink);
                    if (!SystemParameters.HighContrast && shade > 0)
                    {
                        dc.PushOpacity(shade); dc.DrawRectangle(FoldShade, null, half); dc.Pop();
                    }
                    dc.Pop(); dc.Pop();
                }
                var changing = progress < 1 && old.Length == current.Length && old[i] != current[i];
                // The outgoing old upper half covers the new upper half. Keep the
                // old lower half underneath until the new lower half unfolds.
                Half(current[i], true); Half(changing ? old[i] : current[i], false);
                if (changing)
                {
                    var firstHalf = progress < .5;
                    var foldScale = Math.Abs(Math.Cos(Math.PI * progress));
                    if (firstHalf) Half(old[i], true, foldScale, (1 - foldScale) * .7);
                    else
                    {
                        if (!SystemParameters.HighContrast)
                        {
                            var edge = hinge + FlipDigitHeight / 2 * foldScale;
                            var shadowHeight = Math.Min(14 * (1 - foldScale), top + FlipDigitHeight - edge);
                            dc.PushOpacity((1 - foldScale) * .6);
                            dc.DrawRectangle(FoldShade, null, new Rect(left + 1, edge, FlipDigitWidth - 2, shadowHeight)); dc.Pop();
                        }
                        Half(current[i], false, foldScale, (1 - foldScale) * .5);
                    }
                }
                dc.DrawLine(new Pen(SystemParameters.HighContrast ? ink : Brushes.Black, 2), new(left, hinge), new(left + FlipDigitWidth, hinge));
                dc.PushOpacity(.25); dc.DrawLine(new Pen(ink, 1), new(left + 1, hinge + 1.5), new(left + FlipDigitWidth - 1, hinge + 1.5)); dc.Pop();
                foreach (var pinX in new[] { left + 5, left + FlipDigitWidth - 5 })
                    dc.DrawRoundedRectangle(SystemParameters.HighContrast ? ink : FlipFace, new Pen(ink, .5), new Rect(pinX - 1.5, hinge - 4, 3, 8), 1, 1);
            }
        }
        else
        {
            var text = DisplayedTime;
            var centers = ClockTextLayout.Centers(text, geometry.DigitWidth, geometry.SeparatorWidth);
            for (var i = 0; i < text.Length; i++) Text(text[i].ToString(), 170, w / 2 + centers[i], 2 + geometry.TimeTop, font: "Segoe UI Light");
        }
        if (!options.Use24Hours) Text(now.Hour < 12 ? "AM" : "PM", 18, w / 2, 2 + geometry.MeridiemTop);
        if (options.ShowDate) Text(now.ToString("M 月 d 日 · dddd", DateCulture), 22, w / 2, 2 + geometry.DateTop);
        dc.Pop(); dc.Pop();
    }
    public void Dispose() { SetActive(false); timer.Tick -= OnTick; }
}
