using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Palon.UI;

/// <summary>
/// The dock's local motion + color constants. Kept here (not in Motion /
/// Theme) on purpose: other work is adding silver tokens there in parallel;
/// fold these in once both land.
/// </summary>
static class DockMotion
{
    public const int Morph = 420;       // pill width/height/radius morph
    public const int MorphQuick = 260;  // small resizes inside a state (label swaps, rows appear)
    public const int FadeOut = 110;     // outgoing content
    public const int FadeIn = 220;      // incoming content, starts as the exit lands
    public const int Press = 90;        // press dip
    public const int Release = 380;     // spring back
    public const double PressScale = 0.965;
    public const int PopCheck = 450;
    public const int AfterCallIdleMs = 20_000; // after-call card tucks away after this long untouched
    public const int ConfirmHoldMs = 2600;     // "done ✓" row lingers, then the card collapses

    /// <summary>Exponential ease-out — fast start, long soft landing (≈ cubic-bezier(.16,1,.3,1)).</summary>
    public static readonly IEasingFunction Expo = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 });
    /// <summary>The release spring — a whisper of overshoot.</summary>
    public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });

    public static DoubleAnimation To(double to, int ms, IEasingFunction? ease = null) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Expo };

    public static DoubleAnimation FromTo(double from, double to, int ms, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Expo };

    static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }
}

/// <summary>Semantic dock colors (design: green done, red overdue, amber on-call) and the silver ramp.</summary>
static class DockPalette
{
    public static readonly Brush Done = Frozen("#FF32D74B");
    public static readonly Brush Overdue = Frozen("#FFFF453A");
    public static readonly Brush OverdueText = Frozen("#FFFF6961");
    public static readonly Brush OnCall = Frozen("#FFFF9F0A");
    public static readonly Brush Primary = Frozen("#FFF5F5F7");      // silver-white primary button
    public static readonly Brush PrimaryHover = Frozen("#FFFFFFFF");
    public static readonly Brush OnPrimary = Frozen("#FF000000");
    public static readonly Brush Secondary = Frozen("#17FFFFFF");    // rgba(255,255,255,.09)
    public static readonly Brush SecondaryHover = Frozen("#29FFFFFF");
    public static readonly Brush Ghost = Brushes.Transparent;
    public static readonly Brush GhostHover = Frozen("#14FFFFFF");
    public static readonly Brush Well = Frozen("#0AFFFFFF");         // summary well, rgba(255,255,255,.04)
    public static readonly Brush HeardFill = Frozen("#24D8D8DD");    // silver accent @ .14
    public static readonly Brush HeardStroke = Frozen("#73D8D8DD");  // silver accent @ .45
    public static readonly Brush HeardText = Frozen("#FFD8D8DD");
    public static readonly Brush DoneWell = Frozen("#1A32D74B");     // rgba(50,215,75,.1)
    public static readonly Brush Muted = Frozen("#FFA1A1A6");
    public static readonly Brush Faint = Frozen("#FF86868B");
    public static readonly Brush Body = Frozen("#FFD1D1D6");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>Small builders for the dock's buttons, chips, icons and the pop check.</summary>
static class DockKit
{
    // 14×14 stroke icons (viewBox 0 0 14 14), drawn LTR — hosts pin FlowDirection.
    public static readonly Geometry IconMic = Geometry.Parse("M7,1.5 C8.1,1.5 8.8,2.3 8.8,3.3 V6.9 C8.8,7.9 8.1,8.7 7,8.7 C5.9,8.7 5.2,7.9 5.2,6.9 V3.3 C5.2,2.3 5.9,1.5 7,1.5 Z M3,6.6 C3,9 4.8,10.7 7,10.7 C9.2,10.7 11,9 11,6.6 M7,10.7 V12.6");
    public static readonly Geometry IconChat = Geometry.Parse("M2,3.8 C2,2.8 2.8,2 3.8,2 H10.2 C11.2,2 12,2.8 12,3.8 V8.2 C12,9.2 11.2,10 10.2,10 H6 L3.4,12.2 V10 H3.8 C2.8,10 2,9.2 2,8.2 Z");
    public static readonly Geometry IconTerminal = Geometry.Parse("M3,4 L6,7 L3,10 M7.5,10.5 H11");
    public static readonly Geometry IconClock = Geometry.Parse("M7,1.8 A5.2,5.2 0 1 1 6.99,1.8 Z M7,4.2 V7 L8.9,8.3");
    public static readonly Geometry IconNote = Geometry.Parse("M3.5,1.8 H8.5 L11,4.3 V12.2 H3.5 Z M5.5,7 H9 M5.5,9.4 H9");
    public static readonly Geometry IconMore = Geometry.Parse("M3,7 H3.01 M7,7 H7.01 M11,7 H11.01");
    public static readonly Geometry IconClose = Geometry.Parse("M4,4 L10,10 M10,4 L4,10");
    public static readonly Geometry IconCopy = Geometry.Parse("M5,5 H11 V12 H5 Z M3,9 V2.5 H9");
    public static readonly Geometry IconCheck = Geometry.Parse("M3.2,7.3 L5.9,9.9 L10.8,4.4");

    public static ShapePath Icon(Geometry data, double size = 14, Brush? stroke = null, double thickness = 1.6)
    {
        var path = new ShapePath
        {
            Data = data,
            Width = 14,
            Height = 14,
            Stretch = Stretch.None,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight, // glyphs never mirror under RTL
            IsHitTestVisible = false,
        };
        if (size != 14) path.LayoutTransform = new ScaleTransform(size / 14, size / 14);
        if (stroke is not null) path.Stroke = stroke;
        else path.SetResourceReference(Shape.StrokeProperty, "TextPrimaryBrush");
        return path;
    }

