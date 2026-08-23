using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Palon.UI;

/// <summary>iOS-style animated toggle switch.</summary>
sealed class PillSwitch : Grid
{
    readonly Border _track;
    readonly TranslateTransform _knobOffset = new();
    bool _isOn;

    public event Action<bool>? Toggled;

    public PillSwitch(bool initial)
    {
        Width = 40;
        Height = 22;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;

        _track = new Border { CornerRadius = new CornerRadius(11) }; // track = height / 2, not on the radius scale
        var knob = new Ellipse
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _knobOffset,
        };
        knob.SetResourceReference(Shape.FillProperty, "KnobBrush");
        Children.Add(_track);
        Children.Add(knob);

        _isOn = initial;
        ApplyVisual(animate: false);
        MouseLeftButtonUp += (_, _) =>
        {
            Set(!_isOn, animate: true);
            Toggled?.Invoke(_isOn);
        };
    }

    public bool IsOn => _isOn;

    public void Set(bool on, bool animate)
    {
        // No-op on same value even for animate:false syncs — reapplying
        // would cancel an in-flight toggle animation and snap the knob.
        if (_isOn == on) return;
        _isOn = on;
        ApplyVisual(animate);
    }

    void ApplyVisual(bool animate)
    {
        _track.SetResourceReference(Border.BackgroundProperty, _isOn ? "StatusGoodBrush" : "SwitchOffBrush");
        double target = _isOn ? 18 : 0;
        if (animate)
        {
            _knobOffset.BeginAnimation(TranslateTransform.XProperty, Motion.Fade(target, Motion.Fast));
        }
        else
        {
            _knobOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _knobOffset.X = target;
        }
    }
}

/// <summary>Two-option segmented control with a sliding thumb.</summary>
sealed class Segmented : Grid
{
    readonly Border _thumb;
    readonly TranslateTransform _thumbOffset = new();
    readonly TextBlock[] _labels;
    int _selected;

    public event Action<int>? SelectionChanged;

