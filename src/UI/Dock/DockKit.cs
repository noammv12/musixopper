using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace Palon.UI;

/// <summary>
/// The dock's motion constants. Morphs ride a real spring (the Dock2 design's
/// 1 − e^(−8t)·cos(10.5t) over 1.1 s); content crossfades; presses dip and
/// spring back. Reduced motion (Windows "show animations" off) makes every
/// duration zero — states still change, nothing travels.
/// </summary>
static class DockMotion
{
    public const int Morph = 1100;      // pill width/height/radius spring
    public const int MorphQuick = 420;  // small resizes inside a state (label swaps)
    public const int FadeOut = 110;     // outgoing content
    public const int FadeIn = 280;      // incoming content, starts as the exit lands
    public const int Press = 110;       // press dip
    public const int Release = 450;     // spring back
    public const double PressScale = 0.93;
    public const int PopCheck = 600;
    public const int AfterCallIdleMs = 20_000; // an untouched after-call card tucks away
    public const int BookedHoldMs = 1600;      // "booked ✓" holds, then the card tucks
    public const int DoneHoldMs = 1700;
    public const int FlashMs = 2600;           // a rest-state micro message
    public const int FlashActionMs = 5000;     // …with an action (undo, open)

    public static bool Reduced => !SystemParameters.ClientAreaAnimation;

    /// <summary>Exponential ease-out — fast start, long soft landing.</summary>
    public static readonly IEasingFunction Expo = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 });
    /// <summary>The release spring — a whisper of overshoot.</summary>
    public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
    /// <summary>The morph spring.</summary>
    public static readonly IEasingFunction Shape = Freeze(new DockSpringEase { EasingMode = EasingMode.EaseIn });

    static int D(int ms) => Reduced ? 0 : ms;

    public static DoubleAnimation To(double to, int ms, IEasingFunction? ease = null) =>
        new(to, TimeSpan.FromMilliseconds(D(ms))) { EasingFunction = ease ?? Expo };

    public static DoubleAnimation FromTo(double from, double to, int ms, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(D(ms))) { EasingFunction = ease ?? Expo };

    public static TimeSpan Delay(int ms) => TimeSpan.FromMilliseconds(D(ms));

    static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }
}

/// <summary>The design's damped spring, normalized so t = 1 is fully settled (1.1 s).</summary>
sealed class DockSpringEase : EasingFunctionBase
{
    protected override double EaseInCore(double t)
    {
        if (t >= 1) return 1;
        var x = t * 1.1;
        return 1 - Math.Exp(-8 * x) * Math.Cos(10.5 * x);
    }

    protected override Freezable CreateInstanceCore() => new DockSpringEase();
}

/// <summary>Dock colors: black → silver glass, silver primary, amber on a call.</summary>
static class DockPalette
{
    public static readonly Brush Done = Frozen("#FF5CE07A");
    public static readonly Brush Overdue = Frozen("#FFFF453A");
    public static readonly Brush OverdueText = Frozen("#FFFF6961");
    public static readonly Brush OnCall = Frozen("#FFFFB020");
    public static readonly Brush OnCallSoft = Frozen("#26FFB020");
    public static readonly Brush OnPrimary = Frozen("#FF0A0A0C");
    public static readonly Brush Secondary = Frozen("#0FFFFFFF");      // rgba(255,255,255,.06)
    public static readonly Brush SecondaryHover = Frozen("#1FFFFFFF"); // .12
    public static readonly Brush SecondaryStroke = Frozen("#17FFFFFF");
    public static readonly Brush SecondaryText = Frozen("#FFE5E5EA");
    public static readonly Brush Ghost = Brushes.Transparent;
    public static readonly Brush GhostHover = Frozen("#12FFFFFF");
    public static readonly Brush DoneWell = Frozen("#1A5CE07A");
    public static readonly Brush Muted = Frozen("#FFA1A1A6");
    public static readonly Brush Faint = Frozen("#FF8E8E93");
    public static readonly Brush Text = Frozen("#FFF5F5F7");
    public static readonly Brush Stroke = Frozen("#1AFFFFFF");
    public static readonly Brush CallStroke = Frozen("#52FFB020");
    public static readonly Brush NudgeTag = Frozen("#FFB7C8FF");
    public static readonly Brush NudgeTagFill = Frozen("#24A0BEFF");
    public static readonly Brush RingTrack = Frozen("#1FFFFFFF");
    public static readonly Brush[] Rings = { Frozen("#FFB7C8E2"), Frozen("#FFA6D6BE"), Frozen("#FFDCC7A0") };

