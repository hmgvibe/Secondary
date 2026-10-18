using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Secondary.Core;
using Secondary.Shared;

namespace Secondary.Linux.Home;

// One wall-clock timer, scheduled at the next displayed time boundary. The only frame loop is
// the 420 ms flip, which stops itself. Layout and drawing follow the WPF HomeClock.
public sealed class ClockView : Control, IDisposable
{
    private const double DefaultScale = .7;
    private const double FlipDigitWidth = 140, FlipDigitHeight = 190, FlipDigitGap = 8, FlipGroupGap = 28;
    private static readonly TimeSpan FlipDuration = TimeSpan.FromMilliseconds(420);
    private static readonly CultureInfo DateCulture = CultureInfo.GetCultureInfo("zh-TW");
    private static readonly FontFamily ClockFont = new("fonts:Inter#Inter");
    private static readonly IBrush FlipFace = new ImmutableSolidColorBrush(Color.FromRgb(24, 30, 35));
    private static readonly IBrush FlipBottomFace = new ImmutableSolidColorBrush(Color.FromRgb(20, 26, 31));
    private static readonly IBrush FoldShade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.FromArgb(170, 0, 0, 0), 1) }
    }.ToImmutable();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly DispatcherTimer flipTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Dictionary<(string Text, FontWeight Weight, double Size, IBrush Ink), FormattedText> textCache = new();
    private readonly Dictionary<(FontWeight Weight, double Size), (double DigitWidth, Rect InkBounds)> metrics = new();
    private HomeSettings options = new();
    private DateTime now = DateTime.Now;
    private string previous = "";
    private IBrush? customColor;
    private ClockLayout? layout;
    private bool motionAllowed = true, highContrast;
    private DateTime flipStarted;

    private sealed record ClockLayout(Size Size, double TimeWidth, double TimeHeight, double TimeTop, double MeridiemTop, double DateTop, double DigitWidth, double SeparatorWidth);

    public static readonly StyledProperty<IBrush?> AutomaticBrushProperty = AvaloniaProperty.Register<ClockView, IBrush?>(nameof(AutomaticBrush));
    public IBrush? AutomaticBrush { get => GetValue(AutomaticBrushProperty); set => SetValue(AutomaticBrushProperty, value); }
    public double FlipProgress { get; private set; } = 1;
    public bool IsRunning => timer.IsEnabled;
    public bool IsFlipping => flipTimer.IsEnabled;
    public bool FitWidget { get; set; } = true;
    public HomeSettings Options => options;
    public string DisplayedTime => options.TimeText(now);
    public bool MotionAllowed { get => motionAllowed; set { motionAllowed = value; if (!value) StopFlip(); } }
    public bool HighContrast { get => highContrast; set { highContrast = value; if (value) StopFlip(); InvalidateVisual(); } }
    // Format and font metrics determine these bounds; changing time or date does not.
    public LayoutSize NaturalSize { get { var size = GetLayout().Size; return new(size.Width * DefaultScale, size.Height * DefaultScale); } }
    public event Action<HomeSettings>? ConfigurationChanged;
    private bool CanAnimate => MotionAllowed && !HighContrast;

    static ClockView() => AffectsRender<ClockView>(AutomaticBrushProperty);
    public ClockView()
    {
        ClipToBounds = true;
        Bind(AutomaticBrushProperty, this.GetResourceObservable("TextBrush"));
        timer.Tick += OnTick;
        flipTimer.Tick += (_, _) =>
        {
            FlipProgress = Math.Clamp((DateTime.UtcNow - flipStarted) / FlipDuration, 0, 1);
            if (FlipProgress >= 1) StopFlip(); else InvalidateVisual();
        };
        DetachedFromVisualTree += (_, _) => SetActive(false);
    }

    public void Configure(HomeSettings value)
    {
        options = value.Normalize(); previous = ""; layout = null;
        customColor = options.Color == "Auto" ? null : new ImmutableSolidColorBrush(Color.Parse(options.Color));
        textCache.Clear(); StopFlip();
        UpdateTime(DateTime.Now, false); InvalidateMeasure(); InvalidateVisual();
        if (timer.IsEnabled) { timer.Stop(); Schedule(); }
        ConfigurationChanged?.Invoke(options);
    }
    public void SetActive(bool active)
    {
        if (!active) { timer.Stop(); StopFlip(); return; }
        if (timer.IsEnabled) return;
        UpdateTime(DateTime.Now, false); Schedule();
    }
    private void StopFlip() { flipTimer.Stop(); FlipProgress = 1; InvalidateVisual(); }
    private void Schedule()
    {
        var current = DateTime.Now;
        timer.Interval = TimeSpan.FromMilliseconds(options.ShowSeconds ? 1000 - current.Millisecond : (60 - current.Second) * 1000 - current.Millisecond);
        timer.Start();
    }
    private void OnTick(object? sender, EventArgs e) { timer.Stop(); UpdateTime(DateTime.Now, true); Schedule(); }
    public void UpdateTime(DateTime value, bool animate = false)
    {
        var old = DisplayedTime;
        StopFlip(); now = value; previous = old;
        if (animate && old != DisplayedTime && options.ClockStyle == "Flip" && CanAnimate)
        {
            flipStarted = DateTime.UtcNow; FlipProgress = 0; flipTimer.Start();
        }
        AutomationProperties.SetName(this, value.ToString("yyyy年M月d日 ", DateCulture) + DisplayedTime + (!options.Use24Hours ? (value.Hour < 12 ? " 上午" : " 下午") : ""));
        InvalidateVisual();
    }

    private FormattedText Formatted(string text, double size, IBrush ink, FontWeight weight = FontWeight.SemiBold)
    {
        var key = (text, weight, size, ink);
        if (!textCache.TryGetValue(key, out var formatted))
        {
            if (textCache.Count > 256) textCache.Clear();
            formatted = new FormattedText(text, DateCulture, FlowDirection.LeftToRight, new Typeface(ClockFont, FontStyle.Normal, weight), size, ink);
            textCache[key] = formatted;
        }
        return formatted;
    }
    private (double DigitWidth, Rect InkBounds) DigitMetrics(FontWeight weight, double size)
    {
        if (metrics.TryGetValue((weight, size), out var result)) return result;
        double advance = 0; Rect? bounds = null;
        for (var digit = 0; digit <= 9; digit++)
        {
            var text = Formatted(digit.ToString(CultureInfo.InvariantCulture), size, Brushes.White, weight);
            advance = Math.Max(advance, text.WidthIncludingTrailingWhitespace);
            var ink = text.BuildGeometry(new Point())?.Bounds ?? new Rect(0, 0, text.Width, text.Height);
            bounds = bounds is { } b ? b.Union(ink) : ink;
        }
        result = (advance, bounds!.Value); metrics[(weight, size)] = result; return result;
    }
    private double DateWidth()
    {
        // Reserve the longest month, day and weekday so midnight never moves the frame.
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
        var timeMetrics = DigitMetrics(flip ? FontWeight.SemiBold : FontWeight.Light, flip ? 135 : 170);
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
        layout = new ClockLayout(new Size(Math.Ceiling(width) + 4, Math.Ceiling(bottom) + 4), timeWidth, timeHeight, timeTop, meridiemTop, dateTop, timeMetrics.DigitWidth, separator);
        return layout;
    }

    public override void Render(DrawingContext dc)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var geometry = GetLayout(); var w = geometry.Size.Width; var h = geometry.Size.Height;
        var scale = Math.Min(Math.Min(Bounds.Width / w, Bounds.Height / h), FitWidget ? double.MaxValue : DefaultScale * options.Size);
        var x = (Bounds.Width - w * scale) * (FitWidget ? .5 : options.X);
        var y = (Bounds.Height - h * scale) * (FitWidget ? .5 : options.Y);
        using var placement = dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y));
        var automatic = AutomaticBrush ?? Brushes.White;
        var color = HighContrast ? automatic : customColor ?? (options.Background == "Static" ? automatic : Brushes.White);
        void Text(string text, double size, double cx, double top, IBrush? brush = null, FontWeight weight = FontWeight.SemiBold)
        {
            var formatted = Formatted(text, size, brush ?? color, weight);
            dc.DrawText(formatted, new Point(cx - formatted.Width / 2, top));
        }
        if (options.ClockStyle == "Analog")
        {
            var cx = w / 2; const double cy = 182; var center = new Point(cx, cy);
            using (dc.PushOpacity(.35)) dc.DrawEllipse(null, new Pen(color, 1), center, 178, 178);
            for (var i = 0; i < 60; i++)
            {
                var a = i * Math.PI / 30; var r = i % 5 == 0 ? 148 : 159;
                dc.DrawLine(new Pen(color, i % 5 == 0 ? 3 : 1), new(cx + Math.Sin(a) * r, cy - Math.Cos(a) * r), new(cx + Math.Sin(a) * 166, cy - Math.Cos(a) * 166));
            }
            void Hand(double units, double length, double thickness)
            {
                var angle = units * Math.PI / 30;
                dc.DrawLine(new Pen(color, thickness, lineCap: PenLineCap.Round), center, new(cx + Math.Sin(angle) * length, cy - Math.Cos(angle) * length));
            }
            Hand(now.Hour % 12 * 5 + now.Minute / 12d, 89, 7); Hand(now.Minute, 127, 4);
            if (options.ShowSeconds) using (dc.PushOpacity(.6)) Hand(now.Second, 145, 1.5);
            dc.DrawEllipse(color, null, center, 6, 6);
        }
        else if (options.ClockStyle == "Flip")
        {
            var current = DisplayedTime.Replace(":", ""); var old = previous.Replace(":", "");
            var progress = CanAnimate ? Math.Clamp(FlipProgress, 0, 1) : 1;
            var ink = HighContrast || customColor is not null ? color : Brushes.White;
            var face = HighContrast ? Brushes.Black : FlipFace;
            var lowerFace = HighContrast ? Brushes.Black : FlipBottomFace;
            for (var i = 0; i < current.Length; i++)
            {
                var left = (w - geometry.TimeWidth) / 2 + i * FlipDigitWidth + i / 2 * FlipGroupGap + (i + 1) / 2 * FlipDigitGap;
                var top = 2d; var hinge = top + FlipDigitHeight / 2;
                void Half(char digit, bool upper, double foldScale = 1, double shade = 0)
                {
                    if (foldScale <= 0) return;
                    var pivot = new Point(left + FlipDigitWidth / 2, hinge);
                    using var fold = dc.PushTransform(Matrix.CreateTranslation(-pivot.X, -pivot.Y) * Matrix.CreateScale(1, foldScale) * Matrix.CreateTranslation(pivot.X, pivot.Y));
                    var half = new Rect(left, upper ? top : hinge, FlipDigitWidth, FlipDigitHeight / 2);
                    using var clip = dc.PushClip(half);
                    dc.DrawRectangle(upper ? face : lowerFace, new Pen(ink, .5), new Rect(left, top, FlipDigitWidth, FlipDigitHeight), 12, 12);
                    Text(digit.ToString(), 135, left + FlipDigitWidth / 2, top + geometry.TimeTop, ink);
                    if (!HighContrast && shade > 0) using (dc.PushOpacity(shade)) dc.DrawRectangle(FoldShade, null, half);
                }
                var changing = progress < 1 && old.Length == current.Length && old[i] != current[i];
                // The outgoing upper half covers the new one; the old lower half stays until the new one unfolds.
                Half(current[i], true); Half(changing ? old[i] : current[i], false);
                if (changing)
                {
                    var foldScale = Math.Abs(Math.Cos(Math.PI * progress));
                    if (progress < .5) Half(old[i], true, foldScale, (1 - foldScale) * .7);
                    else
                    {
                        if (!HighContrast)
                        {
                            var edge = hinge + FlipDigitHeight / 2 * foldScale;
                            var shadowHeight = Math.Min(14 * (1 - foldScale), top + FlipDigitHeight - edge);
                            using (dc.PushOpacity((1 - foldScale) * .6)) dc.DrawRectangle(FoldShade, null, new Rect(left + 1, edge, FlipDigitWidth - 2, Math.Max(0, shadowHeight)));
                        }
                        Half(current[i], false, foldScale, (1 - foldScale) * .5);
                    }
                }
                dc.DrawLine(new Pen(HighContrast ? ink : Brushes.Black, 2), new(left, hinge), new(left + FlipDigitWidth, hinge));
                using (dc.PushOpacity(.25)) dc.DrawLine(new Pen(ink, 1), new(left + 1, hinge + 1.5), new(left + FlipDigitWidth - 1, hinge + 1.5));
                foreach (var pinX in new[] { left + 5, left + FlipDigitWidth - 5 })
                    dc.DrawRectangle(HighContrast ? ink : FlipFace, new Pen(ink, .5), new Rect(pinX - 1.5, hinge - 4, 3, 8), 1, 1);
            }
        }
        else
        {
            var text = DisplayedTime;
            var centers = ClockTextLayout.Centers(text, geometry.DigitWidth, geometry.SeparatorWidth);
            for (var i = 0; i < text.Length; i++) Text(text[i].ToString(), 170, w / 2 + centers[i], 2 + geometry.TimeTop, weight: FontWeight.Light);
        }
        if (!options.Use24Hours) Text(now.Hour < 12 ? "AM" : "PM", 18, w / 2, 2 + geometry.MeridiemTop);
        if (options.ShowDate) Text(now.ToString("M 月 d 日 · dddd", DateCulture), 22, w / 2, 2 + geometry.DateTop, weight: FontWeight.Normal);
    }

    public void Dispose() { SetActive(false); timer.Tick -= OnTick; }
}
