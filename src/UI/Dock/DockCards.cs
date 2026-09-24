using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Palon.Notes;

namespace Palon.UI;

/// <summary>
/// The two cards the pill morphs into: right after a call (summary, one-tap
/// callback, copy for Salesforce) and when a callback comes due. Which card
/// shows, and in what order, is DockCardQueue's call (pure, tested).
/// </summary>
sealed partial class DockWindow
{
    readonly DockCardQueue _cards = new();
    Border _cardHost = null!;
    string? _cardShownKey;     // which card's element is in _cardHost
    bool _cardResolved;        // the user acted on the showing card (store echoes are ours)
    DispatcherTimer _cardIdle = null!;
    DispatcherTimer _cardConfirm = null!;
    PalonAvatar? _cardAvatar;

    void BuildCardHost()
    {
        _cardHost = new Border
        {
            Padding = new Thickness(18, 16, 18, 16),
            VerticalAlignment = VerticalAlignment.Top, // the pill reveals it top-down as it grows
        };
        _cardIdle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DockMotion.AfterCallIdleMs) };
        _cardIdle.Tick += (_, _) =>
        {
            _cardIdle.Stop();
            if (_pill.IsMouseOver) return; // MouseLeave re-arms it
            if (_cards.Current is not { Kind: DockCardKind.AfterCall }) return;
            if (_state == DockState.Card) AdvanceCard();
            else RetireQuietly(); // tucked while the mic had the pill
        };
        _cardConfirm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DockMotion.ConfirmHoldMs) };
        _cardConfirm.Tick += (_, _) =>
        {
            _cardConfirm.Stop();
            if (_pill.IsMouseOver)
            {
                _cardConfirm.Start(); // still reading (or reaching for undo) — hold
                return;
            }
            if (_state == DockState.Card) AdvanceCard();
            else RetireQuietly();
        };
    }

    /// <summary>Retire the current card without touching the pill (it's showing something else).</summary>
    void RetireQuietly()
    {
        _cardShownKey = null;
        _cards.Advance(); // the next one (if any) shows when the pill is free — SetState routes it
    }

    // ---- entry points -----------------------------------------------------------

    /// <summary>A call's note is ready: morph into the after-call card.
    /// (Short/unanswered calls produce no note, so nothing shows.)</summary>
    public void ShowAfterCall(CallNote note)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowAfterCall(note));
            return;
        }
        _cards.Enqueue(DockCard.ForNote(note));
        TryShowCard();
    }

    /// <summary>A callback came due (missed = it fired late).</summary>
    public void ShowCallbackDue(Callback callback, bool missed)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowCallbackDue(callback, missed));
            return;
        }
        _cards.Enqueue(DockCard.ForCallback(callback, missed));
        TryShowCard();
    }

    bool CanShowCard => !_fullscreenHidden && IsVisible && _callState != CallState.OnCall
        && _state != DockState.Dictation;

    void TryShowCard()
    {
        if (!CanShowCard) return; // stays queued; surfaced when the dock is free again
        if (_cards.Resume() is not { } card) return;
        if (_state == DockState.Card && _cardShownKey == card.Key) return;
        Present(card);
    }

    void Present(DockCard card)
    {
        StopCardTimers();
        _cardResolved = false;
        var element = card.Kind == DockCardKind.AfterCall
            ? BuildAfterCallCard(card.Note!)
            : BuildCallbackCard(card.Callback!, card.Missed);
        if (_state == DockState.Card)
        {
            // Card → card: the old content steps out, the pill re-morphs,
            // the new content rises in. No cut.
            var exit = DockMotion.To(0, DockMotion.FadeOut);
            exit.Completed += (_, _) =>
            {
                if (_cardShownKey != card.Key) return; // superseded mid-exit
                _cardHost.Child = element;
                MorphTo(CardWidth, MeasureCardHeight(), CardRadius, DockMotion.Morph);
                DockKit.RiseIn(_cardHost, 0);
            };
            _cardShownKey = card.Key;
            _cardHost.BeginAnimation(OpacityProperty, exit);
        }
        else
        {
            _cardShownKey = card.Key;
            _cardHost.Child = element;
            _toastTimer.Stop();
            _hoverIntent.Stop();
            _collapseDelay.Stop();
            SetState(DockState.Card);
        }
        if (card.Kind == DockCardKind.AfterCall && !_pill.IsMouseOver) _cardIdle.Start();
    }

    /// <summary>Retire the showing card: the next one takes its place, or the pill settles.</summary>
    void AdvanceCard()
    {
        StopCardTimers();
        _cardShownKey = null;
        var next = _cards.Advance();
        if (next is not null && CanShowCard) Present(next);
        else SetState(RestState());
        RefreshDayStats();
    }

    void StopCardTimers()
    {
        _cardIdle?.Stop();
        _cardConfirm?.Stop();
    }

    void PauseCardIdle() => _cardIdle.Stop();

    void ResumeCardIdle()
    {
        if (_cards.Current is { Kind: DockCardKind.AfterCall } && !_cardConfirm.IsEnabled)
        {
            _cardIdle.Stop();
            _cardIdle.Start();
        }
    }

    double MeasureCardHeight()
    {
        _cardHost.Measure(new Size(CardWidth - 2, double.PositiveInfinity));
        return Math.Min(Math.Ceiling(_cardHost.DesiredSize.Height) + 2, WindowHeight - 24);
    }

    /// <summary>The card's content changed height (picks → confirmation): re-morph, quick.</summary>
    void RefitCard()
    {
        if (_state == DockState.Card) MorphTo(CardWidth, MeasureCardHeight(), CardRadius, DockMotion.MorphQuick);
    }

    /// <summary>A callback was handled somewhere else (flyout, voice): drop its card quietly.</summary>
    void OnCallbacksChanged() => Dispatcher.InvokeAsync(() =>
    {
        RefreshDayStats();
        List<Callback> all;
        try
        {
            all = CallbackStore.Load();
        }
        catch
        {
            return;
        }
        // Gone, finished, or re-timed elsewhere → this card no longer speaks for it.
        bool Stale(DockCard c)
        {
            if (c.Kind != DockCardKind.CallbackDue) return false;
            var live = all.Find(x => x.Id == c.Callback!.Id);
            return live is null || !live.IsActive || live.DueAtUtc != c.Callback!.DueAtUtc;
        }
        foreach (var waiting in _cards.Waiting.Where(Stale).ToList()) _cards.Forget(waiting.Key);
        if (!_cardResolved && _cards.Current is { } cur && Stale(cur))
        {
            if (_state == DockState.Card && _cardShownKey == cur.Key) AdvanceCard();
            else _cards.Forget(cur.Key);
        }
    }, DispatcherPriority.Background);

    // ---- shared card pieces ---------------------------------------------------------

    Grid CardHeader(string title, TextBlock subtitle, UIElement? trailing)
    {
        _cardAvatar = new PalonAvatar { Width = 36, Height = 36, Mood = PalonMood.Idle, VerticalAlignment = VerticalAlignment.Center };
        var titleText = DockKit.Text(title, 15, null, FontWeights.SemiBold);
        titleText.FlowDirection = FlowDirection.RightToLeft;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        titles.Children.Add(titleText);
        subtitle.Margin = new Thickness(0, 2, 0, 0);
        titles.Children.Add(subtitle);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_cardAvatar);
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    static Border Well(string text, Action? onClick)
    {
        var body = new TextBlock
        {
            Text = text,
            FontSize = Font.Lead,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 40, // two lines, then an ellipsis
            Foreground = DockPalette.Body,
        };
        DockKit.AlignFlow(body);
        var well = new Border
        {
            Background = DockPalette.Well,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 12, 10),
            Margin = new Thickness(0, 14, 0, 0),
            Child = body,
        };
        if (onClick is not null)
        {
            well.Cursor = System.Windows.Input.Cursors.Hand;
            well.ToolTip = "פתח בהערות";
            DockKit.Clickable(well, onClick);
        }
        return well;
    }

    /// <summary>The "done ✓" row: badge · message · undo. Hidden until shown.</summary>
    static (Border Row, Border Badge, TextBlock Text, Border Undo) ConfirmRow()
    {
        var badge = DockKit.CheckBadge(DockPalette.Done);
        var text = DockKit.Text("", 14, null, FontWeights.Medium);
        text.FlowDirection = FlowDirection.RightToLeft;
        text.Margin = new Thickness(10, 0, 8, 0);
        var undoButton = DockKit.Button("בטל", DockKit.Kind.Ghost, null); // caller wires Clickable
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(badge);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(undoButton, 2);
        grid.Children.Add(undoButton);
        var row = new Border
        {
            Background = DockPalette.DoneWell,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 8, 8, 8),
            Margin = new Thickness(0, 14, 0, 0),
            Child = grid,
            Visibility = Visibility.Collapsed,
        };
        return (row, badge, text, undoButton);
    }

    /// <summary>Action buttons, 8 apart; wraps rather than clips if a long
    /// number or label ever outgrows the card.</summary>
    static WrapPanel ButtonRow(params Border[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, -8) };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            row.Children.Add(button);
        }
        return row;
    }

    static void Swap(FrameworkElement hide, FrameworkElement show)
    {
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        DockKit.RiseIn(show, 0);
    }

    static bool TryCopy(string text)
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

    // ---- 1 · right after a call ------------------------------------------------------

    FrameworkElement BuildAfterCallCard(CallNote note)
    {
        var now = DateTime.Now;
        Callback? created = null;
        DateTime? chosen = null;

        var hasSummary = note.Summary is { Length: > 0 };
        var subtitle = DockKit.Text(
            $"{DockText.Iso(DockText.Duration(note.DurationSec))} · {(hasSummary ? "הסיכום מוכן" : "אין סיכום")}",
            Font.Body, DockPalette.Muted);
        subtitle.FlowDirection = FlowDirection.RightToLeft;
        var close = DockKit.IconButton(DockKit.IconClose, "סגור", AdvanceCard);
        var header = CardHeader(DockText.CallEnded(DockText.Who(note)), subtitle, close);

        var well = Well(DockText.OneLine(note), () => OpenNotesRequested?.Invoke());

        // "When to call back?" — one tap books it.
        var picks = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var question = DockKit.Text("מתי לחזור?", Font.Body, DockPalette.Muted);
        question.FlowDirection = FlowDirection.RightToLeft;
        picks.Children.Add(question);
        var chipRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        picks.Children.Add(chipRow);
        if (DockText.Heard(note.ProposedCallback) is { } heard)
        {
            var heardLine = DockKit.Text(heard, Font.Body, DockPalette.HeardText);
            heardLine.FlowDirection = FlowDirection.RightToLeft;
            heardLine.Margin = new Thickness(0, 8, 0, 0);
            picks.Children.Add(heardLine);
        }

        var (confirm, badge, confirmText, undoButton) = ConfirmRow();

        var copy = DockKit.Button("העתק סיכום", DockKit.Kind.Primary, null, DockKit.IconCopy);
        copy.ToolTip = "מוכן להדבקה ב-Salesforce: שם, תאריך, משך, סיכום וצעד הבא";
        var copyFlash = DockKit.Flasher(copy, "העתק סיכום");
        DockKit.Clickable(copy, () =>
        {
            var ok = TryCopy(DockText.SalesforceSummary(note, chosen, DateTime.Now));
            copyFlash(ok ? "הועתק ✓" : "נכשל");
            ResumeCardIdle();
        });
        var notes = DockKit.Button("הערות", DockKit.Kind.Secondary, () => OpenNotesRequested?.Invoke());

        foreach (var pick in DockText.Picks(CallbackPlanner.QuickPicks(now), note.ProposedCallback, now))
            chipRow.Children.Add(PickChip(pick, () => Book(pick)));

        var card = new StackPanel();
        card.Children.Add(header);
        card.Children.Add(well);
        card.Children.Add(picks);
        card.Children.Add(confirm);
        var salesforce = DockKit.Button("ל-Salesforce", DockKit.Kind.Secondary, () => DockActions.LogToSalesforce(note, chosen));
        salesforce.ToolTip = "תיעוד השיחה ב-Salesforce: תצוגה מקדימה, אישור, שמירה ובדיקה";
        card.Children.Add(ButtonRow(copy, notes, salesforce));
        DockKit.Clickable(undoButton, Undo);
        return card;

        void Book(DockPick pick)
        {
            if (created is not null) return;
            Callback? callback;
            try
            {
                if (note.ProposedCallback is { IsPending: true })
                    callback = CallbackProposals.Accept(note, pick.Key == "heard" ? null : pick.DueLocal.ToUniversalTime());
                else
                {
                    var text = NoteBrief.KeyLine(note.Summary ?? "") is { Length: > 0 } key ? key : "חזרה אחרי שיחה";
                    callback = CallbackStore.Add(text, pick.DueLocal.ToUniversalTime(), phone: note.Number,
                        source: CallbackSource.AutoCall, callNoteId: note.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Dock callback booking failed: {ex.Message}");
                callback = null;
            }
            if (callback is null)
            {
                question.Text = "לא הצלחתי לקבוע — נסה מהחזרות";
                question.Foreground = DockPalette.OverdueText;
                return;
            }
            created = callback;
            chosen = pick.DueLocal;
            confirmText.Text = $"חזרה נקבעה · {DockText.When(pick.DueLocal, DateTime.Now)}";
            Swap(picks, confirm);
            DockKit.Pop(badge);
            _cardAvatar?.Cheer();
            RefitCard();
            RefreshDayStats();
        }

        void Undo()
        {
            if (created is not { } booked) return;
            try
            {
                CallbackStore.Remove(booked.Id);
                // Put a heard proposal back to pending, as if never accepted.
                if (note.ProposedCallback is { IsPending: true }) NotesStore.Update(note);
            }
            catch (Exception ex)
            {
                Log.Write($"Dock callback undo failed: {ex.Message}");
            }
            created = null;
            chosen = null;
            Swap(confirm, picks);
            RefitCard();
            RefreshDayStats();
        }
    }

    static Border PickChip(DockPick pick, Action onPick)
    {
        var label = DockKit.Text(pick.Label, Font.Lead, pick.Heard ? DockPalette.HeardText : null, FontWeights.Medium);
        label.FlowDirection = FlowDirection.RightToLeft;
        var chip = new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(14, 0, 14, 1),
            Margin = new Thickness(0, 0, 6, 6),
            BorderThickness = new Thickness(1),
            BorderBrush = pick.Heard ? DockPalette.HeardStroke : Brushes.Transparent,
            Background = pick.Heard ? DockPalette.HeardFill : DockPalette.Secondary,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
            Tag = label,
        };
        if (!pick.Enabled)
        {
            chip.Opacity = 0.35;
            chip.Cursor = System.Windows.Input.Cursors.Arrow;
            chip.ToolTip = "כבר מאוחר מדי היום";
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true; // inert, and never a drag
            return chip;
        }
        if (pick.Heard) chip.ToolTip = "Palon שמע את זה בשיחה";
        var rest = chip.Background;
        var hover = pick.Heard ? DockPalette.HeardFill : DockPalette.SecondaryHover;
        chip.MouseEnter += (_, _) => chip.Background = hover;
        chip.MouseLeave += (_, _) => chip.Background = rest;
        DockKit.Clickable(chip, onPick);
        return chip;
    }

    // ---- 2 · when a callback is due ---------------------------------------------------

    FrameworkElement BuildCallbackCard(Callback callback, bool missed)
    {
        var now = DateTime.Now;
        var who = DockText.Who(callback);
        var subtitle = DockKit.Text(DockText.DueLine(callback.DueAtUtc.ToLocalTime(), now, missed),
            Font.Body, missed ? DockPalette.OverdueText : DockPalette.Muted);
        subtitle.FlowDirection = FlowDirection.RightToLeft;
        var header = CardHeader(DockText.TimeToCall(who), subtitle, null);
        // Palon glances up once as the card lands, then settles.
        if (_cardAvatar is { } avatar)
        {
            avatar.Mood = PalonMood.Listen;
            var glance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            glance.Tick += (_, _) =>
            {
                glance.Stop();
                if (avatar.Mood == PalonMood.Listen) avatar.Mood = PalonMood.Idle;
            };
            glance.Start();
        }

        var (confirm, badge, confirmText, undoButton) = ConfirmRow();
        Callback? before = null; // the record as it was before our action, for undo

        WrapPanel actions = null!; // assigned below; the Open handler runs only after
        var buttons = new List<Border>();
        if (callback.HasPhone)
        {
            var copy = DockKit.Button(callback.Phone!, DockKit.Kind.Primary, null, DockKit.IconCopy);
            DockKit.LabelOf(copy).FlowDirection = FlowDirection.LeftToRight;
            copy.ToolTip = "העתק את המספר";
            var flash = DockKit.Flasher(copy, callback.Phone!);
            DockKit.Clickable(copy, () => flash(TryCopy(callback.Phone!) ? "הועתק ✓" : "נכשל"));
            buttons.Add(copy);
        }
        else if (callback.HasUrl)
        {
            buttons.Add(DockKit.Button("פתח", DockKit.Kind.Primary, () =>
            {
                _cardResolved = true;
                ReminderOpenRequested?.Invoke(callback); // opens the link, marks it done
                ShowResolved(DockPalette.Done, DockText.RemainingToday(RemainingToday()), undoable: false);
            }));
        }
        buttons.Add(DockKit.Button("עוד 10 דק׳", DockKit.Kind.Secondary, () => Snooze(SnoozeKind.TenMinutes)));
        buttons.Add(DockKit.Button("עוד שעה", DockKit.Kind.Secondary, () => Snooze(SnoozeKind.OneHour)));
        buttons.Add(DockKit.Button("בוצע", DockKit.Kind.Secondary, Done));
        actions = ButtonRow(buttons.ToArray());

        var card = new StackPanel();
        card.Children.Add(header);
        if (callback.Note.Length > 0 && callback.Note != who) card.Children.Add(Well(callback.Note, null));
        card.Children.Add(actions);
        card.Children.Add(confirm);
        DockKit.Clickable(undoButton, Undo);
        return card;

        void Done()
        {
            before = Current(callback);
            _cardResolved = true;
            CallbackStore.Mutate(callback.Id, c => CallbackPlanner.MarkDone(c, DateTime.UtcNow));
            _cardAvatar?.Cheer();
            ShowResolved(DockPalette.Done, DockText.RemainingToday(RemainingToday()), undoable: true);
        }

        void Snooze(SnoozeKind kind)
        {
            before = Current(callback);
            _cardResolved = true;
            var target = CallbackPlanner.SnoozeTargetLocal(kind, DateTime.Now);
            CallbackStore.Mutate(callback.Id, c => CallbackPlanner.Snooze(c, target.ToUniversalTime()));
            ShowResolved(DockPalette.Muted, DockText.SnoozedUntil(target), undoable: true);
        }

        void ShowResolved(Brush badgeFill, string message, bool undoable)
        {
            badge.Background = badgeFill;
            confirmText.Text = message;
            undoButton.Visibility = undoable ? Visibility.Visible : Visibility.Collapsed;
            Swap(actions, confirm);
            DockKit.Pop(badge);
            RefitCard();
            RefreshDayStats();
            _cardConfirm.Stop();
            _cardConfirm.Start(); // hold the confirmation, then collapse (or the next card)
        }

        void Undo()
        {
            if (before is not { } original) return;
            _cardConfirm.Stop();
            try
            {
                CallbackStore.Update(original); // back to exactly how it was
            }
            catch (Exception ex)
            {
                Log.Write($"Dock callback undo failed: {ex.Message}");
            }
            before = null;
            _cardResolved = false;
            Swap(confirm, actions);
            RefitCard();
            RefreshDayStats();
        }
    }

    static Callback Current(Callback callback)
    {
        try
        {
            return CallbackStore.Load().Find(c => c.Id == callback.Id) ?? callback;
        }
        catch
        {
            return callback;
        }
    }

    static int RemainingToday()
    {
        try
        {
            return CallbackPlanner.Counts(CallbackStore.Load(), DateTime.Now).Badge;
        }
        catch
        {
            return 0;
        }
    }
}