    /// <summary>The pill's glass: graphite lit from above.</summary>
    public static readonly Brush Glass = Vertical("#D62C2D32", "#E6101013");
    /// <summary>The on-call capsule: warm amber glass.</summary>
    public static readonly Brush CallGlass = Vertical("#E6402E0E", "#F018130B");
    /// <summary>Silver primary: white → cool silver.</summary>
    public static readonly Brush Primary = Vertical("#FFFAFAFB", "#FFD6D8DD");
    public static readonly Brush PrimaryHover = Vertical("#FFFFFFFF", "#FFE2E4E8");
    /// <summary>A hairline of light along the top edge.</summary>
    public static readonly Brush Sheen = Vertical("#26FFFFFF", "#00FFFFFF", 0.35);
    /// <summary>One band of the pill's static shadow; bands stack, darkest at the edge.</summary>
    public static readonly Brush ShadowRing = Frozen("#16000000");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static Brush Vertical(string top, string bottom, double end = 1)
    {
        var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom),
            new Point(0, 0), new Point(0, end));
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
    public static readonly Geometry IconClock = Geometry.Parse("M7,1.8 A5.2,5.2 0 1 1 6.99,1.8 Z M7,4.2 V7 L8.9,8.3");
    public static readonly Geometry IconScan = Geometry.Parse("M2,4.5 V3 A1,1 0 0 1 3,2 H4.5 M9.5,2 H11 A1,1 0 0 1 12,3 V4.5 M12,9.5 V11 A1,1 0 0 1 11,12 H9.5 M4.5,12 H3 A1,1 0 0 1 2,11 V9.5 M4.5,6 H9.5 M4.5,8 H8");
    public static readonly Geometry IconMore = Geometry.Parse("M3,7 H3.01 M7,7 H7.01 M11,7 H11.01");
    public static readonly Geometry IconClose = Geometry.Parse("M4,4 L10,10 M10,4 L4,10");
    public static readonly Geometry IconCopy = Geometry.Parse("M5,5 H11 V12 H5 Z M3,9 V2.5 H9");
    public static readonly Geometry IconCheck = Geometry.Parse("M3.2,7.3 L5.9,9.9 L10.8,4.4");
    public static readonly Geometry IconPlus = Geometry.Parse("M7,3 V11 M3,7 H11");
    public static readonly Geometry IconMinus = Geometry.Parse("M3.5,7 H10.5");
    public static readonly Geometry IconTemplate = Geometry.Parse("M3.5,1.8 H8.5 L11,4.3 V12.2 H3.5 Z M5.5,7 H9 M5.5,9.4 H8");
    public static readonly Geometry IconCloud = Geometry.Parse("M4.2,11 H10.2 A2.4,2.4 0 0 0 10.5,6.2 A3.6,3.6 0 0 0 3.7,5.8 A2.5,2.5 0 0 0 4.2,11 Z");
    // A saved-text snippet: lines of text (was a "<>" code glyph that read as a stray character).
    public static readonly Geometry IconSnippet = Geometry.Parse("M2.5,3.5 H11.5 M2.5,7 H11.5 M2.5,10.5 H7.5");
    public static readonly Geometry IconGear = Geometry.Parse("M7,4.6 A2.4,2.4 0 1 1 6.99,4.6 Z M7,1.5 V3 M7,11 V12.5 M1.5,7 H3 M11,7 H12.5 M3.1,3.1 L4.2,4.2 M9.8,9.8 L10.9,10.9 M3.1,10.9 L4.2,9.8 M9.8,4.2 L10.9,3.1");
    public static readonly Geometry PauseGlyph = Geometry.Parse("M0,0 H3.6 V11 H0 Z M6.4,0 H10 V11 H6.4 Z");
    public static readonly Geometry PlayGlyph = Geometry.Parse("M0,0 L10,5.5 L0,11 Z");

    public static ShapePath Icon(Geometry data, double size = 14, Brush? stroke = null, double thickness = 1.7)
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
            HorizontalAlignment = HorizontalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight, // glyphs never mirror under RTL
            IsHitTestVisible = false,
            Stroke = stroke ?? DockPalette.Text,
        };
        if (size != 14) path.LayoutTransform = new ScaleTransform(size / 14, size / 14);
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
            Foreground = brush ?? DockPalette.Text,
        };
        AlignFlow(tb);
        return tb;
    }

    /// <summary>Latin-only runs (numbers, English toasts) read LTR inside the RTL dock.</summary>
    public static void AlignFlow(TextBlock tb) =>
        tb.FlowDirection = Bidi.HasRtl(tb.Text ?? "") ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static void SetText(TextBlock tb, string text)
    {
        tb.Text = text;
        AlignFlow(tb);
    }

    /// <summary>Tabular digits for timers and counts.</summary>
    public static TextBlock Numeric(string text, double size, Brush? brush = null, FontWeight? weight = null)
    {
        var tb = Text(text, size, brush, weight);
        tb.FlowDirection = FlowDirection.LeftToRight;
        System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    public enum Kind { Primary, Secondary, Ghost }

    /// <summary>A capsule button: silver primary, glass secondary, or a quiet ghost.</summary>
    public static Border Button(string label, Kind kind, Action? onClick, Geometry? icon = null, double height = 36)
    {
        var fg = kind switch { Kind.Primary => DockPalette.OnPrimary, Kind.Ghost => DockPalette.Muted, _ => DockPalette.SecondaryText };
        var text = Text(label, Font.Lead, fg, kind == Kind.Primary ? FontWeights.SemiBold : FontWeights.Medium);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (icon is not null)
        {
            row.Children.Add(Icon(icon, 14, fg));
            text.Margin = new Thickness(7, 0, 0, 0);
        }
        row.Children.Add(text);
        var button = new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Padding = new Thickness(kind == Kind.Ghost ? 10 : 16, 0, kind == Kind.Ghost ? 10 : 16, 1),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(kind == Kind.Secondary ? 1 : 0),
            BorderBrush = DockPalette.SecondaryStroke,
            Child = row,
            Tag = text,
        };
        Paint(button, kind);
        if (onClick is not null) Clickable(button, onClick);
        return button;
    }

    public static void Paint(Border button, Kind kind)
    {
        var (rest, hover) = kind switch
        {
            Kind.Primary => (DockPalette.Primary, DockPalette.PrimaryHover),
            Kind.Secondary => (DockPalette.Secondary, DockPalette.SecondaryHover),
            _ => (DockPalette.Ghost, DockPalette.GhostHover),
        };
        button.Background = button.IsMouseOver ? hover : rest;
        var first = !button.Resources.Contains(PaintKey);
        button.Resources[PaintKey] = new[] { rest, hover };
        if (!first) return; // re-paint: the hover wiring already reads the current pair
        button.MouseEnter += (_, _) => button.Background = ((Brush[])button.Resources[PaintKey])[1];
        button.MouseLeave += (_, _) => button.Background = ((Brush[])button.Resources[PaintKey])[0];
    }

    const string PaintKey = "dock.paint";

    /// <summary>The label TextBlock of a <see cref="Button"/>.</summary>
    public static TextBlock LabelOf(Border button) => (TextBlock)button.Tag;

    /// <summary>A round icon-only button (✕, ⏰, …).</summary>
    public static Border IconButton(Geometry icon, string tooltip, Action onClick, double size = 32, Brush? stroke = null, Kind kind = Kind.Ghost)
    {
        var button = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Icon(icon, 14, stroke ?? (kind == Kind.Primary ? DockPalette.OnPrimary : DockPalette.Faint)),
        };
        Paint(button, kind);
        Clickable(button, onClick);
        return button;
    }

    /// <summary>
    /// Press-to-click wiring: the press is marked Handled (the pill's drag
    /// CaptureMouse would otherwise swallow the release), the element dips
    /// and springs back, and the click fires on release only if the pointer
    /// is still over it.
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
            if (!pressed) return;
            pressed = false;
            Scale(scale, 1, DockMotion.Release, DockMotion.Expo);
        };
    }

    static void Scale(ScaleTransform scale, double to, int ms, IEasingFunction ease)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, DockMotion.To(to, ms, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, DockMotion.To(to, ms, ease));
    }

    /// <summary>A round badge with a check — pops 0 → 1 with a spin when shown.</summary>
    public static Border CheckBadge(Brush fill, double size = 26)
    {
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = fill,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Child = Icon(IconCheck, 14, DockPalette.OnPrimary, 2.2),
        };
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new RotateTransform(0));
        badge.RenderTransform = group;
        return badge;
    }

    public static void Pop(FrameworkElement element)
    {
        if (element.RenderTransform is not TransformGroup { Children: [ScaleTransform scale, RotateTransform spin] }) return;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.8 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, DockMotion.FromTo(0, 1, DockMotion.PopCheck, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, DockMotion.FromTo(0, 1, DockMotion.PopCheck, ease));
        spin.BeginAnimation(RotateTransform.AngleProperty, DockMotion.FromTo(-40, 0, DockMotion.PopCheck, ease));
    }

    /// <summary>Content crossfade in: opacity 0→1 plus a 6px rise, a beat after the exit.</summary>
    public static void RiseIn(UIElement element, int delayMs = DockMotion.FadeOut)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        var fade = DockMotion.FromTo(0, 1, DockMotion.FadeIn);
        fade.BeginTime = DockMotion.Delay(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        var rise = new TranslateTransform(0, 6);
        element.RenderTransform = rise;
        var up = DockMotion.FromTo(6, 0, 500);
        up.BeginTime = DockMotion.Delay(delayMs);
        rise.BeginAnimation(TranslateTransform.YProperty, up);
    }

    /// <summary>A thin vertical divider.</summary>
    public static Border Divider(double height = 18) => new()
    {
        Width = 1,
        Height = height,
        Margin = new Thickness(8, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = DockPalette.Stroke,
    };

    /// <summary>
    /// Dock tooltips: above the element (so they clear the pill), after a short
    /// delay, never at the mouse where they would sit on top of the next chip.
    /// Walks the tree once per re-fit; WPF shows one tooltip at a time.
    /// </summary>
    public static void ApplyTips(DependencyObject root)
    {
        if (root is FrameworkElement fe && fe.ToolTip is not null
            && ToolTipService.GetPlacement(fe) != System.Windows.Controls.Primitives.PlacementMode.Top)
        {
            ToolTipService.SetPlacement(fe, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetPlacementTarget(fe, fe);
            ToolTipService.SetInitialShowDelay(fe, TipDelayMs);
            ToolTipService.SetBetweenShowDelay(fe, 150);
            ToolTipService.SetShowDuration(fe, 8000);
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) ApplyTips(VisualTreeHelper.GetChild(root, i));
    }

    public const int TipDelayMs = 450;
    /// <summary>Gap between the pill's top edge and a tooltip.</summary>
    public const double TipGap = 8;

    public static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Dock copy failed: {ex.Message}"); // clipboard held by another app
            return false;
        }
    }
}
