using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>The progress ring: a graphite track and a silver arc that sweeps
/// in once on entry. One element, drawn in OnRender — no layout churn.</summary>
sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    readonly double _thickness;
    readonly Pen _track;
    readonly Pen _arc;

    static readonly LinearGradientBrush ArcBrush = MakeArcBrush();

    public RingGauge(double diameter, double thickness)
    {
        Width = diameter;
        Height = diameter;
        _thickness = thickness;
        _track = new Pen(Tone.Track, thickness);
        _track.Freeze();
        _arc = new Pen(ArcBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _arc.Freeze();
        FlowDirection = FlowDirection.LeftToRight;
    }

    static LinearGradientBrush MakeArcBrush()
    {
        var b = new LinearGradientBrush(Tone.Accent.Color, Tone.Graphite.Color, new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    /// <summary>Sweeps from zero to <paramref name="value"/> (0..1).</summary>
    public void SweepTo(double value, int delay = 120)
    {
        if (Kit.Entering) BeginAnimation(ProgressProperty, Feel.FromTo(0, Math.Clamp(value, 0, 1), Feel.Count, Feel.Expo, delay));
        else Progress = Math.Clamp(value, 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = (size - _thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, _track, c, r, r);
        var p = Math.Clamp(Progress, 0, 1);
        if (p <= 0.001) return;
        if (p >= 0.9995)
        {
            dc.DrawEllipse(null, _arc, c, r, r);
            return;
        }
        var angle = p * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, _arc, geo);
    }
}

/// <summary>
/// Deposits per work day: bars (today in silver, future days as dashed
/// "needed" outlines), the cumulative line and the straight target-pace
/// line, with counts above and day numbers below. Grows in once.
/// </summary>
sealed class DayChart : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DayChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    const double PlotHeight = 150;
    const double TopBand = 18;
    const double BottomBand = 22;
    const double Gap = 12;

    readonly List<DayBar> _bars;
    readonly int? _target;
    readonly FormattedText?[] _counts;
    readonly FormattedText[] _days;

    static readonly SolidColorBrush PastFill = Tone.B("#33FFFFFF");
    static readonly Pen NeededPen = MakePen(Tone.B("#47FFFFFF"), 1, dashed: true);
    static readonly Pen PacePen = MakePen(Tone.B("#4DFFFFFF"), 1.5, dashed: true);
    static readonly Pen LinePen = MakePen(Tone.Accent, 2.5, dashed: false);
    static readonly Pen DotRing = MakePen(Tone.B("#99000000"), 3, dashed: false);

    public DayChart(List<DayBar> bars, int? target)
    {
        _bars = bars;
        _target = target;
        Height = TopBand + PlotHeight + BottomBand;
        FlowDirection = FlowDirection.LeftToRight;
        _counts = new FormattedText?[bars.Count];
        _days = new FormattedText[bars.Count];
    }

    double _textDpi;

    void EnsureText()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi == _textDpi) return;
        _textDpi = dpi;
        var bars = _bars;
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var color = b.IsToday ? Tone.Accent : b.IsFuture ? Tone.Faint : Tone.Muted;
            var countText = b.IsFuture ? (b.Needed > 0 ? He.R1(b.Needed) : "") : b.Count > 0 ? b.Count.ToString(CultureInfo.InvariantCulture) : "";
            if (countText.Length > 0) _counts[i] = Text(countText, color, dpi);
            _days[i] = Text(b.Date.Day.ToString(CultureInfo.InvariantCulture), color, dpi);
        }
    }

    static FormattedText Text(string s, Brush brush, double dpi) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Font.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, brush, dpi);

    static Pen MakePen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 4 }, 0);
        pen.Freeze();
        return pen;
    }

    public void Grow()
    {
        if (Kit.Entering) BeginAnimation(ProgressProperty, Feel.FromTo(0, 1, 1600, Feel.Expo, 120));
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = _bars.Count;
        if (n == 0 || ActualWidth <= 0) return;
        EnsureText();
        var w = ActualWidth;
        var colW = (w - Gap * (n - 1)) / n;
        var barW = Math.Min(30, colW);
        var baseY = TopBand + PlotHeight;
        var p = Progress;

        var maxCount = Math.Max(1, _bars.Max(b => b.IsFuture ? b.Needed : b.Count));
        var perDeal = Math.Min(20, (PlotHeight - 24) / maxCount);

        // Cumulative scale: the target when set, else the month's total.
        var finalCum = _bars.LastOrDefault(b => !b.IsFuture)?.Cumulative ?? 0;
        double scale = _target is int t && t > 0 ? t : Math.Max(1, finalCum);

        if (_target is int && p > 0)
        {
            dc.PushOpacity(Math.Min(1, p * 2));
            dc.DrawLine(PacePen, new Point(0, baseY), new Point(w, TopBand + 10));
            dc.Pop();
        }

        var linePts = new List<Point>();
        for (var i = 0; i < n; i++)
        {
            var b = _bars[i];
            var cx = i * (colW + Gap) + colW / 2;
            // Each bar starts 35ms after the previous one, over a 0.9s grow.
            var local = Math.Clamp((p * 1600 - 35 * i) / 900, 0, 1);
            var e = 1 - Math.Pow(1 - local, 4);
            double h;
            if (b.IsFuture)
            {
                h = Math.Max(4, b.Needed * perDeal) * e;
                if (b.Needed > 0 && h > 0.5)
                    dc.DrawRoundedRectangle(null, NeededPen, new Rect(cx - barW / 2 + 0.5, baseY - h + 0.5, barW - 1, h - 1), 7, 7);
            }
            else
            {
                h = Math.Max(b.Count * perDeal, 3) * e;
                if (h > 0.5)
                    dc.DrawRoundedRectangle(b.IsToday ? Tone.Accent : PastFill, null, new Rect(cx - barW / 2, baseY - h, barW, h), Math.Min(8, barW / 2), Math.Min(8, barW / 2));
                linePts.Add(new Point(cx, baseY - Math.Min(b.Cumulative, scale) / scale * (PlotHeight - 10)));
            }
            if (_counts[i] is { } ct && e > 0.05)
            {
                dc.PushOpacity(e);
                dc.DrawText(ct, new Point(cx - ct.Width / 2, baseY - h - ct.Height - 3));
                dc.Pop();
            }
            var dt = _days[i];
            dc.DrawText(dt, new Point(cx - dt.Width / 2, baseY + 8));
        }

        // The cumulative line draws left→right with the progress.
        if (linePts.Count > 0)
        {
            var drawTo = linePts[0].X + (linePts[^1].X - linePts[0].X) * Math.Clamp((p - 0.15) / 0.85, 0, 1);
            var geo = new StreamGeometry();
            Point last = linePts[0];
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(linePts[0], false, false);
                for (var i = 1; i < linePts.Count; i++)
                {
                    var a = linePts[i - 1];
                    var b = linePts[i];
                    if (b.X <= drawTo)
                    {
                        ctx.LineTo(b, true, true);
                        last = b;
                    }
                    else
                    {
                        var f = (drawTo - a.X) / Math.Max(0.001, b.X - a.X);
                        if (f > 0)
                        {
                            last = new Point(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
                            ctx.LineTo(last, true, true);
                        }
                        break;
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, LinePen, geo);
            if (p >= 0.999)
            {
                dc.DrawEllipse(Tone.Accent, DotRing, linePts[^1], 5, 5);
            }
        }
    }
}

/// <summary>The round check: an empty ring that fills green with a popping tick.</summary>
sealed class CheckDot : Border
{
    readonly FrameworkElement _tick;
    bool _on;

    public event Action<bool>? Toggled;

    public CheckDot(bool on, double size = 26)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        BorderThickness = new Thickness(1.5);
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        VerticalAlignment = VerticalAlignment.Center;
        _tick = Kit.Icon(Icons.Check, size * 0.58, Tone.OnPrimary, 2.8);
        Child = _tick;
        _on = on;
        Paint(false);
        Kit.Clickable(this, () =>
        {
            Set(!_on, animate: true);
            Toggled?.Invoke(_on);
        });
        Kit.Press(this, 0.9);
    }

    public bool IsOn => _on;

    public void Set(bool on, bool animate)
    {
        _on = on;
        Paint(animate);
    }

    void Paint(bool animate)
    {
        Background = _on ? Tone.Green : Brushes.Transparent;
        BorderBrush = _on ? Tone.Green : Tone.B("#4DFFFFFF");
        _tick.Visibility = _on ? Visibility.Visible : Visibility.Hidden;
        if (_on && animate) Kit.Pop(_tick);
    }
}