    public Segmented(string first, string second, int initial)
    {
        Height = 30;
        Cursor = Cursors.Hand;

        var track = new Border { CornerRadius = new CornerRadius(Radius.Control) };
        track.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Children.Add(track);

        _thumb = new Border
        {
            CornerRadius = new CornerRadius(Radius.Small),
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _thumbOffset,
        };
        _thumb.SetResourceReference(Border.BackgroundProperty, "SegThumbBrush");
        Children.Add(_thumb);

        var overlay = new Grid();
        overlay.ColumnDefinitions.Add(new ColumnDefinition());
        overlay.ColumnDefinitions.Add(new ColumnDefinition());
        _labels = new TextBlock[2];
        var texts = new[] { first, second };
        for (int i = 0; i < 2; i++)
        {
            _labels[i] = new TextBlock
            {
                Text = texts[i],
                FontSize = Font.Body,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Border { Background = Brushes.Transparent, Child = _labels[i] };
            Grid.SetColumn(cell, i);
            int index = i;
            cell.MouseLeftButtonUp += (_, _) =>
            {
                if (index == _selected) return;
                Select(index, animate: true);
                SelectionChanged?.Invoke(index);
            };
            overlay.Children.Add(cell);
        }
        Children.Add(overlay);

        _selected = initial;
        SizeChanged += (_, _) => Layout(animate: false);
        Loaded += (_, _) => Layout(animate: false);
        UpdateLabels();
    }

    public void Select(int index, bool animate)
    {
        _selected = index;
        Layout(animate);
        UpdateLabels();
    }

    void Layout(bool animate)
    {
        double w = (ActualWidth - 4) / 2;
        if (w <= 0) return;
        _thumb.Width = w;
        double target = _selected * w;
        if (animate)
        {
            _thumbOffset.BeginAnimation(TranslateTransform.XProperty, Motion.Fade(target, Motion.Fast));
        }
        else
        {
            _thumbOffset.BeginAnimation(TranslateTransform.XProperty, null);
            _thumbOffset.X = target;
        }
    }

    void UpdateLabels()
    {
        for (int i = 0; i < 2; i++)
        {
            _labels[i].SetResourceReference(TextBlock.ForegroundProperty,
                i == _selected ? "TextPrimaryBrush" : "TextSecondaryBrush");
            _labels[i].FontWeight = i == _selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}

/// <summary>Small factories shared by the flyout panels.</summary>
static class Ui
{
    public static TextBlock Text(string text, double size, string brushKey, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size };
        if (weight is { } w) tb.FontWeight = w;
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    // ---- the type ramp, as factories -------------------------------------
    // Five roles (see Tokens.cs). Ui.Text stays as the escape hatch, but a
    // label that fits a role should be built from the role.

    public static TextBlock Caption(string text, string brushKey = "TextSecondaryBrush") =>
        Text(text, Font.Caption, brushKey, FontWeights.SemiBold);

    public static TextBlock Small(string text, string brushKey = "TextSecondaryBrush") =>
        Text(text, Font.Small, brushKey);

    public static TextBlock Body(string text, string brushKey = "TextPrimaryBrush") =>
        Text(text, Font.Body, brushKey);

    public static TextBlock Lead(string text, string brushKey = "TextPrimaryBrush") =>
        Text(text, Font.Lead, brushKey, FontWeights.SemiBold);

    public static TextBlock Title(string text) =>
        Text(text, Font.Title, "TextPrimaryBrush", FontWeights.SemiBold);

    // ---- spacing shorthand ------------------------------------------------

    public static Thickness Top(double t) => new(0, t, 0, 0);
    public static Thickness Left(double l) => new(l, 0, 0, 0);
    public static Thickness TopBottom(double t, double b) => new(0, t, 0, b);

    public static TextBox TextBox(string text, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = Font.Body,
            Padding = Pad.Input,
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        box.SetResourceReference(System.Windows.Controls.TextBox.CaretBrushProperty, "TextPrimaryBrush");
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        return box;
    }

    public static PasswordBox PasswordBox()
    {
        var box = new PasswordBox
        {
            FontSize = Font.Body,
            Padding = Pad.Input,
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        box.SetResourceReference(System.Windows.Controls.PasswordBox.CaretBrushProperty, "TextPrimaryBrush");
        return box;
    }

    /// <summary>A silver text link that brightens to white on hover.</summary>
    public static TextBlock Link(string text, double size, FontWeight? weight = null)
    {
        var link = Text(text, size, "AccentBrush", weight ?? FontWeights.SemiBold);
        link.Cursor = Cursors.Hand;
        link.MouseEnter += (_, _) => link.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        link.MouseLeave += (_, _) => link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return link;
    }

    /// <summary>Standard action feedback on a link: swap the text, revert after 1.2 s.</summary>
    public static void Flash(TextBlock link, string message, string revertTo)
    {
        link.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Revert) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            link.Text = revertTo;
        };
        timer.Start();
    }

    /// <summary>Standard hover treatment for a clickable filled surface.</summary>
    public static void HoverFill(Border element)
    {
        element.MouseEnter += (_, _) => element.SetResourceReference(Border.BackgroundProperty, "ControlFillHoverBrush");
        element.MouseLeave += (_, _) => element.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
    }

    /// <summary>A gentle scale spring on hover — the "alive" half of the glass feel.</summary>
    public static void HoverSpring(FrameworkElement element, double to = 1.07)
    {
        var scale = AttachScale(element);
        element.MouseEnter += (_, _) => AnimateScale(scale, to, Motion.Base, Motion.Overshoot);
        element.MouseLeave += (_, _) => AnimateScale(scale, 1, Motion.Fast, Motion.Out);
    }

    /// <summary>Press feedback: a quick dip on mouse-down, spring back on release.</summary>
    public static void PressSpring(FrameworkElement element)
    {
        var scale = AttachScale(element);
        element.MouseLeftButtonDown += (_, _) => AnimateScale(scale, 0.97, Motion.Dip, Motion.Out);
        element.MouseLeftButtonUp += (_, _) => AnimateScale(scale, 1, Motion.Base, Motion.Overshoot);
        element.MouseLeave += (_, _) => AnimateScale(scale, 1, Motion.Fast, Motion.Out);
    }

    /// <summary>Fades a panel's children in one beat apart — list entrances
    /// feel placed, not dumped. Opacity-only so it can't clobber transforms
    /// (chips carry hover-spring scales). Capped so long lists stay quick.</summary>
    public static void StaggerIn(Panel panel, int stepMs = 25)
    {
        var i = 0;
        foreach (UIElement child in panel.Children)
        {
            child.BeginAnimation(UIElement.OpacityProperty, null);
            if (i >= 10)
            {
                child.Opacity = 1;
                continue;
            }
            child.Opacity = 0;
            var fade = Motion.FromTo(0, 1, Motion.Base);
            fade.BeginTime = TimeSpan.FromMilliseconds(30 + stepMs * i++);
            child.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    static ScaleTransform AttachScale(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    static void AnimateScale(ScaleTransform scale, double to, int ms, IEasingFunction ease)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(scale.ScaleX, to, ms, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(scale.ScaleY, to, ms, ease));
    }

    public static Border Divider(double top, double bottom)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, top, 0, bottom) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return line;
    }

    /// <summary>Hover dim for accent-filled surfaces — the one sanctioned opacity hover.</summary>
    public const double HoverDim = 0.92;

    /// <summary>The one elevation spec — both windows cast the same shadow.</summary>
    public static DropShadowEffect Shadow() => new()
    {
        Color = Colors.Black,
        BlurRadius = 24,
        ShadowDepth = 4,
        Direction = 270,
        Opacity = 0.45,
    };

    public static Grid ToggleRow(string label, PillSwitch toggle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Body(label);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 1);
        row.Children.Add(text);
        row.Children.Add(toggle);
        return row;
    }

    public static Border PrimaryButton(string label)
    {
        var text = Lead(label, "OnAccentBrush");
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(Radius.Control),
            Cursor = Cursors.Hand,
            Child = text,
        };
        button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        button.MouseEnter += (_, _) => button.Opacity = HoverDim;
        button.MouseLeave += (_, _) => button.Opacity = 1.0;
        PressSpring(button);
        return button;
    }

