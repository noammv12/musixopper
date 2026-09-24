using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// When the Now window may move on its own. Nothing plays during a call
/// (the rep is talking — the screen stays still), and Windows' "show
/// animations" off means reduced motion: values land in place.
/// </summary>
static class Fx
{
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    public static bool Allowed(TerminalWindow host) => !Reduced && host.CallStateSource() != CallState.OnCall;

    public static SolidColorBrush Frozen(string hex) => Tone.B(hex);

    public static LinearGradientBrush Vertical(string top, string bottom)
    {
        var b = new LinearGradientBrush(Tone.C(top), Tone.C(bottom), new Point(0, 0), new Point(0, 1));
        b.Freeze();
        return b;
    }

    public static RadialGradientBrush Glow(string center, string edge)
    {
        var b = new RadialGradientBrush(Tone.C(center), Tone.C(edge));
        b.Freeze();
        return b;
    }

    /// <summary>The design's "glass2": deep, more opaque glass with a lit rim.</summary>
    public static Border Glass2(double radius, Thickness padding) => new()
    {
        CornerRadius = new CornerRadius(radius),
        Background = Vertical("#D128292E", "#DB121215"),
        BorderBrush = Tone.GlassDeepRim,
        BorderThickness = new Thickness(1),
        Padding = padding,
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 44, ShadowDepth = 14, Direction = 270, Opacity = 0.55, RenderingBias = RenderingBias.Performance },
    };

    /// <summary>A small caps-less label in the design's kicker grey.</summary>
    public static TextBlock Kicker(string text, Brush? brush = null) => Kit.T(text, 12.5, brush ?? Tone.Muted, FontWeights.Medium);
}

/// <summary>
/// The three daily rings (calls, callbacks cleared, deposits vs. pace), one
/// element drawn in OnRender. Each ring's progress is its own animatable
/// property, so they sweep in staggered and never touch layout.
/// </summary>
sealed class TripleRing : FrameworkElement
{
    public static readonly DependencyProperty P0Property = Reg(nameof(P0));
    public static readonly DependencyProperty P1Property = Reg(nameof(P1));
    public static readonly DependencyProperty P2Property = Reg(nameof(P2));

    static DependencyProperty Reg(string name) => DependencyProperty.Register(name, typeof(double), typeof(TripleRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double P0 { get => (double)GetValue(P0Property); set => SetValue(P0Property, value); }
    public double P1 { get => (double)GetValue(P1Property); set => SetValue(P1Property, value); }
    public double P2 { get => (double)GetValue(P2Property); set => SetValue(P2Property, value); }

    const double Stroke = 11;
    static readonly Pen Track = MakePen(Tone.B("#14FFFFFF"), false);
    static readonly Pen[] Arcs =
    {
        MakePen(Grad("#FFE4ECF7", "#FF8FA3C2"), true),
        MakePen(Grad("#FFF1E2C4", "#FFB39565"), true),
        MakePen(Grad("#FFD2F0E0", "#FF6FB594"), true),
    };

    /// <summary>The ring colors' legend dots, outer to inner.</summary>
    public static readonly SolidColorBrush[] Dots = { Tone.B("#FFB7C8E2"), Tone.B("#FFDCC7A0"), Tone.B("#FFA6D6BE") };

    static LinearGradientBrush Grad(string a, string b)
    {
        var g = new LinearGradientBrush(Tone.C(a), Tone.C(b), new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    static Pen MakePen(Brush brush, bool round)
    {
        var p = new Pen(brush, Stroke);
        if (round) p.StartLineCap = p.EndLineCap = PenLineCap.Round;
        p.Freeze();
        return p;
    }

    public TripleRing(double size)
    {
        Width = Height = size;
        FlowDirection = FlowDirection.LeftToRight;
    }

    public void Set(IReadOnlyList<DayRing> rings, bool animate)
    {
        var props = new[] { P0Property, P1Property, P2Property };
        for (var i = 0; i < 3 && i < rings.Count; i++)
        {
            var to = rings[i].Fraction;
            if (animate) BeginAnimation(props[i], Feel.To(to, Feel.Count + 200, Feel.Expo, 200 + i * 150));
            else
            {
                BeginAnimation(props[i], null);
                SetValue(props[i], to);
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var values = new[] { P0, P1, P2 };
        for (var i = 0; i < 3; i++)
        {
            var r = size / 2 - Stroke / 2 - i * (Stroke + 5);
            if (r <= 4) break;
            dc.DrawEllipse(null, Track, c, r, r);
            var p = Math.Clamp(values[i], 0, 1);
            if (p <= 0.002) continue;
            if (p >= 0.9995)
            {
                dc.DrawEllipse(null, Arcs[i], c, r, r);
                continue;
            }
            var a = p * 2 * Math.PI;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
                ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, Arcs[i], geo);
        }
    }
}

/// <summary>
/// The pay counter: each digit is a clipped column of 0–9 that rolls to its
/// value (the odometer). Commas and the ₪ sign sit still. Changing the
/// number of digits rebuilds the columns — rare (₪9,999 → ₪10,000).
/// </summary>
sealed class Odometer : StackPanel
{
    readonly double _size;
    readonly Brush _brush;
    readonly List<(TranslateTransform Move, int Digit)> _columns = new();
    string _shape = "";
    int _value = -1;

    public Odometer(double size, Brush brush)
    {
        _size = size;
        _brush = brush;
        Orientation = Orientation.Horizontal;
        FlowDirection = FlowDirection.LeftToRight;
    }

    double LineHeight => Math.Round(_size * 1.18);

    public int Value => _value;

    public void Set(int value, bool roll)
    {
        var text = He.N(Math.Max(0, value));
        var shape = new string(text.Select(ch => char.IsDigit(ch) ? '0' : ch).ToArray());
        if (shape != _shape) Build(shape);
        var d = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsDigit(text[i])) continue;
            var digit = text[i] - '0';
            var (move, _) = _columns[d];
            var to = -digit * LineHeight;
            if (roll) move.BeginAnimation(TranslateTransform.YProperty, Feel.To(to, 900 + d * 90, Feel.Expo, d * 40));
            else
            {
                move.BeginAnimation(TranslateTransform.YProperty, null);
                move.Y = to;
            }
            _columns[d] = (move, digit);
            d++;
        }
        _value = value;
    }

    void Build(string shape)
    {
        _shape = shape;
        Children.Clear();
        _columns.Clear();
        foreach (var ch in shape)
        {
            if (ch != '0')
            {
                var sep = Kit.Num(ch.ToString(), _size, _brush, FontWeights.Light);
                sep.Height = LineHeight;
                Children.Add(sep);
                continue;
            }
            var strip = new StackPanel();
            for (var n = 0; n <= 9; n++)
            {
                var t = Kit.Num(n.ToString(), _size, _brush, FontWeights.Light);
                t.Height = LineHeight;
                t.TextAlignment = TextAlignment.Center;
                strip.Children.Add(t);
            }
            var move = new TranslateTransform();
            strip.RenderTransform = move;
            var window = new Canvas { Height = LineHeight, Width = Math.Round(_size * 0.6), ClipToBounds = true };
            window.Children.Add(strip);
            Children.Add(window);
            _columns.Add((move, 0));
        }
    }
}
