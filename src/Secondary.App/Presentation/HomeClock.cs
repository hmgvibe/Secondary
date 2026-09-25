using System;
using System.Globalization;
using System.Collections.Generic;
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
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private HomeSettings options = new();
    private DateTime now = DateTime.Now;
    private string previous = "";
    private readonly Dictionary<(string Font, double Size, double Dpi), double> digitWidths = new();
    public bool IsRunning => timer.IsEnabled;
    public bool MotionAllowed { get; set; } = true;
    public bool FitWidget { get; set; }
    public HomeSettings Options => options;
    public event Action<HomeSettings>? ConfigurationChanged;
    public string DisplayedTime => options.TimeText(now);
    public static readonly DependencyProperty AutomaticBrushProperty = DependencyProperty.Register(nameof(AutomaticBrush), typeof(Brush), typeof(HomeClock), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush AutomaticBrush { get => (Brush)GetValue(AutomaticBrushProperty); set => SetValue(AutomaticBrushProperty, value); }
    public static readonly DependencyProperty FlipProgressProperty = DependencyProperty.Register(nameof(FlipProgress), typeof(double), typeof(HomeClock), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double FlipProgress { get => (double)GetValue(FlipProgressProperty); set => SetValue(FlipProgressProperty, value); }
    public HomeClock() { timer.Tick += OnTick; ClipToBounds = true; SetResourceReference(AutomaticBrushProperty, "TextBrush"); }
    protected override AutomationPeer OnCreateAutomationPeer() => new ClockPeer(this);
    private sealed class ClockPeer(HomeClock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => "HomeClock";
    }
    public void Configure(HomeSettings value)
    {
        options = value.Normalize(); previous = "";
        BeginAnimation(FlipProgressProperty, null); FlipProgress = 1;
        UpdateTime(DateTime.Now, false); InvalidateVisual();
        if (timer.IsEnabled) { timer.Stop(); Schedule(); }
        ConfigurationChanged?.Invoke(options);
    }
    public void SetActive(bool active)
    {
        if (active == timer.IsEnabled) return;
        if (active) { UpdateTime(DateTime.Now, false); Schedule(); }
        else { timer.Stop(); BeginAnimation(FlipProgressProperty, null); FlipProgress = 1; }
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
        now = value;
        previous = old;
        if (animate && old != DisplayedTime && options.ClockStyle == "Flip" && MotionAllowed && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)) { FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => BeginAnimation(FlipProgressProperty, null);
            BeginAnimation(FlipProgressProperty, animation);
        }
        AutomationProperties.SetName(this, value.ToString("yyyy年M月d日 ") + DisplayedTime + (!options.Use24Hours ? (value.Hour < 12 ? " 上午" : " 下午") : ""));
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var analog = options.ClockStyle == "Analog";
        var w = analog ? 400d : options.ShowSeconds ? 940d : 660d;
        var h = analog ? 440d : 260d;
        var scale = Math.Min(Math.Min(ActualWidth / w, ActualHeight / h), FitWidget ? double.MaxValue : .7 * options.Size);
        var x = (ActualWidth - w * scale) * (FitWidget ? .5 : options.X);
        var y = (ActualHeight - h * scale) * (FitWidget ? .5 : options.Y);
        dc.PushTransform(new TranslateTransform(x, y)); dc.PushTransform(new ScaleTransform(scale, scale));
        var color = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : options.Color == "Auto"
            ? options.Background == "Static" ? AutomaticBrush : Brushes.White : (Brush)new BrushConverter().ConvertFromString(options.Color)!;
        void Text(string text, double size, double cx, double top, Brush? brush = null, string font = "Bahnschrift")
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font), size, brush ?? color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new Point(cx - formatted.Width / 2, top));
        }
        void Digits(string text, double size, double cx, double top, Brush? brush = null, string font = "Bahnschrift")
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var key = (font,size,dpi);
            if (!digitWidths.TryGetValue(key,out var advance))
            {
                // Measure once per font/size/DPI, including fallback fonts. Keep the
                // original typeface while centering every digit in an equal slot.
                for (var digit = 0; digit <= 9; digit++)
                {
                    var glyph = new FormattedText(digit.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,new Typeface(font),size,Brushes.White,dpi);
                    advance = Math.Max(advance,glyph.WidthIncludingTrailingWhitespace);
                }
                digitWidths[key] = advance;
            }
            var centers = ClockTextLayout.Centers(text,advance,size * .28);
            for (var i = 0; i < text.Length; i++) Text(text[i].ToString(),size,cx + centers[i],top,brush,font);
        }
        if (analog)
        {
            var center = new Point(200, 190);
            dc.PushOpacity(.35); dc.DrawEllipse(null, new Pen(color, 1), center, 178, 178); dc.Pop();
            for (var i = 0; i < 60; i++)
            {
                var a = i * Math.PI / 30;
                var r = i % 5 == 0 ? 148 : 159;
                dc.DrawLine(new Pen(color, i % 5 == 0 ? 3 : 1), new(200 + Math.Sin(a) * r, 190 - Math.Cos(a) * r), new(200 + Math.Sin(a) * 166, 190 - Math.Cos(a) * 166));
            }
            void Hand(double units, double length, double thickness)
            {
                var angle = units * Math.PI / 30;
                dc.DrawLine(new Pen(color, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, new(200 + Math.Sin(angle) * length, 190 - Math.Cos(angle) * length));
            }
            Hand(now.Hour % 12 * 5 + now.Minute / 12d, 89, 7); Hand(now.Minute, 127, 4);
            if (options.ShowSeconds) { dc.PushOpacity(.6); Hand(now.Second, 145, 1.5); dc.Pop(); }
            dc.DrawEllipse(color, null, center, 6, 6);
            if (!options.Use24Hours) Text(now.Hour < 12 ? "AM" : "PM", 16, 200, 270);
        }
        else if (options.ClockStyle == "Flip")
        {
            var parts = DisplayedTime.Split(':'); var oldParts = previous.Split(':');
            var tile = (w - (parts.Length - 1) * 20) / parts.Length;
            for (var i = 0; i < parts.Length; i++)
            {
                var left = i * (tile + 20);
                var face = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(24, 30, 35));
                var ink = SystemParameters.HighContrast || options.Color != "Auto" ? color : Brushes.White;
                dc.DrawRoundedRectangle(face, new Pen(ink, .5), new Rect(left, 0, tile, 190), 14, 14);
                Digits(parts[i], 135, left + tile / 2, 4, ink);
                // Fold only changing digits. The completed animation removes its clock automatically.
                if (FlipProgress < 1 && oldParts.Length == parts.Length && oldParts[i] != parts[i])
                {
                    var firstHalf = FlipProgress < .5;
                    dc.PushClip(new RectangleGeometry(new Rect(left, firstHalf ? 0 : 95, tile, 95)));
                    dc.PushTransform(new ScaleTransform(1, Math.Abs(1 - 2 * FlipProgress), left + tile / 2, 95));
                    dc.DrawRoundedRectangle(face, null, new Rect(left, 0, tile, 190), 14, 14);
                    Digits(firstHalf ? oldParts[i] : parts[i], 135, left + tile / 2, 4, ink);
                    dc.Pop(); dc.Pop();
                }
                dc.DrawLine(new Pen(Brushes.Black, 2), new(left, 95), new(left + tile, 95));
            }
            if (!options.Use24Hours) Text(now.Hour < 12 ? "AM" : "PM", 18, w / 2, 196);
        }
        else
        {
            Digits(DisplayedTime, 170, w / 2, 0, font: "Segoe UI Light");
            if (!options.Use24Hours) Text(now.Hour < 12 ? "AM" : "PM", 18, w / 2, 186);
        }
        if (options.ShowDate) Text(now.ToString("M 月 d 日 · dddd", CultureInfo.GetCultureInfo("zh-TW")), 22, w / 2, analog ? 405 : 229);
        dc.Pop(); dc.Pop();
    }
    public void Dispose() { SetActive(false); timer.Tick -= OnTick; }
}
