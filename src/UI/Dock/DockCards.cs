using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Agentic;
using Palon.Notes;
using Palon.Terminal;
using ShapePath = System.Windows.Shapes.Path;

namespace Palon.UI;

/// <summary>
/// The Moment cards: after a call (four time chips — one click books), a
/// callback the user set coming due, one high-priority nudge, and the in-call
/// chips. Which card shows, and in what order, is DockCardQueue's call (pure, tested).
/// </summary>
sealed partial class DockWindow
{
    const double AfterCardWidth = 620;
    const double DueCardWidth = 540;

    readonly DockCardQueue _cards = new();
    string? _cardShownKey;
    bool _cardResolved;        // the user acted on the showing card (store echoes are ours)
    double _cardWidth = AfterCardWidth;
    DispatcherTimer _cardIdle = null!;
    DispatcherTimer _cardTuck = null!;
    Action? _onTuck;           // runs once the card has tucked (the rest flash with undo)
    PalonAvatar? _cardAvatar;

    PalonAvatar? _cardAvatarSlot;

    /// <summary>The one avatar every card uses: moved between card rebuilds
    /// instead of a new instance each time (a detached avatar unhooks from the
    /// frame clock on Unloaded).</summary>
    PalonAvatar CardAvatar(double size, PalonMood mood)
    {
        var a = _cardAvatarSlot ??= new PalonAvatar { VerticalAlignment = VerticalAlignment.Center };
        switch (a.Parent)
        {
            case Panel p: p.Children.Remove(a); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
        a.Width = a.Height = size;
        a.Margin = new Thickness(0);
        Grid.SetColumn(a, 0);
        Grid.SetRow(a, 0);
        a.Mood = mood;
        return a;
    }

    void BuildCardTimers()
    {
        _cardIdle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DockMotion.AfterCallIdleMs) };
        _cardIdle.Tick += (_, _) =>
        {
            _cardIdle.Stop();
            if (_pill.IsMouseOver) return; // MouseLeave re-arms it
            if (_cards.Current is not { Kind: DockCardKind.AfterCall }) return;
            if (_moment == MomentKind.Card) AdvanceCard();
            else RetireQuietly();
        };
        _cardTuck = new DispatcherTimer();
        _cardTuck.Tick += (_, _) =>
        {
            _cardTuck.Stop();
            if (_pill.IsMouseOver)
            {
                _cardTuck.Start(); // still reading (or reaching for undo) — hold
                return;
            }
            if (_moment == MomentKind.Card) AdvanceCard();
            else RetireQuietly();
        };
    }

    void RetireQuietly()
    {
        _cardShownKey = null;
        _cards.Advance();
        var tuck = _onTuck;
        _onTuck = null;
        tuck?.Invoke();
    }

    // ---- entry points -----------------------------------------------------------

    /// <summary>A call's note is ready: the after-call card.</summary>
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

    /// <summary>A callback the user set came due (missed = it fired late).</summary>
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

    void OnNudgeRaised(Nudge nudge)
    {
        if (!DockCard.DockWorthy(nudge)) return; // the Now window shows the rest
        Dispatcher.InvokeAsync(() =>
        {
            if (OnCall) return; // nothing new appears on a call
            // The Now window is in front and already shows it next to Palon — one surface at a time.
            // (Dismissing on either surface goes through NudgeHub, so both stay in sync.)
            if (TerminalWindow.IsForeground) return;
            _cards.Enqueue(DockCard.ForNudge(nudge));
            TryShowCard();
        });
    }

    void OnNudgeDismissed(string id) => Dispatcher.InvokeAsync(() =>
    {
        var key = "nudge:" + id;
        if (_cards.Current?.Key == key && _moment == MomentKind.Card && _cardShownKey == key)
        {
            if (!_cardResolved) AdvanceCard();
        }
        else _cards.Forget(key);
    });

    bool CanShowCard => !_fullscreenHidden && IsVisible && !OnCall
        && _moment is not (MomentKind.Listening or MomentKind.Quick);

    void TryShowCard()
    {
        if (!CanShowCard) return; // stays queued; surfaced when the dock is free again
        if (_cards.Resume() is not { } card) return;
        if (_moment == MomentKind.Card && _cardShownKey == card.Key) return;
        Present(card);
    }

    void Present(DockCard card)
    {
        StopCardTimers();
        _cardResolved = false;
        _onTuck = null;
        FrameworkElement element;
        try
        {
            element = card.Kind switch
            {
                DockCardKind.AfterCall => BuildAfterCallCard(card.Note!),
                DockCardKind.Nudge => BuildNudgeCard(card.Nudge!),
                _ => BuildDueCard(card.Callback!, card.Missed),
            };
        }
        catch (Exception ex)
        {
            Log.Write($"Dock card build failed: {ex.Message}");
            _cards.Advance();
            return;
        }
        _cardWidth = card.Kind == DockCardKind.AfterCall ? AfterCardWidth : DueCardWidth;
        _cardShownKey = card.Key;
        ShowMoment(MomentKind.Card, element);
        if (card.Kind == DockCardKind.AfterCall && !_pill.IsMouseOver) _cardIdle.Start();
    }

    /// <summary>Retire the showing card: the next one takes its place, or the pill settles.</summary>
    void AdvanceCard()
    {
        StopCardTimers();
        _cardShownKey = null;
        _cards.Advance();
        var tuck = _onTuck;
        _onTuck = null;
        if (_moment == MomentKind.Card) Settle();
        tuck?.Invoke();
        RefreshToday();
    }

    void StopCardTimers()
    {
        _cardIdle?.Stop();
        _cardTuck?.Stop();
    }

    void PauseCardIdle() => _cardIdle.Stop();

    void ResumeCardIdle()
    {
        if (_cards.Current is { Kind: DockCardKind.AfterCall } && !_cardTuck.IsEnabled && !_cardResolved)
        {
            _cardIdle.Stop();
            _cardIdle.Start();
        }
    }

    void TuckAfter(int ms)
    {
        _cardIdle.Stop();
        _cardTuck.Stop();
        _cardTuck.Interval = TimeSpan.FromMilliseconds(ms);
        _cardTuck.Start();
    }

    /// <summary>A callback was handled elsewhere (Now window, voice): drop its card quietly.</summary>
    void OnCallbacksChanged() => Dispatcher.InvokeAsync(() =>
    {
        RefreshToday();
        List<Callback> all;
        try
        {
            all = CallbackStore.Load();
        }
        catch
        {
            return;
        }
        bool Stale(DockCard c)
        {
            if (c.Kind != DockCardKind.CallbackDue) return false;
            var live = all.Find(x => x.Id == c.Callback!.Id);
            return live is null || !live.IsActive || live.DueAtUtc != c.Callback!.DueAtUtc;
        }
        foreach (var waiting in _cards.Waiting.Where(Stale).ToList()) _cards.Forget(waiting.Key);
        if (!_cardResolved && _cards.Current is { } cur && Stale(cur))
        {
            if (_moment == MomentKind.Card && _cardShownKey == cur.Key) AdvanceCard();
            else _cards.Forget(cur.Key);
        }
    }, DispatcherPriority.Background);

    // ---- shared pieces -----------------------------------------------------------------

    /// <summary>A "done ✓" row: badge · title · detail · undo. Hidden until shown.</summary>
    static (Border Row, Border Badge, TextBlock Title, TextBlock Detail, Border Undo) DoneRow(double height)
    {
        var badge = DockKit.CheckBadge(DockPalette.Done, 30);
        var title = DockKit.Text("", 15, null, FontWeights.SemiBold);
        var detail = DockKit.Text("", 13, DockPalette.Muted);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        texts.Children.Add(title);
        texts.Children.Add(detail);
        var undo = DockKit.Button("בטל", DockKit.Kind.Ghost, null, height: 30);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(badge);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(undo, 2);
        grid.Children.Add(undo);
        var row = new Border
        {
            Background = DockPalette.DoneWell,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 0, 10, 0),
            Height = height,
            Child = grid,
            Visibility = Visibility.Collapsed,
        };
        return (row, badge, title, detail, undo);
    }

    static void Swap(FrameworkElement hide, FrameworkElement show)
    {
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        DockKit.RiseIn(show, 0);
    }

    static TextBlock Wrapped(string text, double size, Brush brush, FontWeight weight, int lines)
    {
        var tb = DockKit.Text(text, size, brush, weight);
        tb.TextWrapping = TextWrapping.Wrap;
        tb.MaxHeight = Math.Ceiling(size * 1.4 * lines);
        return tb;
    }

    /// <summary>One time chip's data: its top line, when, and an optional quote.</summary>
    sealed record ChipData(string Top, DateTime Due, string? Sub, bool Heard);

    /// <summary>
    /// The chips row. Each chip is one click to book; hovering shows ±15m
    /// micro buttons and the mouse wheel nudges by a quarter hour. The first
    /// chip (Palon's heard time when there is one) is the silver primary.
    /// </summary>
    FrameworkElement ChipRow(IReadOnlyList<ChipData> chips, bool big, Action<DateTime> onPick)
    {
        var row = new UniformGrid { Rows = 1, Columns = Math.Max(1, chips.Count), Margin = new Thickness(-4, 0, -4, 0) };
        for (var i = 0; i < chips.Count; i++)
        {
            var data = chips[i];
            var primary = i == 0;
            var fg = primary ? DockPalette.OnPrimary : DockPalette.Text;
            var sub0 = big ? data.Sub : null;
            var due = data.Due;
            var off = 0;

            var top = DockKit.Text(data.Top, 11.5, primary ? DockPalette.OnPrimary : DockPalette.Muted, FontWeights.Medium);
            top.HorizontalAlignment = HorizontalAlignment.Center;
            top.Opacity = primary ? 0.7 : 1;
            var time = DockKit.Text(TimeLabel(), big ? 20 : 16, fg, FontWeights.SemiBold);
            time.HorizontalAlignment = HorizontalAlignment.Center;
            var sub = DockKit.Text(sub0 ?? "", 10.5, primary ? DockPalette.OnPrimary : DockPalette.Muted);
            sub.HorizontalAlignment = HorizontalAlignment.Center;
            sub.MaxWidth = 130;
            sub.Opacity = 0.75;
            sub.Visibility = string.IsNullOrEmpty(sub0) ? Visibility.Collapsed : Visibility.Visible;
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(top);
            stack.Children.Add(time);
            stack.Children.Add(sub);

            var chip = new Border
            {
                Height = big ? 76 : 56,
                CornerRadius = new CornerRadius(18),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(primary ? 0 : 1),
                BorderBrush = DockPalette.SecondaryStroke,
                Child = stack,
                ToolTip = data.Heard ? "Palon שמע את זה בשיחה · גלגלת = ± רבע שעה" : "לחיצה אחת קובעת · גלגלת = ± רבע שעה",
            };
            DockKit.Paint(chip, primary ? DockKit.Kind.Primary : DockKit.Kind.Secondary);
            DockKit.Clickable(chip, () => onPick(due));

            var minus = MicroButton(DockKit.IconMinus, primary, "− רבע שעה", () => Shift(-15));
            var plus = MicroButton(DockKit.IconPlus, primary, "+ רבע שעה", () => Shift(15));
            minus.HorizontalAlignment = HorizontalAlignment.Left;
            plus.HorizontalAlignment = HorizontalAlignment.Right;

            var wrap = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            wrap.Children.Add(chip);
            wrap.Children.Add(minus);
            wrap.Children.Add(plus);
            wrap.MouseEnter += (_, _) =>
            {
                minus.BeginAnimation(OpacityProperty, DockMotion.To(1, 250));
                plus.BeginAnimation(OpacityProperty, DockMotion.To(1, 250));
            };
            wrap.MouseLeave += (_, _) =>
            {
                minus.BeginAnimation(OpacityProperty, DockMotion.To(0, 200));
                plus.BeginAnimation(OpacityProperty, DockMotion.To(0, 200));
            };
            wrap.MouseWheel += (_, e) =>
            {
                e.Handled = true;
                Shift(e.Delta > 0 ? 15 : -15);
            };
            row.Children.Add(wrap);

            string TimeLabel() => data.Heard ? DockText.When(due, DateTime.Now) : due.ToString("HH:mm", CultureInfo.InvariantCulture);

            void Shift(int minutes)
            {
                var next = DockQuick.Nudge(due, minutes, DateTime.Now);
                if (next == due) return;
                off += (int)(next - due).TotalMinutes;
                due = next;
                DockKit.SetText(time, TimeLabel());
                var label = off == 0 ? sub0 ?? "" : $"{(off > 0 ? "+" : "−")}{Math.Abs(off)} דק׳";
                DockKit.SetText(sub, label);
                sub.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                PauseCardIdle();
            }
        }
        return row;
    }

    static Border MicroButton(Geometry icon, bool onPrimary, string tip, Action onClick)
    {
        var b = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(6, 6, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = onPrimary ? new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) : DockPalette.SecondaryHover,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Opacity = 0,
            Child = DockKit.Icon(icon, 11, onPrimary ? DockPalette.OnPrimary : DockPalette.Text, 2),
        };
        DockKit.Clickable(b, onClick);
        return b;
    }

    /// <summary>Standard chips, moved to obey the user's time-window rules for this client.</summary>
    static List<QuickPick> RuledPicks(DateTime now, string? name, string? number)
    {
        var picks = CallbackPlanner.QuickPicks(now);
        try
        {
            return Palon.Memory.TimeRules.Apply(picks, Palon.Memory.MemoryStore.RulesFor(name, number));
        }
        catch (Exception ex)
        {
            Log.Write($"Dock picks: rule check failed: {ex.Message}");
            return picks;
        }
    }

    static List<ChipData> ChipsFor(DateTime now, string? name, string? number, CallbackProposal? proposal)
    {
        var chips = new List<ChipData>();
        foreach (var pick in DockQuick.Chips(RuledPicks(now, name, number), proposal, now))
        {
            var (top, _) = DockQuick.ChipLines(pick, pick.DueLocal, now);
            string? quote = null;
            if (pick.Heard && proposal is { Phrase.Length: > 0 } p)
            {
                var phrase = p.Phrase.Trim().Trim('"', '״', '\'');
                quote = "״" + (phrase.Length > 22 ? phrase[..22].TrimEnd() + "…" : phrase) + "״";
            }
            chips.Add(new ChipData(top, pick.DueLocal, quote, pick.Heard));
        }
        return chips;
    }

    FrameworkElement Header(UIElement lead, string title, UIElement? tag, string meta, Brush? metaBrush, UIElement? trailing)
    {
        var titleText = DockKit.Text(title, 17, null, FontWeights.SemiBold);
        titleText.MaxWidth = 330;
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(titleText);
        if (tag is not null) titleRow.Children.Add(tag);
        var metaText = DockKit.Text(meta, 12.5, metaBrush ?? DockPalette.Muted);
        metaText.MaxWidth = 440;
        metaText.Margin = new Thickness(0, 2, 0, 0);
        metaText.Visibility = meta.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        titles.Children.Add(titleRow);
        titles.Children.Add(metaText);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(lead);
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        grid.Tag = metaText;
        return grid;
    }

    static Border Pill(string text, Brush fg, Brush fill)
    {
        var t = DockKit.Text(text, 11.5, fg, FontWeights.SemiBold);
        return new Border
        {
            Background = fill,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = t,
        };
    }

    static Border QuietAction(string label, Geometry icon, string tip, Action onClick)
    {
        var b = DockKit.Button(label, DockKit.Kind.Ghost, onClick, icon, height: 30);
        b.ToolTip = tip;
        b.Margin = new Thickness(0, 0, 2, 0);
        return b;
    }

    /// <summary>Label flash on a quiet action ("הועתק ✓"), reverting after a beat.</summary>
    static void FlashLabel(Border button, string message)
    {
        var label = DockKit.LabelOf(button);
        var original = label.Tag as string ?? label.Text;
        label.Tag = original;
        DockKit.SetText(label, message);
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Revert) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            DockKit.SetText(label, original);
        };
        revert.Start();
    }

    // ---- 1 · right after a call ------------------------------------------------------

    FrameworkElement BuildAfterCallCard(CallNote note)
    {
        var now = DateTime.Now;
        string? name = null;
        try
        {
            name = ClientIndex.NameForPhone(CallbackStore.Load(), note.Number);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock name lookup failed: {ex.Message}");
        }
        var who = name ?? DockText.Who(note);
        var first = name is null ? who : TemplateFill.FirstName(name);
        if (name is not null || note.Number is not null) _lastClient = new QuickPerson(who, note.Number);
        RebuildPins();

        // Booked on the call with ⏰? The card adjusts that one instead of adding another.
        Callback? created = TakeCallBooking(note);
        Callback? before = null;
        DateTime? chosen = created?.DueAtUtc.ToLocalTime();

        var hasSummary = note.Summary is { Length: > 0 };
        var meta = $"שיחה {DockText.Iso(DockText.Duration(note.DurationSec))} · {(hasSummary ? "הסיכום מוכן" : "אין סיכום")}"
                   + (created is not null ? $" · נקבעה בשיחה ל{DockText.When(created.DueAtUtc.ToLocalTime(), now)}" : "");
        _cardAvatar = CardAvatar(40, PalonMood.Idle);
        var close = DockKit.IconButton(DockKit.IconClose, "סגור", AdvanceCard);
        var header = Header(_cardAvatar, who, null, meta, null, close);

        var (booked, badge, bookedTitle, bookedWhen, undoButton) = DoneRow(76);
        TextBlock hint = null!;
        FrameworkElement chipsHost = null!;
        chipsHost = ChipRow(ChipsFor(now, name, note.Number, created is null ? note.ProposedCallback : null), big: true, due => Book(due));
        chipsHost.Margin = new Thickness(-4, 16, -4, 0);
        booked.Margin = new Thickness(0, 16, 0, 0);
        bookedTitle.Text = "חזרה נקבעה";
        DockKit.Clickable(undoButton, Undo);

        var summary = QuietAction("סיכום", DockKit.IconCopy, "העתק סיכום מוכן ל-Salesforce: שם, תאריך, משך, סיכום וצעד הבא", () => { });
        DockKit.Clickable(summary, () =>
        {
            FlashLabel(summary, DockKit.TryCopy(DockText.SalesforceSummary(note, chosen, DateTime.Now)) ? "הועתק ✓" : "נכשל");
            ResumeCardIdle();
        });
        var template = TemplateAction(note, name);
        var salesforce = QuietAction("Salesforce", DockKit.IconCloud, "תיעוד השיחה ב-Salesforce", () => DockActions.LogToSalesforce(note, chosen));
        hint = DockKit.Text("לחיצה אחת קובעת · ± רבע שעה בריחוף", 11.5, DockPalette.Faint);
        var footer = new Grid { Margin = new Thickness(-6, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(summary);
        if (template is not null) actions.Children.Add(template);
        actions.Children.Add(salesforce);
        footer.Children.Add(actions);
        hint.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(hint, 1);
        footer.Children.Add(hint);

        var card = new StackPanel { Margin = new Thickness(20, 18, 20, 14) };
        card.Children.Add(header);
        card.Children.Add(chipsHost);
        card.Children.Add(booked);
        card.Children.Add(footer);
        return card;

        void Book(DateTime due)
        {
            var target = DockQuick.ApplyRules(due, RulesFor(name, note.Number));
            Callback? result;
            try
            {
                if (created is { } existing)
                {
                    before ??= existing;
                    result = CallbackStore.Mutate(existing.Id, c => c with { DueAtUtc = target.ToUniversalTime() });
                }
                else if (note.ProposedCallback is { IsPending: true })
                    result = CallbackProposals.Accept(note, target.ToUniversalTime());
                else
                {
                    var text = NoteBrief.KeyLine(note.Summary ?? "") is { Length: > 0 } key ? key : "חזרה אחרי שיחה";
                    result = CallbackStore.Add(text, target.ToUniversalTime(), name: name, phone: note.Number,
                        source: CallbackSource.AutoCall, callNoteId: note.Id);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Dock callback booking failed: {ex.Message}");
                result = null;
            }
            if (result is null)
            {
                DockKit.SetText(hint, "לא הצלחתי לקבוע — נסה מ-Now");
                hint.Foreground = DockPalette.OverdueText;
                return;
            }
            created = result;
            chosen = target;
            _cardResolved = true;
            var when = DockText.When(target, DateTime.Now);
            DockKit.SetText(bookedWhen, when);
            Swap(chipsHost, booked);
            DockKit.Pop(badge);
            _cardAvatar?.Cheer();
            _restAvatar.Cheer();
            DockKit.SetText(hint, "נסגר לבד");
            hint.Foreground = DockPalette.Faint;
            Refit();
            RefreshToday();
            _onTuck = () => Flash($"{first} · {when}", null, null, UndoFromFlash);
            TuckAfter(DockMotion.BookedHoldMs);
        }

        void Undo()
        {
            _cardTuck.Stop();
            _onTuck = null;
            RevertBooking();
            _cardResolved = false;
            Swap(booked, chipsHost);
            DockKit.SetText(hint, "לחיצה אחת קובעת · ± רבע שעה בריחוף");
            Refit();
            RefreshToday();
            ResumeCardIdle();
        }

        void UndoFromFlash()
        {
            RevertBooking();
            RefreshToday();
        }

        void RevertBooking()
        {
            if (created is not { } booking) return;
            try
            {
                if (before is { } original) CallbackStore.Update(original); // moved a call booking: put it back
                else
                {
                    CallbackStore.Remove(booking.Id);
                    // A heard proposal goes back to pending, as if never accepted.
                    if (note.ProposedCallback is { IsPending: true }) NotesStore.Update(note);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Dock callback undo failed: {ex.Message}");
            }
            if (before is null) created = null;
            else created = before;
            before = null;
            chosen = created?.DueAtUtc.ToLocalTime();
        }
    }

    /// <summary>The ⏰ booking made on the call that just produced this note (then forgotten).</summary>
    Callback? TakeCallBooking(CallNote note)
    {
        if (_lastCallBooking is not { } b) return null;
        _lastCallBooking = null;
        if (DateTime.UtcNow - b.CreatedUtc > TimeSpan.FromHours(1)) return null; // a stale one from an earlier call
        if (b.Phone is not null && note.Number is not null && !Palon.Agent.PhoneMatch.Same(b.Phone, note.Number)) return null;
        try
        {
            return CallbackStore.Load().Find(c => c.Id == b.Id && c.IsActive);
        }
        catch
        {
            return null;
        }
    }

    Callback? _lastCallBooking;

    /// <summary>"תבנית": the template that fits this call (else the first pin), filled with the first name.</summary>
    Border? TemplateAction(CallNote note, string? name)
    {
        List<MessageTemplate> templates;
        try
        {
            templates = TemplatesStore.Load();
        }
        catch (Exception ex)
        {
            Log.Write($"Dock templates load failed: {ex.Message}");
            return null;
        }
        var fit = TemplateFill.Suggest(templates, (note.Summary ?? "") + "\n" + note.Transcript)
                  ?? DockPins.Pick(templates, Settings.DockPinnedTemplates).FirstOrDefault();
        if (fit is null) return null;
        var first = TemplateFill.FirstName(name);
        var button = QuietAction("תבנית", DockKit.IconTemplate,
            first.Length > 0 ? $"{fit.Title} · עם השם {first}" : fit.Title, () => { });
        DockKit.Clickable(button, () =>
        {
            var text = TemplateFill.Fill(fit, first);
            var ok = DockKit.TryCopy(text);
            FlashLabel(button, ok ? "הועתק ✓" : "נכשל");
            if (ok)
            {
                try
                {
                    TemplateLearningStore.LogCopy(fit, name, note.Number, text, null, "dock");
                }
                catch (Exception ex)
                {
                    Log.Write($"Dock template log failed: {ex.Message}");
                }
            }
            ResumeCardIdle();
        });
        return button;
    }

    // ---- 2 · a callback the user set comes due ---------------------------------------------

    FrameworkElement BuildDueCard(Callback callback, bool missed)
    {
        var now = DateTime.Now;
        var who = DockText.Who(callback);
        var due = callback.DueAtUtc.ToLocalTime();
        var tag = missed
            ? Pill($"באיחור · {due:HH:mm}", DockPalette.OverdueText, new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x45, 0x3A)))
            : Pill($"עכשיו · {due:HH:mm}", DockPalette.OnCall, DockPalette.OnCallSoft);
        var dot = new Ellipse { Width = 12, Height = 12, Fill = missed ? DockPalette.Overdue : DockPalette.OnCall, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        var ctx = callback.Note.Length > 0 && callback.Note != who ? callback.Note.Replace('\n', ' ') : "";
        var header = Header(dot, who, tag, ctx, null, null);
        if (!string.IsNullOrEmpty(callback.Name) || callback.HasPhone)
            _lastClient = new QuickPerson(callback.Name ?? callback.Phone!, callback.Phone);

        var (doneRow, badge, doneTitle, doneDetail, undoButton) = DoneRow(56);
        doneRow.Margin = new Thickness(0, 16, 0, 0);
        Callback? before = null;

        var actions = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Border primary;
        var showDone = true;
        if (callback.HasPhone)
        {
            primary = DockKit.Button("העתק מספר", DockKit.Kind.Primary, null, DockKit.IconCopy, height: 52);
            var number = DockKit.Numeric(callback.Phone!, 13, DockPalette.OnPrimary);
            number.Opacity = 0.6;
            number.Margin = new Thickness(10, 0, 0, 0);
            ((StackPanel)primary.Child).Children.Add(number);
            primary.ToolTip = callback.Phone;
            DockKit.Clickable(primary, () =>
            {
                var ok = DockKit.TryCopy(callback.Phone!);
                var label = DockKit.LabelOf(primary);
                DockKit.SetText(label, ok ? "הועתק" : "נכשל");
                if (ok && ((StackPanel)primary.Child).Children[0] is ShapePath icon) icon.Data = DockKit.IconCheck;
            });
        }
        else if (callback.HasUrl)
        {
            primary = DockKit.Button("פתח", DockKit.Kind.Primary, () =>
            {
                _cardResolved = true;
                ReminderOpenRequested?.Invoke(callback); // opens the link, marks it done
                ShowResolved(DockPalette.Done, DockText.DoneLeft(RemainingToday()), "", undoable: false);
            }, height: 52);
        }
        else
        {
            primary = DockKit.Button("בוצע", DockKit.Kind.Primary, Done, DockKit.IconCheck, height: 52);
            showDone = false;
        }
        primary.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(primary);
        if (showDone)
        {
            var done = DockKit.Button("בוצע", DockKit.Kind.Secondary, Done, height: 52);
            done.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(done, 1);
            actions.Children.Add(done);
        }
        var snooze = DockKit.Button("עוד 10 דק׳", DockKit.Kind.Secondary, Snooze, height: 52);
        snooze.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(snooze, 2);
        actions.Children.Add(snooze);

        var card = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        card.Children.Add(header);
        card.Children.Add(actions);
        card.Children.Add(doneRow);
        DockKit.Clickable(undoButton, Undo);
        return card;

        void Done()
        {
            before = Current(callback);
            _cardResolved = true;
            CallbackStore.Mutate(callback.Id, c => CallbackPlanner.MarkDone(c, DateTime.UtcNow));
            _restAvatar.Cheer();
            ShowResolved(DockPalette.Done, DockText.DoneLeft(RemainingToday()), "", undoable: true);
        }

        void Snooze()
        {
            before = Current(callback);
            _cardResolved = true;
            var target = CallbackPlanner.SnoozeTargetLocal(SnoozeKind.TenMinutes, DateTime.Now);
            CallbackStore.Mutate(callback.Id, c => CallbackPlanner.Snooze(c, target.ToUniversalTime()));
            ShowResolved(DockPalette.Muted, DockText.SnoozedUntil(target), "", undoable: true);
        }

        void ShowResolved(Brush badgeFill, string title, string detail, bool undoable)
        {
            badge.Background = badgeFill;
            DockKit.SetText(doneTitle, title);
            DockKit.SetText(doneDetail, detail);
            doneDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            undoButton.Visibility = undoable ? Visibility.Visible : Visibility.Collapsed;
            Swap(actions, doneRow);
            DockKit.Pop(badge);
            Refit();
            RefreshToday();
            TuckAfter(DockMotion.DoneHoldMs);
        }

        void Undo()
        {
            if (before is not { } original) return;
            _cardTuck.Stop();
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
            Swap(doneRow, actions);
            Refit();
            RefreshToday();
        }
    }

    // ---- 3 · one nudge -----------------------------------------------------------------------

    FrameworkElement BuildNudgeCard(Nudge nudge)
    {
        var avatar = CardAvatar(36, PalonMood.Talk);
        var head = new Grid();
        var glance = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        glance.Tick += (_, _) =>
        {
            glance.Stop();
            if (ReferenceEquals(avatar.Parent, head)) avatar.Mood = PalonMood.Idle; // still this card's
        };
        glance.Start();
        var kind = nudge.Kind == NudgeKind.Reminder ? "תזכורת" : "הצעה";
        var title = Wrapped(nudge.Text, 15, DockPalette.Text, FontWeights.SemiBold, 2);
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(avatar);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 0, 0, 4) };
        tagRow.Children.Add(Pill($"Palon · {kind}", DockPalette.NudgeTag, DockPalette.NudgeTagFill));
        texts.Children.Add(tagRow);
        texts.Children.Add(title);
        Grid.SetColumn(texts, 1);
        head.Children.Add(texts);

        var (doneRow, badge, doneTitle, _, undo) = DoneRow(52);
        undo.Visibility = Visibility.Collapsed;
        doneRow.Margin = new Thickness(0, 16, 0, 0);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        if (nudge.Act is { } act)
        {
            var go = DockKit.Button(nudge.ActionLabel is { Length: > 0 } l ? l : "כן", DockKit.Kind.Primary, null, height: 46);
            go.MinWidth = 180;
            DockKit.Clickable(go, async () =>
            {
                _cardResolved = true;
                DockKit.SetText(DockKit.LabelOf(go), "רגע…");
                try
                {
                    await act();
                    DockKit.SetText(doneTitle, "בוצע");
                    badge.Background = DockPalette.Done;
                }
                catch (Exception ex)
                {
                    Log.Write($"Nudge action failed: {ex.Message}");
                    DockKit.SetText(doneTitle, "לא הצליח — נסה מ-Now");
                    badge.Background = DockPalette.Muted;
                }
                NudgeHub.Dismiss(nudge.Id);
                Swap(actions, doneRow);
                DockKit.Pop(badge);
                Refit();
                TuckAfter(DockMotion.DoneHoldMs);
            });
            actions.Children.Add(go);
        }
        var later = DockKit.Button("אחר כך", DockKit.Kind.Secondary, () =>
        {
            _cardResolved = true;
            NudgeHub.Dismiss(nudge.Id);
            AdvanceCard();
        }, height: 46);
        later.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(later);

        var card = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        card.Children.Add(head);
        card.Children.Add(actions);
        card.Children.Add(doneRow);
        return card;
    }

    // ---- 4 · the in-call chips (⏰ pressed twice) ----------------------------------------------

    TextBlock? _chipsTimer;

    void ShowCallChips()
    {
        var now = DateTime.Now;
        var dot = new Ellipse { Width = 9, Height = 9, Fill = DockPalette.OnCall, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var q = DockKit.Text(_callDisplayName is { } n ? $"מתי לחזור ל{(Bidi.HasRtl(n[..1]) ? n : "-" + DockText.Iso(n))}?" : "מתי לחזור?", 14, null, FontWeights.SemiBold);
        _chipsTimer = DockKit.Numeric(DockText.Timer(DateTime.UtcNow - _callStartedUtc), 14, DockPalette.OnCall, FontWeights.Medium);
        _chipsTimer.Margin = new Thickness(10, 0, 10, 0);
        var close = DockKit.IconButton(DockKit.IconClose, "סגור", () => SetMode(DockMode.Rest));
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(dot);
        Grid.SetColumn(q, 1);
        head.Children.Add(q);
        Grid.SetColumn(_chipsTimer, 2);
        head.Children.Add(_chipsTimer);
        Grid.SetColumn(close, 3);
        head.Children.Add(close);

        var chips = ChipRow(ChipsFor(now, _callDisplayName, _callNumber, null), big: false, due =>
        {
            if (_callBooked is { } b)
            {
                var target = DockQuick.ApplyRules(due, RulesFor(_callDisplayName, _callNumber));
                _callBooked = CallbackStore.Mutate(b.Id, c => c with { DueAtUtc = target.ToUniversalTime() }) ?? b;
                SetCallSub(CallBookedLine(), UndoCallBooking);
                RefreshToday();
            }
            SetMode(DockMode.Rest);
        });
        chips.Margin = new Thickness(-4, 12, -4, 0);
        var panel = new StackPanel { Margin = new Thickness(18, 12, 12, 14) };
        panel.Children.Add(head);
        panel.Children.Add(chips);
        ShowMoment(MomentKind.CallChips, panel);
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