    public static Border OptionCard(string title, string description)
    {
        var stack = new StackPanel();
        stack.Children.Add(Lead(title));
        var desc = Small(description);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(desc);

        var card = new Border
        {
            CornerRadius = new CornerRadius(Radius.Card),
            Padding = Pad.CardLoose,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        HoverFill(card);
        return card;
    }

    public static void SetCardSelected(Border card, bool selected)
    {
        if (selected) card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else card.BorderBrush = Brushes.Transparent;
    }

    /// <summary>A labeled, copyable command line for the softphone setup.</summary>
    public static Border CommandRow(string caption, string command)
    {
        var stack = new StackPanel();
        stack.Children.Add(Caption(caption));

        var line = new Grid { Margin = Top(Space.Tight) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cmd = Small(command, "TextPrimaryBrush");
        cmd.FontFamily = Font.Mono;
        cmd.TextTrimming = TextTrimming.CharacterEllipsis;
        cmd.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(cmd);

        var copy = Text("Copy", Font.Small, "AccentBrush", FontWeights.SemiBold);
        copy.Cursor = Cursors.Hand;
        copy.Margin = Left(Space.Row);
        copy.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(copy, 1);
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Revert) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            copy.Text = "Copy";
        };
        copy.MouseLeftButtonUp += (_, _) =>
        {
            if (!SnippetPaster.TrySetClipboard(command)) return;
            copy.Text = "Copied ✓";
            revert.Stop();
            revert.Start();
        };
        line.Children.Add(copy);
        stack.Children.Add(line);

        var row = new Border
        {
            CornerRadius = new CornerRadius(Radius.Control),
            Padding = Pad.Card,
            Child = stack,
        };
        row.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return row;
    }
}
