using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Palon.UI;

enum PillKind { Primary, Secondary, Ghost, Accent }

/// <summary>
/// The Terminal's building blocks, all in code: glass cards, pill buttons,
/// fields, segmented bars, icons, and the handful of micro-interactions
/// (press spring, hover fade, card lift, staggered rise). Everything moves
/// on Feel's curves and only in response to the user.
/// </summary>
static class Kit
{
    /// <summary>True while a screen renders for navigation: one-shot entry
    /// motion (count-ups, ring sweeps, bar growth) plays only then — a data
    /// refresh lands values in place.</summary>
    public static bool Entering { get; set; }

    // ---- text -------------------------------------------------------------------

    public static TextBlock T(string text, double size, Brush brush, FontWeight? weight = null, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontFamily = Font.Family,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        TextOptions.SetTextFormattingMode(tb, size >= 20 ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        return tb;
    }

    /// <summary>A number: tabular figures, always laid out left-to-right so
    /// "$10,000" and "12:30" never get reordered by the RTL page.</summary>
    public static TextBlock Num(string text, double size, Brush brush, FontWeight? weight = null)
    {
        var tb = T(text, size, brush, weight);
        tb.FlowDirection = FlowDirection.LeftToRight;
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    public static TextBlock Mono(string text, double size, Brush brush)
    {
        var tb = Num(text, size, brush);
        tb.FontFamily = Font.Mono;
        return tb;
    }

    /// <summary>Direction follows the content (a Latin client name in an RTL list).</summary>
    public static TextBlock Auto(TextBlock tb)
    {
        if (!Bidi.HasRtl(tb.Text) && tb.Text.Length > 0)
        {
            tb.FlowDirection = FlowDirection.LeftToRight;
            tb.TextAlignment = TextAlignment.Right;
        }
        return tb;
    }

    public static StackPanel Row(double gap, params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0)
                fe.Margin = new Thickness(fe.Margin.Left + gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            sp.Children.Add(children[i]);
        }
        return sp;
    }

    public static StackPanel Col(double gap, params UIElement[] children)
    {
        var sp = new StackPanel();
        for (var i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0)
                fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
            sp.Children.Add(children[i]);
        }
        return sp;
    }