/// <summary>
/// A virtualized list whose rows are built in code on demand: only rows on
/// screen exist, containers are recycled, and scrolling is per-pixel. Give
/// it a Builder and an ItemsSource; bound its height (MaxHeight) so it can
/// scroll inside the page.
/// </summary>
sealed class VirtualList : ItemsControl
{
    public Func<object, FrameworkElement> Builder { get; }

    static readonly DataTemplate RowTemplate = MakeTemplate();
    static readonly ControlTemplate ListTemplate = MakeListTemplate();

    public VirtualList(Func<object, FrameworkElement> builder)
    {
        Builder = builder;
        ItemTemplate = RowTemplate;
        Template = ListTemplate;
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        Focusable = false;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Scroll") is ScrollViewer sv) Ui.ChainWheel(Ui.ThinScroll(sv));
    }

    static DataTemplate MakeTemplate()
    {
        var t = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(LazyRow)) };
        t.Seal();
        return t;
    }

    static ControlTemplate MakeListTemplate()
    {
        var t = new ControlTemplate(typeof(ItemsControl));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_Scroll");
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.SetValue(FrameworkElement.FocusVisualStyleProperty, null);
        sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        t.VisualTree = sv;
        t.Seal();
        return t;
    }

    /// <summary>Rebuilds its row whenever a recycled container gets a new item.</summary>
    sealed class LazyRow : Decorator
    {
        public LazyRow()
        {
            DataContextChanged += (_, _) => Build();
            Loaded += (_, _) => { if (Child is null) Build(); };
        }

        void Build()
        {
            if (DataContext is null) { Child = null; return; }
            DependencyObject? p = this;
            while (p is not null && p is not VirtualList) p = VisualTreeHelper.GetParent(p);
            if (p is VirtualList list) Child = list.Builder(DataContext);
        }
    }
}
