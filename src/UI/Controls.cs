using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // The stock Aero2 input template hard-codes Windows-blue hover/focus
    // borders on its named child, discarding our BorderBrush — replacing the
    // template (built in code, cached per target type) is the only way out.
    // Rounded corners come along for free.
    static readonly Dictionary<Type, ControlTemplate> InputTemplates = new();

    static ControlTemplate InputTemplate(Type targetType)
    {
        if (InputTemplates.TryGetValue(targetType, out var cached)) return cached;

        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius.Control));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        // PART_ContentHost must be a ScrollViewer (or Decorator) for the text
        // editor to attach; the control's scrollbar settings are forwarded to
        // it by TextBoxBase itself.
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        host.SetValue(UIElement.FocusableProperty, false);
        border.AppendChild(host);

        var template = new ControlTemplate(targetType) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("ControlFillHoverBrush"), "border"));
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty,
            new DynamicResourceExtension("AccentBrush"), "border"));
        template.Triggers.Add(hover);
        template.Triggers.Add(focus); // after hover — a focused box stays accent under the cursor
        template.Seal();
        InputTemplates[targetType] = template;
        return template;
    }

    public static TextBox TextBox(string text, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = Font.Body,
            Padding = Pad.Input,
            BorderThickness = new Thickness(1),
            Template = InputTemplate(typeof(TextBoxBase)),
        };
        box.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        box.SetResourceReference(System.Windows.Controls.TextBox.CaretBrushProperty, "TextPrimaryBrush");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 64;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            box.Resources.Add(typeof(ScrollBar), ThinScrollBarStyle());
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
            Template = InputTemplate(typeof(PasswordBox)),
        };
        box.SetResourceReference(Control.BackgroundProperty, "ControlFillBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        box.SetResourceReference(System.Windows.Controls.PasswordBox.CaretBrushProperty, "TextPrimaryBrush");
        box.SetResourceReference(System.Windows.Controls.PasswordBox.SelectionBrushProperty, "AccentBrush");
        return box;
    }

    // ---- thin scrollbars -------------------------------------------------
    // The stock ~17px Windows scrollbar doesn't belong on the glass. This is
    // an 8px overlay bar: a bare Track plus a rounded thumb, dark and quiet.
    // Track exposes no content property, so the Thumb is attached in code
    // when each bar loads (a FrameworkElementFactory can't parent it).
    static Style? _thinScrollBarStyle;
    static ControlTemplate? _thinThumbTemplate;

    /// <summary>Swaps a ScrollViewer's bars for the thin overlay style. The
    /// control stays a stock ScrollBar, so the flyout's drag exemption keeps
    /// matching it by type.</summary>
    public static ScrollViewer ThinScroll(ScrollViewer viewer)
    {
        viewer.Resources.Add(typeof(ScrollBar), ThinScrollBarStyle());
        return viewer;
    }

    static Style ThinScrollBarStyle()
    {
        if (_thinScrollBarStyle is not null) return _thinScrollBarStyle;

        static System.Windows.Data.Binding FromBar(string path) =>
            new(path) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent };

        var track = new FrameworkElementFactory(typeof(Track), "PART_Track");
        track.SetValue(Track.IsDirectionReversedProperty, true); // vertical bars only in this app
        track.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 2, 1, 2));
        track.SetBinding(Track.MinimumProperty, FromBar(nameof(ScrollBar.Minimum)));
        track.SetBinding(Track.MaximumProperty, FromBar(nameof(ScrollBar.Maximum)));
        track.SetBinding(Track.ValueProperty, FromBar(nameof(ScrollBar.Value)));
        track.SetBinding(Track.ViewportSizeProperty, FromBar(nameof(ScrollBar.ViewportSize)));
        track.SetBinding(Track.OrientationProperty, FromBar(nameof(ScrollBar.Orientation)));

        var template = new ControlTemplate(typeof(ScrollBar)) { VisualTree = track };
        template.Seal();

        var style = new Style(typeof(ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 8.0));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnThinScrollBarLoaded)));
        style.Seal();
        return _thinScrollBarStyle = style;
    }

    static void OnThinScrollBarLoaded(object sender, RoutedEventArgs e)
    {
        var bar = (ScrollBar)sender;
        if (bar.Template?.FindName("PART_Track", bar) is not Track track || track.Thumb is not null) return;
        track.Thumb = new Thumb
        {
            MinHeight = 24, // a graspable handle even on very long lists
            Template = ThinThumbTemplate(),
        };
    }

    static ControlTemplate ThinThumbTemplate()
    {
        if (_thinThumbTemplate is not null) return _thinThumbTemplate;
        var body = new FrameworkElementFactory(typeof(Border));
        body.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius.Hairline));
        body.SetResourceReference(Border.BackgroundProperty, "ControlFillHoverBrush");
        var template = new ControlTemplate(typeof(Thumb)) { VisualTree = body };
        template.Seal();
        return _thinThumbTemplate = template;
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

    // One revert timer per TextBlock: a re-flash restarts the countdown
    // instead of stacking timers, so feedback never reverts early.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, DispatcherTimer> FlashTimers = new();

    /// <summary>Standard action feedback on a link: swap the text, revert
    /// after 1.2 s (or a caller-chosen hold, e.g. Motion.Linger for errors).</summary>
    public static void Flash(TextBlock link, string message, string revertTo, int ms = Motion.Revert)
    {
        link.Text = message;
        var timer = FlashTimers.GetValue(link, l =>
        {
            var t = new DispatcherTimer();
            t.Tick += (_, _) =>
            {
                t.Stop();
                l.Text = (string)t.Tag;
            };
            return t;
        });
        timer.Tag = revertTo;
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.Start();
    }

    /// <summary>The standard Copy link: caption-size, flashes "Copied ✓".</summary>
    public static TextBlock CopyLink(Func<string?> text)
    {
        var copy = Link("Copy", Font.Caption);
        copy.MouseLeftButtonUp += (_, _) =>
        {
            if (text() is { } t && SnippetPaster.TrySetClipboard(t)) Flash(copy, "Copied ✓", "Copy");
        };
        return copy;
    }

    /// <summary>An API-key section: masked box + Save, status line + Remove.
    /// The caller owns the status text via the returned TextBlock.</summary>
    public static (Grid InputRow, Grid StatusRow, TextBlock Status) KeyRow(
        PasswordBox box, Action<string> onSave, Action onRemove)
    {
        var inputRow = new Grid { Margin = Top(Space.Tight) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(box);
        var save = Link("Save", Font.Small);
        save.Margin = Left(Space.Row);
        save.VerticalAlignment = VerticalAlignment.Center;
        save.MouseLeftButtonUp += (_, _) =>
        {
            if (box.Password.Trim().Length == 0) return;
            onSave(box.Password);
            box.Password = "";
        };
        Grid.SetColumn(save, 1);
        inputRow.Children.Add(save);

        var statusRow = new Grid { Margin = Top(Space.Tight) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = Small("");
        status.TextWrapping = TextWrapping.Wrap;
        statusRow.Children.Add(status);
        var remove = Link("Remove", Font.Small);
        remove.Margin = Left(Space.Row);
        remove.MouseLeftButtonUp += (_, _) => onRemove();
        Grid.SetColumn(remove, 1);
        statusRow.Children.Add(remove);

        return (inputRow, statusRow, status);
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

    /// <summary>The standard list card: rounded, quiet fill, one card rhythm.</summary>
    public static Border Card(UIElement child)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(Radius.Card),
            Padding = Pad.Card,
            Margin = Top(Space.Row),
            Child = child,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return card;
    }

    /// <summary>The one quiet empty-state voice: secondary, wrapping, and
    /// where there's something to do, saying what.</summary>
    public static TextBlock EmptyState(string text)
    {
        var empty = Small(text);
        empty.TextWrapping = TextWrapping.Wrap;
        empty.Margin = new Thickness(2, Space.Row, 2, 0);
        return empty;
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
            BorderThickness = new Thickness(1), // whole pixels only — 1.5 blurs at every DPI

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
        copy.MouseLeftButtonUp += (_, _) =>
        {
            if (SnippetPaster.TrySetClipboard(command)) Flash(copy, "Copied ✓", "Copy");
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