    /// <summary>A 3-slot row: start content, a star spacer, end content.</summary>
    public static Grid Bar(UIElement start, params UIElement[] end)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(start);
        var col = 1;
        foreach (var e in end)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(e, col++);
            if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(e);
        }
        if (start is FrameworkElement s) s.VerticalAlignment = VerticalAlignment.Center;
        return g;
    }

    // ---- transforms ---------------------------------------------------------------

    sealed class Xf
    {
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Move = new();
    }

    static readonly DependencyProperty XfProperty =
        DependencyProperty.RegisterAttached("TerminalXf", typeof(Xf), typeof(Kit));

    static Xf Transforms(FrameworkElement el)
    {
        if (el.GetValue(XfProperty) is Xf existing) return existing;
        var xf = new Xf();
        el.RenderTransform = new TransformGroup { Children = { xf.Scale, xf.Move } };
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.SetValue(XfProperty, xf);
        return xf;
    }

    static void ScaleTo(ScaleTransform s, double to, int ms, IEasingFunction ease)
    {
        s.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(s.ScaleX, to, ms, ease));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.FromTo(s.ScaleY, to, ms, ease));
    }

    /// <summary>Press: dip to 0.965 fast, spring back with a whisper of overshoot.</summary>
    public static void Press(FrameworkElement el, double depth = Feel.Press)
    {
        var xf = Transforms(el);
        el.PreviewMouseLeftButtonDown += (_, _) => ScaleTo(xf.Scale, depth, Feel.PressDown, Feel.Expo);
        el.PreviewMouseLeftButtonUp += (_, _) => ScaleTo(xf.Scale, 1, Feel.PressUp, Feel.Spring);
        el.MouseLeave += (_, _) => { if (xf.Scale.ScaleX < 1) ScaleTo(xf.Scale, 1, Feel.PressUp, Feel.Spring); };
    }

    /// <summary>Card lift: 2px up and a brighter rim while hovered.</summary>
    public static void Lift(Border card)
    {
        var xf = Transforms(card);
        Brush? rest = null;
        card.MouseEnter += (_, _) =>
        {
            xf.Move.BeginAnimation(TranslateTransform.YProperty, Feel.To(-2, Feel.Lift));
            rest = card.BorderBrush;
            if (ReferenceEquals(rest, Tone.GlassRim)) card.BorderBrush = Tone.GlassStrokeHover;
        };
        card.MouseLeave += (_, _) =>
        {
            xf.Move.BeginAnimation(TranslateTransform.YProperty, Feel.To(0, Feel.Lift));
            if (ReferenceEquals(card.BorderBrush, Tone.GlassStrokeHover)) card.BorderBrush = rest ?? Tone.GlassRim;
        };
    }

    /// <summary>Hover magnify with lift — the dock icon feel.</summary>
    public static void Magnify(FrameworkElement el, double scale = 1.08, double rise = -4)
    {
        var xf = Transforms(el);
        el.RenderTransformOrigin = new Point(0.5, 1);
        el.MouseEnter += (_, _) =>
        {
            ScaleTo(xf.Scale, scale, 500, Feel.Spring);
            xf.Move.BeginAnimation(TranslateTransform.YProperty, Feel.To(rise, 500, Feel.Spring));
        };
        el.MouseLeave += (_, _) =>
        {
            ScaleTo(xf.Scale, 1, 500, Feel.Expo);
            xf.Move.BeginAnimation(TranslateTransform.YProperty, Feel.To(0, 500));
        };
    }

    /// <summary>
    /// A background that fades between two colors on hover. The brush is
    /// the element's own (animated, so unfrozen); pass the frozen palette
    /// brushes and only their colors are used.
    /// </summary>
    public static SolidColorBrush HoverFill(Border el, SolidColorBrush normal, SolidColorBrush hover)
    {
        var brush = new SolidColorBrush(normal.Color);
        el.Background = brush;
        el.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(hover.Color, TimeSpan.FromMilliseconds(Feel.Hover)) { EasingFunction = Feel.Expo });
        el.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(normal.Color, TimeSpan.FromMilliseconds(Feel.Hover)) { EasingFunction = Feel.Expo });
        return brush;
    }

    /// <summary>Opacity fade in/out of hover-revealed controls on a row.</summary>
    public static void Reveal(FrameworkElement row, params FrameworkElement[] revealed)
    {
        foreach (var r in revealed) r.Opacity = 0;
        row.MouseEnter += (_, _) => { foreach (var r in revealed) r.BeginAnimation(UIElement.OpacityProperty, Feel.To(1, 200)); };
        row.MouseLeave += (_, _) =>
        {
            if (row.IsKeyboardFocusWithin) return;
            foreach (var r in revealed) r.BeginAnimation(UIElement.OpacityProperty, Feel.To(0, 200));
        };
        row.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) foreach (var r in revealed) r.BeginAnimation(UIElement.OpacityProperty, Feel.To(1, 200));
        };
    }

    /// <summary>The screen entrance: each element rises 8px and fades in,
    /// staggered 30ms, done well inside 0.55s. Transform-only, no layout.</summary>
    public static void Rise(IEnumerable<FrameworkElement> elements, int startDelay = 0)
    {
        var i = 0;
        foreach (var el in elements)
        {
            var xf = Transforms(el);
            var delay = startDelay + Math.Min(i++, 6) * Feel.Step;
            el.Opacity = 0;
            el.BeginAnimation(UIElement.OpacityProperty, Feel.FromTo(0, 1, 420, Feel.Expo, delay));
            xf.Move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(8, 0, Feel.Entrance, Feel.Expo, delay));
        }
    }

    /// <summary>The check/tick pop: 0.4 → 1.2 → 1.</summary>
    public static void Pop(FrameworkElement el)
    {
        var xf = Transforms(el);
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(450) };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.4, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.55), Feel.Expo));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), Motion.Out));
        xf.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        xf.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // ---- surfaces -----------------------------------------------------------------

    public static Border Glass(double radius = Display.CardRadius, Thickness? padding = null, bool lift = false)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = Tone.Glass,
            BorderBrush = Tone.GlassRim,
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(26),
            SnapsToDevicePixels = true,
        };
        if (lift) Lift(card);
        return card;
    }

    public static Border DeepGlass(double radius, Thickness padding) => new()
    {
        CornerRadius = new CornerRadius(radius),
        Background = Tone.GlassDeep,
        BorderBrush = Tone.GlassDeepRim,
        BorderThickness = new Thickness(1),
        Padding = padding,
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 60, ShadowDepth = 20, Direction = 270, Opacity = 0.6,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
        },
    };

    /// <summary>A list row: rounded, transparent, a whisper of fill on hover.</summary>
    public static Border ListRow(UIElement child, Thickness? padding = null)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(Display.RowRadius),
            Padding = padding ?? new Thickness(12, 11, 12, 11),
            Child = child,
        };
        HoverFill(row, Transparent, Tone.RowHover);
        return row;
    }

    static readonly SolidColorBrush Transparent = Tone.B("#00FFFFFF");

    public static TextBlock SectionTitle(string text, double size = Display.Section) =>
        T(text, size, Tone.Text, FontWeights.SemiBold);

    public static Border Dot(Brush fill, double size = 8) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = fill,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- controls -----------------------------------------------------------------

    /// <summary>
    /// A pill button. Click fires on release inside (like a real button),
    /// Enter/Space when focused. Primary = silver fill on black text.
    /// </summary>
    public static Border Pill(string label, PillKind kind, Action onClick, double height = 38, double fontSize = 13.5, UIElement? content = null)
    {
        var (normal, hover, fg) = kind switch
        {
            PillKind.Primary => (Tone.Primary, Tone.PrimaryHover, (Brush)Tone.OnPrimary),
            PillKind.Secondary => (Tone.Fill, Tone.FillHover, Tone.Text),
            PillKind.Accent => (Tone.AccentSoft, Tone.AccentLine, Tone.Accent),
            _ => (Transparent, Tone.Track, Tone.Accent),
        };
        var text = content ?? T(label, fontSize, fg, kind == PillKind.Primary ? FontWeights.SemiBold : FontWeights.Medium);
        if (text is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            fe.HorizontalAlignment = HorizontalAlignment.Center;
        }
        var pill = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Padding = new Thickness(kind == PillKind.Primary ? 20 : 16, 0, kind == PillKind.Primary ? 20 : 16, 0),
            Child = text,
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
        };
        HoverFill(pill, normal, hover);
        Clickable(pill, onClick);
        Press(pill);
        return pill;
    }

    /// <summary>A round icon button — close, minimize, stepper.</summary>
    public static Border IconButton(string pathData, double size, Action onClick, PillKind kind = PillKind.Ghost, double icon = 16, Brush? fg = null)
    {
        var (normal, hover) = kind == PillKind.Secondary ? (Tone.Fill, Tone.FillHover) : (Transparent, Tone.Fill);
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Child = Icon(pathData, icon, fg ?? Tone.MutedSoft),
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
        };
        HoverFill(b, normal, hover);
        Clickable(b, onClick);
        Press(b);
        return b;
    }

    public static void Clickable(UIElement el, Action onClick)
    {
        var armed = false;
        el.MouseLeftButtonDown += (_, e) => { armed = true; e.Handled = true; };
        el.MouseLeave += (_, _) => armed = false;
        el.MouseLeftButtonUp += (_, e) =>
        {
            if (!armed) return;
            armed = false;
            e.Handled = true;
            onClick();
        };
        el.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
    }

    /// <summary>A stroked 24×24 icon. Always LTR so it isn't mirrored.</summary>
    public static FrameworkElement Icon(string pathData, double size, Brush stroke, double thickness = 1.7)
    {
        var path = new Path
        {
            Data = Geo(pathData),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 24, Height = 24,
        };
        return new Viewbox
        {
            Width = size, Height = size, Child = path, FlowDirection = FlowDirection.LeftToRight,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    static readonly Dictionary<string, Geometry> Geometries = new();

    static Geometry Geo(string data)
    {
        if (Geometries.TryGetValue(data, out var g)) return g;
        g = Geometry.Parse(data);
        g.Freeze();
        Geometries[data] = g;
        return g;
    }

    /// <summary>A chromeless text box with its placeholder, no frame.</summary>
    public static (Grid Host, TextBox Box) Input(string placeholder, double fontSize, bool ltr = false, bool multiline = false)
    {
        var box = new TextBox
        {
            FontSize = fontSize,
            FontFamily = Font.Family,
            Foreground = Tone.Text,
            CaretBrush = Tone.Text,
            SelectionBrush = Tone.AccentLine,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalAlignment = multiline ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            Template = BareTextBoxTemplate(multiline),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (ltr) box.FlowDirection = FlowDirection.LeftToRight;
        var hint = T(placeholder, fontSize, Tone.Faint);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (ltr) hint.FlowDirection = FlowDirection.LeftToRight;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var host = new Grid();
        host.Children.Add(hint);
        host.Children.Add(box);
        return (host, box);
    }

    static void FocusRing(Border frame, TextBox box)
    {
        frame.MouseLeftButtonDown += (_, _) => box.Focus();
        box.GotKeyboardFocus += (_, _) => frame.BorderBrush = Tone.AccentLine;
        box.LostKeyboardFocus += (_, _) => frame.BorderBrush = Tone.Hairline;
    }

    /// <summary>A text field on a fill ground, with placeholder and a focus
    /// ring that brightens the rim. Returns the frame and the box.</summary>
    public static (Border Frame, TextBox Box) Field(string placeholder, double fontSize = 15, double height = 44,
        double radius = 22, bool ltr = false, UIElement? leading = null, Brush? background = null)
    {
        var (host, box) = Input(placeholder, fontSize, ltr);
        UIElement content = host;
        if (leading is not null)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (leading is FrameworkElement lf) lf.Margin = new Thickness(0, 0, 10, 0);
            g.Children.Add(leading);
            Grid.SetColumn(host, 1);
            g.Children.Add(host);
            content = g;
        }
        var frame = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background = background ?? Tone.FillSoft,
            BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 0, 16, 0),
            Child = content,
            Cursor = Cursors.IBeam,
        };
        FocusRing(frame, box);
        return (frame, box);
    }

    static readonly ControlTemplate?[] _bare = new ControlTemplate?[2];

    /// <summary>TextBox with no chrome: just the scrolling host.</summary>
    public static ControlTemplate BareTextBoxTemplate(bool multiline = false)
    {
        var i = multiline ? 1 : 0;
        if (_bare[i] is { } cached) return cached;
        var t = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
        host.SetValue(FrameworkElement.FocusVisualStyleProperty, null);
        t.VisualTree = host;
        t.Seal();
        _bare[i] = t;
        return t;
    }

    /// <summary>A labelled field in a sheet: small caption above the input.</summary>
    public static (Border Frame, TextBox Box) LabeledField(string label, string placeholder, double fontSize = 16,
        bool ltr = false, bool multiline = false, double? height = null)
    {
        var (host, box) = Input(placeholder, fontSize, ltr, multiline);
        host.Margin = new Thickness(0, 4, 0, 0);
        if (height is double h) host.Height = h;
        var col = new StackPanel();
        col.Children.Add(T(label, 12, Tone.Muted));
        col.Children.Add(host);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = Tone.FillSoft,
            BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = col,
            Cursor = Cursors.IBeam,
        };
        FocusRing(frame, box);
        return (frame, box);
    }

    /// <summary>A segmented control. Stretch = equal-width options.</summary>
    public static Border Segmented(IReadOnlyList<string> options, int selected, Action<int> onPick,
        bool stretch = false, double height = 32, double fontSize = 13.5)
    {
        var buttons = new List<Border>();
        Panel panel = stretch ? new UniformGrid { Columns = options.Count } : new StackPanel { Orientation = Orientation.Horizontal };
        void Paint(int sel)
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var on = i == sel;
                var b = buttons[i];
                ((SolidColorBrush)b.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(on ? Tone.FillSelected.Color : Transparent.Color, TimeSpan.FromMilliseconds(250)) { EasingFunction = Feel.Expo });
                ((TextBlock)b.Child).Foreground = on ? Tone.Text : Tone.MutedSoft;
            }
        }
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var b = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(height * 0.34),
                Padding = new Thickness(16, 0, 16, 0),
                Background = new SolidColorBrush(i == selected ? Tone.FillSelected.Color : Transparent.Color),
                Child = new TextBlock
                {
                    Text = options[i], FontSize = fontSize, FontWeight = FontWeights.Medium, FontFamily = Font.Family,
                    Foreground = i == selected ? Tone.Text : Tone.MutedSoft,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                },
                Cursor = Cursors.Hand,
                Focusable = true,
                FocusVisualStyle = null,
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
            };
            Clickable(b, () =>
            {
                Paint(index);
                onPick(index);
            });
            Press(b);
            buttons.Add(b);
            panel.Children.Add(b);
        }
        return new Border
        {
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(height * 0.34 + 3),
            Background = Tone.FillSoft,
            Child = panel,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };
    }

    /// <summary>A thin horizontal bar that grows from the start edge.</summary>
    public static Border HBar(double fraction, Brush fill, int delay, double height = 5)
    {
        var inner = new Border
        {
            Background = fill,
            CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        var track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Background = Tone.Track, Child = inner };
        track.SizeChanged += (_, e) => inner.Width = Math.Max(0, Math.Min(1, fraction)) * e.NewSize.Width;
        if (Entering)
        {
            var scale = new ScaleTransform(0, 1);
            inner.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(0, 1, 1000, Feel.Expo, delay));
        }
        return track;
    }

    /// <summary>Counts a number up from zero over ~1.1s (one-shot, on entry).</summary>
    public static void CountUp(TextBlock tb, double target, Func<double, string> format, int ms = Feel.Count)
    {
        tb.Text = format(target);
        if (target == 0 || !Entering) return;
        tb.Text = format(0);
        var clock = Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var p = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            var e = 1 - Math.Pow(1 - p, 3);
            tb.Text = format(target * e);
            if (p >= 1) CompositionTarget.Rendering -= tick;
        };
        CompositionTarget.Rendering += tick;
    }

    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal copy failed: {ex.Message}");
        }
    }
}

