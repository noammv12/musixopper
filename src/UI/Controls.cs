using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Bridget.UI;

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

        _track = new Border { CornerRadius = new CornerRadius(11) };
        var knob = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _knobOffset,
        };
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

        var track = new Border { CornerRadius = new CornerRadius(8) };
        track.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Children.Add(track);

        _thumb = new Border
        {
            CornerRadius = new CornerRadius(6),
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
                FontSize = 12,
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
            _thumbOffset.BeginAnimation(TranslateTransform.XProperty, Motion.Fade(target, 150));
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

    public static TextBox TextBox(string text, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
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
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
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
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
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
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.MouseEnter += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(scale.ScaleX, to, 150, Motion.Overshoot));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(scale.ScaleY, to, 150, Motion.Overshoot));
        };
        element.MouseLeave += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(scale.ScaleX, 1, Motion.Fast, Motion.Out));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(scale.ScaleY, 1, Motion.Fast, Motion.Out));
        };
    }

    /// <summary>Press feedback: a quick dip on mouse-down, spring back on release.</summary>
    public static void PressSpring(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        void To(double target, int ms, IEasingFunction ease)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(scale.ScaleX, target, ms, ease));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(scale.ScaleY, target, ms, ease));
        }
        element.MouseLeftButtonDown += (_, _) => To(0.97, 80, Motion.Out);
        element.MouseLeftButtonUp += (_, _) => To(1, 150, Motion.Overshoot);
        element.MouseLeave += (_, _) => To(1, Motion.Fast, Motion.Out);
    }

    public static Border Divider(double top, double bottom)
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, top, 0, bottom) };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return line;
    }

    public static Grid ToggleRow(string label, PillSwitch toggle)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Text(label, 12, "TextPrimaryBrush");
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(toggle, 1);
        row.Children.Add(text);
        row.Children.Add(toggle);
        return row;
    }

    public static Border PrimaryButton(string label)
    {
        var text = Text(label, 12.5, "OnAccentBrush", FontWeights.SemiBold);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            Child = text,
        };
        button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        button.MouseEnter += (_, _) => button.Opacity = 0.92;
        button.MouseLeave += (_, _) => button.Opacity = 1.0;
        PressSpring(button);
        return button;
    }

    public static Border OptionCard(string title, string description)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, 12.5, "TextPrimaryBrush", FontWeights.SemiBold));
        var desc = Text(description, 11, "TextSecondaryBrush");
        desc.TextWrapping = TextWrapping.Wrap;
        desc.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(desc);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
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
        stack.Children.Add(Text(caption, 10.5, "TextSecondaryBrush", FontWeights.SemiBold));

        var line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cmd = Text(command, 11, "TextPrimaryBrush");
        cmd.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
        cmd.TextTrimming = TextTrimming.CharacterEllipsis;
        cmd.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(cmd);

        var copy = Text("Copy", 11, "AccentBrush", FontWeights.SemiBold);
        copy.Cursor = Cursors.Hand;
        copy.Margin = new Thickness(10, 0, 0, 0);
        copy.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(copy, 1);
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
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
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 8),
            Child = stack,
        };
        row.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return row;
    }
}