    public static TextBlock Text(string text, double size, Brush? brush = null, FontWeight? weight = null)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (brush is not null) tb.Foreground = brush;
        else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        AlignFlow(tb);
        return tb;
    }

    /// <summary>Latin-only runs (numbers, English toasts) read LTR inside the RTL dock.</summary>
    public static void AlignFlow(TextBlock tb) =>
        tb.FlowDirection = Bidi.HasRtl(tb.Text ?? "") ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Tabular digits for timers and counts.</summary>
    public static TextBlock Numeric(string text, double size, Brush? brush = null)
    {
        var tb = Text(text, size, brush);
        tb.FlowDirection = FlowDirection.LeftToRight;
        System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    public enum Kind { Primary, Secondary, Ghost }

    /// <summary>A 34px capsule button. Claims its press (so the pill's drag
    /// capture never eats the click), dips to 0.965 and springs back.</summary>
    public static Border Button(string label, Kind kind, Action? onClick, Geometry? icon = null)
    {
        var fg = kind == Kind.Primary ? DockPalette.OnPrimary : kind == Kind.Ghost ? DockPalette.Muted : null;
        var text = Text(label, Font.Lead, fg, kind == Kind.Primary ? FontWeights.SemiBold : FontWeights.Medium);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            row.Children.Add(Icon(icon, 14, fg));
            text.Margin = new Thickness(6, 0, 0, 0);
        }
        row.Children.Add(text);
        var button = new Border
        {
            Height = kind == Kind.Ghost ? 28 : 34,
            CornerRadius = new CornerRadius(kind == Kind.Ghost ? 14 : 17),
            Padding = new Thickness(kind == Kind.Ghost ? 10 : 14, 0, kind == Kind.Ghost ? 10 : 14, 1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
            Tag = text,
        };
        var (rest, hover) = kind switch
        {
            Kind.Primary => (DockPalette.Primary, DockPalette.PrimaryHover),
            Kind.Secondary => (DockPalette.Secondary, DockPalette.SecondaryHover),
            _ => (DockPalette.Ghost, DockPalette.GhostHover),
        };
        button.Background = rest;
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = rest;
        if (onClick is not null) Clickable(button, onClick); // null: the caller wires Clickable itself
        return button;
    }

    /// <summary>The label TextBlock of a <see cref="Button"/>.</summary>
    public static TextBlock LabelOf(Border button) => (TextBlock)button.Tag;

    /// <summary>A round icon-only ghost button (✕).</summary>
    public static Border IconButton(Geometry icon, string tooltip, Action onClick)
    {
        var button = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = DockPalette.Ghost,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Icon(icon, 14, DockPalette.Faint),
        };
        ((FrameworkElement)button.Child).HorizontalAlignment = HorizontalAlignment.Center;
        button.MouseEnter += (_, _) => button.Background = DockPalette.GhostHover;
        button.MouseLeave += (_, _) => button.Background = DockPalette.Ghost;
        Clickable(button, onClick);
        return button;
    }

    /// <summary>
    /// Press-to-click wiring: the press is marked Handled (the pill's drag
    /// CaptureMouse would otherwise swallow the release — AUDIT #1), the
    /// element dips and springs, and the click fires on release only if the
    /// pointer is still over it.
    /// </summary>
    public static void Clickable(FrameworkElement element, Action onClick)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var pressed = false;
        element.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pressed = true;
            Scale(scale, DockMotion.PressScale, DockMotion.Press, DockMotion.Expo);
        };
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Scale(scale, 1, DockMotion.Release, DockMotion.Spring);
            if (!pressed) return;
            pressed = false;
            onClick();
        };
        element.MouseLeave += (_, _) =>
        {
            pressed = false;
            Scale(scale, 1, DockMotion.Release, DockMotion.Expo);
        };
    }

    static void Scale(ScaleTransform scale, double to, int ms, IEasingFunction ease)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, DockMotion.To(to, ms, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, DockMotion.To(to, ms, ease));
    }

    /// <summary>A 24px round badge with a check — pops .4 → 1.2 → 1 when shown.</summary>
    public static Border CheckBadge(Brush fill)
    {
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = fill,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = Icon(IconCheck, 14, DockPalette.OnPrimary, 2),
        };
        ((FrameworkElement)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        return badge;
    }

    public static void Pop(FrameworkElement element)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(DockMotion.PopCheck) };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.4, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.55), new CubicEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Content crossfade in: opacity 0→1 plus a 6px rise, a beat after the exit.</summary>
    public static void RiseIn(UIElement element, int delayMs = DockMotion.FadeOut)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        var fade = DockMotion.FromTo(0, 1, DockMotion.FadeIn);
        fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        var rise = new TranslateTransform(0, 6);
        element.RenderTransform = rise;
        var up = DockMotion.FromTo(6, 0, DockMotion.Morph);
        up.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        rise.BeginAnimation(TranslateTransform.YProperty, up);
    }

    /// <summary>A label swap ("העתק סיכום" → "הועתק ✓") that reverts after a
    /// beat. One timer per label: rapid clicks restart it (AUDIT #9), and the
    /// button's width is pinned so the row never jumps mid-flash.</summary>
    public static Action<string> Flasher(Border button, string original, int ms = Motion.Revert)
    {
        var label = LabelOf(button);
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            label.Text = original;
            AlignFlow(label);
            button.MinWidth = 0;
        };
        return message =>
        {
            if (!revert.IsEnabled) button.MinWidth = button.ActualWidth;
            label.Text = message;
            AlignFlow(label);
            revert.Stop();
            revert.Start();
        };
    }
}