/// <summary>Stroked icon paths on a 24px grid (from the approved design).</summary>
static class Icons
{
    public const string Today = "M8,12 A4,4 0 1 1 16,12 A4,4 0 1 1 8,12 M12,2.5 V4.5 M12,19.5 V21.5 M4.9,4.9 L6.3,6.3 M17.7,17.7 L19.1,19.1 M2.5,12 H4.5 M19.5,12 H21.5 M4.9,19.1 L6.3,17.7 M17.7,6.3 L19.1,4.9";
    public const string Callbacks = "M5,4 H8.5 L10.5,9 L8,10.5 A11,11 0 0 0 13.5,16 L15,13.5 L20,15.5 V19 A2,2 0 0 1 18,21 A16,16 0 0 1 3,6 A2,2 0 0 1 5,4 M15,3 H21 V9 M21,3 L15,9";
    public const string Month = "M4,20.5 H20 M5,11 H8.2 V17.5 H5 Z M10.4,5.5 H13.6 V17.5 H10.4 Z M15.8,8.5 H19 V17.5 H15.8 Z";
    public const string Clients = "M5.5,8 A3.5,3.5 0 1 1 12.5,8 A3.5,3.5 0 1 1 5.5,8 M2.5,20 A6.5,6.5 0 0 1 15.5,20 M16,4.5 A3.5,3.5 0 0 1 16,11.5 M18,14.5 A6.5,6.5 0 0 1 21.5,20";
    public const string Templates = "M14,3 H7 A2,2 0 0 0 5,5 V19 A2,2 0 0 0 7,21 H17 A2,2 0 0 0 19,19 V8 Z M14,3 V8 H19 M9,13 H15 M9,17 H13";
    public const string Gear = "M9,12 A3,3 0 1 1 15,12 A3,3 0 1 1 9,12 M12,2.8 V5.2 M12,18.8 V21.2 M2.8,12 H5.2 M18.8,12 H21.2 M5.5,5.5 L7.2,7.2 M16.8,16.8 L18.5,18.5 M5.5,18.5 L7.2,16.8 M16.8,7.2 L18.5,5.5";
    public const string Plus = "M12,5 V19 M5,12 H19";
    public const string Minus = "M5,12 H19";
    public const string Close = "M6,6 L18,18 M18,6 L6,18";
    public const string Check = "M5,12.5 L9.5,17 L19,7.5";
    public const string Search = "M4.5,11 A6.5,6.5 0 1 1 17.5,11 A6.5,6.5 0 1 1 4.5,11 M20,20 L15.8,15.8";
    public const string Send = "M12,19 V5 M6,11 L12,5 L18,11";
    public const string Bell = "M6,16 V11 A6,6 0 0 1 18,11 V16 L19.5,18 H4.5 Z M10,20.5 A2,2 0 0 0 14,20.5";
    public const string Minimize = "M6,12 H18";
    public const string Maximize = "M6,6 H18 V18 H6 Z";
    public const string Edit = "M4,20 H8 L19,9 A2.1,2.1 0 0 0 15,5 L4,16 Z";
    public const string Coach = "M4,19 H20 M6,19 V14 M10,19 V10 M14,19 V12 M18,19 V6 M5,10 L10,6 L14,8 L19,3";
    public const string Memory = "M9,4.5 A3,3 0 0 0 6,7.5 A3,3 0 0 0 4,10.5 A3,3 0 0 0 5,13 A3,3 0 0 0 6,17 A3,3 0 0 0 12,18.5 V5.5 A3,3 0 0 0 9,4.5 Z M15,4.5 A3,3 0 0 1 18,7.5 A3,3 0 0 1 20,10.5 A3,3 0 0 1 19,13 A3,3 0 0 1 18,17 A3,3 0 0 1 12,18.5 M12,5.5 A3,3 0 0 1 15,4.5";
}
