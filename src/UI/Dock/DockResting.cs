using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Sales;

namespace Palon.UI;

/// <summary>The resting capsule and the hover bar.</summary>
sealed partial class DockWindow
{
    // Resting capsule: dot (+ static level bars and the live timer on a call).
    StackPanel _restContent = null!;
    Ellipse _restDot = null!;
    StackPanel _restBars = null!;
    TextBlock _restTimer = null!;

    // Hover bar: status · progress · overdue | chips.
    StackPanel _expandedContent = null!;
    Ellipse _statusDot = null!;
    TextBlock _statusText = null!;
    TextBlock _statusTimer = null!;
    Border _progressSep = null!;
    TextBlock _progressText = null!;
    Border _overdueSep = null!;
    TextBlock _overdueText = null!;
    StackPanel _chipsPanel = null!;

    static readonly double[] LevelBars = { 6, 10, 7, 9 };

    void BuildResting()
    {
        _restDot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Fill = DockPalette.Done };
        // No mic meter reaches the dock during a call (only dictation/Ask
        // stream levels), so the bars are a quiet static glyph, not a fake wave.
        _restBars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var h in LevelBars)
        {
            var bar = new Border { Width = 2.5, Height = h * 0.8, CornerRadius = new CornerRadius(1.25), Margin = new Thickness(1.25, 0, 1.25, 0), VerticalAlignment = VerticalAlignment.Center, Background = DockPalette.Muted };
            _restBars.Children.Add(bar);
        }
        _restTimer = DockKit.Numeric("", Font.Body, DockPalette.Muted);
        _restTimer.Margin = new Thickness(8, 0, 0, 0);
        _restContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _restContent.Children.Add(_restDot);
        _restContent.Children.Add(_restBars);
        _restContent.Children.Add(_restTimer);

        _statusDot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Fill = DockPalette.Done };
        _statusText = DockKit.Text("מוכן", Font.Lead, DockPalette.Muted);
        _statusText.Margin = new Thickness(8, 0, 0, 0);
        _statusText.MaxWidth = 170;
        _statusTimer = DockKit.Numeric("", Font.Lead, DockPalette.Muted);
        _statusTimer.Margin = new Thickness(6, 0, 0, 0);

        _progressSep = Separator();
        _progressText = DockKit.Numeric("", Font.Lead);
        _progressText.ToolTip = "הפקדות החודש / יעד";
        _overdueSep = Separator();
        _overdueText = DockKit.Text("", Font.Lead, DockPalette.OverdueText);
        _overdueText.Cursor = System.Windows.Input.Cursors.Hand;
        _overdueText.ToolTip = "פתח את החזרות";
        DockKit.Clickable(_overdueText, () => OpenRemindersRequested?.Invoke());

        _chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _expandedContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 0, 6, 0),
        };
        _expandedContent.Children.Add(_statusDot);
        _expandedContent.Children.Add(_statusText);
        _expandedContent.Children.Add(_statusTimer);
        _expandedContent.Children.Add(_progressSep);
        _expandedContent.Children.Add(_progressText);
        _expandedContent.Children.Add(_overdueSep);
        _expandedContent.Children.Add(_overdueText);
        _expandedContent.Children.Add(Separator());
        _expandedContent.Children.Add(_chipsPanel);

        ApplyCallVisuals();
    }

    static Border Separator()
    {
        var sep = new Border { Width = 1, Height = 14, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        sep.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        return sep;
    }

    /// <summary>Dot color + on-call extras (bars, timer) for the current call state.</summary>
    void ApplyCallVisuals()
    {
        var dot = _callState switch
        {
            CallState.OnCall => DockPalette.OnCall,
            CallState.Disabled => DockPalette.Faint,
            _ => DockPalette.Done,
        };
        _restDot.Fill = dot;
        _statusDot.Fill = dot;
        var onCall = _callState == CallState.OnCall;
        _restBars.Visibility = onCall ? Visibility.Visible : Visibility.Collapsed;
        _restTimer.Visibility = onCall ? Visibility.Visible : Visibility.Collapsed;
        _statusTimer.Visibility = onCall ? Visibility.Visible : Visibility.Collapsed;
        _restDot.Width = _restDot.Height = onCall ? 8 : 6;
    }

    /// <summary>The resting capsule's size: a sliver idle, a readable chip on a call.</summary>
    (double Width, double Height) RestMetrics()
    {
        if (_callState != CallState.OnCall) return (IdleWidth, IdleHeight);
        _restContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Width is quantized to 4 DIP so the ticking timer ("09:59" → "10:00")
        // never nudges the capsule by a pixel.
        return (Math.Ceiling((_restContent.DesiredSize.Width + 28) / 4) * 4, OnCallHeight);
    }

    void RefitExpanded()
    {
        if (_state != DockState.Expanded) return;
        MorphTo(MeasureWidth(_expandedContent), ExpandedHeight, ExpandedHeight / 2, DockMotion.MorphQuick);
    }

    // ---- day stats: progress + overdue --------------------------------------------

    void OnSalesChanged() => Dispatcher.InvokeAsync(RefreshDayStats, DispatcherPriority.Background);

    /// <summary>"40/55" this month (hidden without a target) and "2 באיחור" (hidden at zero).</summary>
    void RefreshDayStats()
    {
        string? progress = null;
        string? overdue = null;
        var now = DateTime.Now;
        try
        {
            if (SalesStore.Get(now.Year, now.Month) is { } book)
                progress = DockText.Progress(book.Deals.Count, book.Target);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock progress read failed: {ex.Message}");
        }
        try
        {
            overdue = DockText.Overdue(CallbackPlanner.Counts(CallbackStore.Load(), now).Overdue);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock overdue read failed: {ex.Message}");
        }
        _progressText.Text = progress ?? "";
        _progressText.Visibility = _progressSep.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        _overdueText.Text = overdue ?? "";
        _overdueText.Visibility = _overdueSep.Visibility = overdue is null ? Visibility.Collapsed : Visibility.Visible;
        RefitExpanded();
    }

    // ---- chips ---------------------------------------------------------------------

    void RefreshChips()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshChips);
            return;
        }
        _chipsPanel.Children.Clear();

        var ask = IconChip(DockKit.IconChat, "Palon", () => AssistantToggleRequested?.Invoke());
        ask.ToolTip = AssistantHotkeyLive
            ? $"שאל את Palon ({_assistantHotkey})"
            : "שאל את Palon";
        _chipsPanel.Children.Add(ask);

        if (DockActions.HasTerminal)
        {
            var terminal = IconChip(DockKit.IconTerminal, null, () => DockActions.OpenTerminal());
            terminal.ToolTip = "טרמינל";
            _chipsPanel.Children.Add(terminal);
        }

        var scan = IconChip(DockKit.IconScan, null, TerminalWindow.ReadScreenFromShortcut);
        scan.ToolTip = ScreenHotkeyLive
            ? $"קרא מהמסך ({ScreenHotkeyLabel}) — סמן אזור, אשר, ו-Palon יקרא"
            : "קרא מהמסך — סמן אזור, אשר, ו-Palon יקרא";
        _chipsPanel.Children.Add(scan);

        var dictate = IconChip(DockKit.IconMic, null, () => DictationToggleRequested?.Invoke());
        dictate.ToolTip = DictationHotkeyLive
            ? $"הכתבה ({_dictationHotkey}) — מדברים, והטקסט מוקלד איפה שהסמן"
            : "הכתבה — מדברים, והטקסט מוקלד איפה שהסמן";
        _chipsPanel.Children.Add(dictate);

        var callbacks = IconChip(DockKit.IconClock, null, () => OpenRemindersRequested?.Invoke());
        callbacks.ToolTip = "חזרות";
        _chipsPanel.Children.Add(callbacks);

        var notes = IconChip(DockKit.IconNote, null, () => OpenNotesRequested?.Invoke());
        notes.ToolTip = "הערות שיחה";
        _chipsPanel.Children.Add(notes);

        var snippets = SnippetStore.Load().Take(3).ToList();
        if (snippets.Count > 0)
        {
            var sep = Separator();
            sep.Margin = new Thickness(8, 0, 4, 0);
            _chipsPanel.Children.Add(sep);
            foreach (var snippet in snippets) _chipsPanel.Children.Add(SnippetChip(snippet));
        }

        var more = IconChip(DockKit.IconMore, null, () => OpenFlyoutRequested?.Invoke());
        more.ToolTip = "קטעים והגדרות";
        _chipsPanel.Children.Add(more);

        if (_state == DockState.Expanded) RefitExpanded();
    }

    /// <summary>A 30px round glyph chip (optionally with a word), hover fill, press spring.</summary>
    static Border IconChip(Geometry icon, string? label, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(DockKit.Icon(icon));
        if (label is not null)
        {
            var text = DockKit.Text(label, Font.Body, null, FontWeights.Medium);
            text.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(text);
        }
        var chip = new Border
        {
            MinWidth = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(label is null ? 0 : 10, 0, label is null ? 0 : 12, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(chip);
        DockKit.Clickable(chip, onClick);
        return chip;
    }

    Border SnippetChip(Snippet snippet)
    {
        var original = snippet.Label.Length > 0 ? snippet.Label : "(ללא שם)";
        var label = DockKit.Text(original, Font.Body, null, FontWeights.Medium);
        label.MaxWidth = 84;
        var chip = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 0, 12, 1),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
            Tag = label,
            ToolTip = (snippet.Text.Length > 120 ? snippet.Text[..120] + "…" : snippet.Text) + "\n(קליק ימני: העתק בלבד)",
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(chip);

        // One revert timer per chip, width pinned while the text swaps (AUDIT #9).
        var flash = DockKit.Flasher(chip, original);
        DockKit.Clickable(chip, async () =>
        {
            var result = await SnippetPaster.PasteAsync(snippet);
            flash(result switch
            {
                PasteResult.Pasted => "הודבק ✓",
                PasteResult.CopiedOnly => "הועתק ✓",
                _ => "נכשל",
            });
        });
        chip.MouseRightButtonUp += (_, _) =>
        {
            var result = SnippetPaster.CopyOnly(snippet);
            flash(result == PasteResult.Failed ? "נכשל" : "הועתק ✓");
        };
        return chip;
    }
}
