using System.Diagnostics;
using System.Windows;
using Palon.Agentic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>Stroke icons the Now window adds to the Terminal set (24-unit grid).</summary>
static class NowIcons
{
    public const string Phone = "M5,4 H8.5 L10.5,9 L8,10.5 A11,11 0 0 0 13.5,16 L15,13.5 L20,15.5 V19 A2,2 0 0 1 18,21 A16,16 0 0 1 3,6 A2,2 0 0 1 5,4";
    public const string Wave = "M3,12 H5 M7,8 V16 M11,5 V19 M15,8 V16 M19,11 V13";
    public const string Cloud = "M7,18 H17 A4,4 0 0 0 17.5,10 A6,6 0 0 0 6,9.5 A4.3,4.3 0 0 0 7,18";
    public const string Send = "M4,12 L20,4 L14,20 L11,13 Z M11,13 L20,4";
    public const string Cal = "M4,6 H20 V20 H4 Z M4,10 H20 M8,3 V7 M16,3 V7";
    public const string Copy = "M9,9 H19 V19 H9 Z M5,15 V5 H15";
    public const string Eye = "M2.5,12 C5,7 8.5,5 12,5 C15.5,5 19,7 21.5,12 C19,17 15.5,19 12,19 C8.5,19 5,17 2.5,12 Z M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12";
    public const string Receipt = "M6,3 H18 V21 L15,19 L12,21 L9,19 L6,21 Z M9,8 H15 M9,12 H15";
    public const string Deal = "M12,5 V19 M5,12 H19";
    public const string Sparkle = "M12,3 L13.8,10.2 L21,12 L13.8,13.8 L12,21 L10.2,13.8 L3,12 L10.2,10.2 Z";
    public const string Moon = "M20,14.5 A8.5,8.5 0 1 1 9.5,4 A7,7 0 0 0 20,14.5 Z";
}

sealed partial class NowScreen
{
    string? _shownCardId;
    bool _flinging;
    bool _riseNext;

    static SolidColorBrush KickerTone(NowCard c) => c.Kind switch
    {
        NowKind.Callback => c.Overdue ? Tone.B("#FFF2C48D") : Tone.B("#FFC7C7CC"),
        NowKind.Heard or NowKind.Salesforce => Tone.B("#FFB9CCEA"),
        _ => Tone.B("#FFC7C7CC"),
    };

    static string KindIcon(NowKind k) => k switch
    {
        NowKind.Callback => NowIcons.Phone,
        NowKind.Heard => NowIcons.Wave,
        NowKind.Salesforce => NowIcons.Cloud,
        NowKind.NextStep => NowIcons.Phone,
        _ => NowIcons.Send,
    };

    void RenderStack(bool animate)
    {
        if (_flinging) return;
        var top = _cards.FirstOrDefault();
        _stackMore.Text = _cards.Count > 1 ? $"עוד {_cards.Count - 1}" : "";
        if (top?.Id == _shownCardId && _stackHost.Children.Count > 0 && top is not null)
        {
            // Same card: refresh its content in place, no motion.
            _stackHost.Children.Clear();
            BuildStack(top, rise: false);
            return;
        }
        _stackHost.Children.Clear();
        _shownCardId = top?.Id;
        if (top is null)
        {
            _stackHost.Children.Add(AllClear(animate));
            return;
        }
        BuildStack(top, rise: animate);
    }

    void BuildStack(NowCard top, bool rise)
    {
        if (_cards.Count > 2) _stackHost.Children.Add(Peek(36, 30, 0.35));
        if (_cards.Count > 1) _stackHost.Children.Add(Peek(18, 15, 0.6));
        var card = BuildCard(top);
        _stackHost.Children.Add(card);
        if (!rise) return;
        var xf = new TranslateTransform(0, 15);
        var sc = new ScaleTransform(0.965, 0.965);
        card.RenderTransformOrigin = new Point(0.5, 0);
        card.RenderTransform = new TransformGroup { Children = { sc, xf } };
        card.BeginAnimation(OpacityProperty, Feel.FromTo(0.6, 1, 380));
        xf.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(15, 0, 600, Feel.Spring));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(0.965, 1, 600, Feel.Spring));
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.FromTo(0.965, 1, 600, Feel.Spring));
    }

    static Border Peek(double inset, double drop, double opacity) => new()
    {
        CornerRadius = new CornerRadius(30), Background = Tone.Glass, BorderBrush = Tone.GlassRim, BorderThickness = new Thickness(1),
        Margin = new Thickness(inset, 0, inset, 0), Height = 330, VerticalAlignment = VerticalAlignment.Top,
        RenderTransform = new TranslateTransform(0, drop), Opacity = opacity, IsHitTestVisible = false,
    };

    /// <summary>Empty stack: nothing owed, nothing prepared — pivot to the tools.</summary>
    FrameworkElement AllClear(bool animate)
    {
        var card = Kit.Glass(30, new Thickness(30));
        card.Height = 330;
        card.VerticalAlignment = VerticalAlignment.Top;
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mark = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = Tone.B("#1FA6D6BE"),
            Child = Kit.Icon(Icons.Check, 26, Tone.B("#FFA6D6BE"), 2.2), HorizontalAlignment = HorizontalAlignment.Center,
        };
        col.Children.Add(mark);
        var title = Kit.T("הכל נקי", 26, Tone.Text, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(title);
        var sub = Kit.T("אין חזרה שקבעת שממתינה, ואין משהו שהכנתי שמחכה לאישור. הכלים כאן למטה — או פשוט תגיד לי מה לעשות.", 14.5, Tone.Muted, wrap: true);
        sub.TextAlignment = TextAlignment.Center;
        sub.MaxWidth = 340;
        sub.Margin = new Thickness(0, 6, 0, 0);
        col.Children.Add(sub);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        tools.Children.Add(Kit.Pill("תבניות", PillKind.Secondary, () => _bar.Prefill("תב "), height: 36, fontSize: 13));
        var deal = Kit.Pill("עסקה חדשה", PillKind.Secondary, () => Sheets.NewDeal(Host), height: 36, fontSize: 13);
        deal.Margin = new Thickness(8, 0, 0, 0);
        tools.Children.Add(deal);
        var ask = Kit.Pill("שאל את Palon", PillKind.Ghost, () => _bar.FocusInput(), height: 36, fontSize: 13);
        ask.Margin = new Thickness(8, 0, 0, 0);
        tools.Children.Add(ask);
        col.Children.Add(tools);
        card.Child = col;
        if (animate)
        {
            Kit.Rise(new[] { card });
            Kit.Pop(mark);
        }
        return card;
    }

    Border BuildCard(NowCard c)
    {
        var card = Kit.Glass(30, new Thickness(26, 22, 26, 22));
        card.MinHeight = 330;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Background = Fx.Vertical("#17FFFFFF", "#08FFFFFF");
        var col = new StackPanel();

        var kicker = new StackPanel { Orientation = Orientation.Horizontal };
        var tone = KickerTone(c);
        kicker.Children.Add(Kit.Icon(KindIcon(c.Kind), 15, tone));
        var kt = Fx.Kicker(c.Kicker, tone);
        kt.Margin = new Thickness(8, 0, 0, 0);
        kt.VerticalAlignment = VerticalAlignment.Center;
        kicker.Children.Add(kt);
        col.Children.Add(kicker);

        var who = Kit.T(c.Who, 28, Tone.Text, FontWeights.SemiBold);
        who.Margin = new Thickness(0, 10, 0, 0);
        who.FlowDirection = Bidi.HasRtl(c.Who) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        who.HorizontalAlignment = HorizontalAlignment.Left;
        col.Children.Add(Kit.Auto(who));
        var sub = Kit.Auto(Kit.T(c.Sub, 13.5, Tone.Muted));
        sub.Margin = new Thickness(0, 2, 0, 0);
        col.Children.Add(sub);

        if (c.Lines.Count > 0)
        {
            var lines = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            foreach (var l in c.Lines)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var dot = Kit.Dot(Tone.Faint, 5);
                dot.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(dot);
                row.Children.Add(Kit.Auto(Kit.T(l, 14.5, Tone.Body)));
                lines.Children.Add(row);
            }
            col.Children.Add(lines);
        }
        if (c.Quote is { } quote)
        {
            var q = new StackPanel();
            q.Children.Add(Kit.T(quote, 19, Tone.Text, FontWeights.Light));
            if (c.QuoteSub is { } qs) q.Children.Add(Kit.T(qs, 12.5, Tone.Muted));
            col.Children.Add(new Border
            {
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(18),
                Background = Tone.B("#12B9CCEA"), BorderBrush = Tone.B("#26B9CCEA"), BorderThickness = new Thickness(1), Child = q,
            });
        }

        // A template that fits this person, one click with their name in it.
        if (c.Kind == NowKind.Callback && MatchFor(c) is { } match)
        {
            var first = TemplateFill.FirstName(c.Who);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(Kit.Icon(Icons.Templates, 14, Tone.Muted));
            var lab = Kit.T($"מתאימה: {match.Title}", 13, Tone.Muted);
            lab.Margin = new Thickness(6, 0, 10, 0);
            lab.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lab);
            row.Children.Add(Kit.Pill(first.Length > 0 ? $"העתק עם {first}" : "העתק", PillKind.Ghost, () => CopyTemplate(match, c.Who, "now-card"), height: 28, fontSize: 12.5));
            col.Children.Add(row);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        var primary = Kit.Pill(c.Primary, PillKind.Primary, () => Act(c, null), height: 44, fontSize: 14.5);
        actions.Children.Add(Magnetic(primary));
        foreach (var s in c.Secondary)
        {
            var label = s;
            var b = Kit.Pill(label, PillKind.Secondary, () => Act(c, label), height: 44, fontSize: 14);
            b.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(b);
        }
        var dock = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(actions);
        dock.Children.Add(col);
        card.Child = dock;
        card.MouseEnter += (_, _) => _hero.Gaze = new Point(-0.6, 0.2);
        card.MouseLeave += (_, _) => _hero.Gaze = null;
        return card;
    }

    MessageTemplate? MatchFor(NowCard c)
    {
        if (c.CallbackId is null || _data is null) return null;
        var cb = _data.Callbacks.FirstOrDefault(x => x.Id == c.CallbackId);
        return cb is null ? null : TemplateFill.Suggest(TemplatesStore.Load(), cb.Note);
    }

    /// <summary>The primary button leans toward the pointer (the design's magnetic CTA) —
    /// a transform on a wrapper, so it never touches layout.</summary>
    FrameworkElement Magnetic(FrameworkElement el)
    {
        var move = new TranslateTransform();
        var wrap = new Border { Child = el, RenderTransform = move, Background = Brushes.Transparent, Padding = new Thickness(6), Margin = new Thickness(-6) };
        wrap.MouseMove += (_, e) =>
        {
            if (!Fx.Allowed(Host)) return;
            var p = e.GetPosition(wrap);
            move.X = (p.X - wrap.ActualWidth / 2) / Math.Max(1, wrap.ActualWidth) * 8;
            move.Y = (p.Y - wrap.ActualHeight / 2) / Math.Max(1, wrap.ActualHeight) * 6;
        };
        wrap.MouseLeave += (_, _) =>
        {
            move.BeginAnimation(TranslateTransform.XProperty, Feel.FromTo(move.X, 0, 450, Feel.Spring));
            move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(move.Y, 0, 450, Feel.Spring));
        };
        wrap.MouseEnter += (_, _) =>
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
        };
        return wrap;
    }

    // ---- acting on a card ----------------------------------------------------------------

    void Act(NowCard c, string? secondary)
    {
        var note = c.NoteId is null ? null : _data?.Notes.FirstOrDefault(n => n.Id == c.NoteId);
        try
        {
            switch (c.Kind)
            {
                case NowKind.Callback when c.CallbackId is { } id:
                    ActCallback(c, id, secondary);
                    return;
                case NowKind.Heard when note is not null:
                    if (secondary is null)
                    {
                        var cb = CallbackProposals.Accept(note);
                        Handle(c, cb is null ? "לא הצלחתי לקבוע — ראה לוג" : $"קבעתי · {He.When(cb.DueAtUtc.ToLocalTime(), DateTime.Now)}");
                        if (cb is not null) Say($"קבעתי. {He.When(cb.DueAtUtc.ToLocalTime(), DateTime.Now)} אזכיר לך.", holdMs: 4000);
                    }
                    else
                    {
                        CallbackProposals.Dismiss(note);
                        Handle(c, null);
                    }
                    return;
                case NowKind.Salesforce when note is not null:
                    if (secondary == "העתק סיכום")
                    {
                        Host.CopyWithToast(CallSummary.Format(note, c.Who, note.StartedUtc.ToLocalTime()), "הסיכום הועתק · מוכן להדבקה ב-Salesforce");
                        return;
                    }
                    Handle(c, null);
                    if (secondary is null) SalesforceSheets.LogCall(Host, note, null);
                    return;
                case NowKind.NextStep when secondary is null:
                {
                    var when = CallbackPlanner.NextWorkMorning(DateTime.Now);
                    var phone = note?.Number;
                    var cb = Host.Quietly(() => CallbackStore.Add(c.Sub, when.ToUniversalTime(), name: c.Who.Any(char.IsAsciiDigit) ? null : c.Who,
                        phone: phone, url: Phones.WaMeUrl(phone) ?? "", source: CallbackSource.Manual, callNoteId: c.NoteId));
                    Handle(c, cb is null ? "לא הצלחתי לקבוע — ראה לוג" : $"קבעתי · {He.When(when, DateTime.Now)}");
                    if (cb is not null) Host.Toast($"חזרה ל{c.Who} · {He.When(when, DateTime.Now)}", "בטל", () => CallbackStore.Remove(cb.Id));
                    return;
                }
                case NowKind.Draft when secondary is null:
                {
                    var snapshot = AgenticRouter.Snapshot();
                    var text = snapshot.NamedClients.FirstOrDefault(x => x.Name == c.Who) is { } card && c.NoteId is { } nid
                        ? FollowUpDrafts.Cached(card.Key, nid)?.Text : null;
                    if (text is not null) Host.CopyWithToast(text, $"ההודעה ל{c.Who} הועתקה · מוכנה להדבקה");
                    Handle(c, null);
                    return;
                }
                case NowKind.FollowUp:
                    if (secondary is null && c.TemplateId is { } tid && TemplatesStore.Load().FirstOrDefault(t => t.Id == tid) is { } tpl)
                        CopyTemplate(tpl, c.Who, "now-card", note?.Number);
                    Handle(c, null);
                    return;
            }
            Handle(c, null);
        }
        catch (Exception ex)
        {
            Log.Write($"Now card action failed: {ex}");
            Host.Toast("הפעולה לא הצליחה — ראה לוג");
        }
    }

    void ActCallback(NowCard c, string id, string? secondary)
    {
        var before = _data?.Callbacks.FirstOrDefault(x => x.Id == id);
        if (before is null) return;
        switch (secondary)
        {
            case null:
                Fling(() => Host.Quietly(() => CallbackStore.Mutate(id, x => CallbackPlanner.MarkDone(x, DateTime.UtcNow))));
                Cheer();
                Host.Toast($"בוצע · {before.DisplayLabel}", "בטל", () => CallbackStore.Mutate(id, CallbackPlanner.Reopen));
                return;
            case "פתח קישור":
                try
                {
                    Process.Start(new ProcessStartInfo(before.Url) { UseShellExecute = true })?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Write($"Open callback link failed: {ex.Message}");
                }
                return;
            default:
                var kind = secondary == "מחר בבוקר" ? SnoozeKind.Tomorrow : SnoozeKind.OneHour;
                Fling(() => Host.Quietly(() => CallbackStore.Mutate(id, x => CallbackPlanner.Snooze(x, kind, DateTime.Now))));
                Host.Toast($"{(kind == SnoozeKind.Tomorrow ? "נדחה למחר" : "נדחה בשעה")} · {before.DisplayLabel}", "בטל", () => CallbackStore.Update(before));
                return;
        }
    }

    /// <summary>Marks a prepared card handled (never shown again) and flings it.</summary>
    void Handle(NowCard c, string? toast)
    {
        _state.Handled[c.Id] = DateTime.UtcNow;
        _state.Save();
        Fling(null);
        if (toast is not null) Host.Toast(toast);
    }

    /// <summary>The card flies off to the side; the next one rises from the peek.</summary>
    void Fling(Action? write)
    {
        var card = _stackHost.Children.OfType<Border>().LastOrDefault();
        _shownCardId = null;
        if (card is null || !Fx.Allowed(Host))
        {
            write?.Invoke();
            Host.Refresh();
            return;
        }
        _flinging = true;
        var move = new TranslateTransform();
        var rot = new RotateTransform(0);
        card.RenderTransformOrigin = new Point(0.5, 1);
        card.RenderTransform = new TransformGroup { Children = { rot, move } };
        const int dur = 380;
        move.BeginAnimation(TranslateTransform.XProperty, Feel.To(-520, dur, Motion.Out));
        move.BeginAnimation(TranslateTransform.YProperty, Feel.To(-30, dur, Motion.Out));
        rot.BeginAnimation(RotateTransform.AngleProperty, Feel.To(-9, dur, Motion.Out));
        var fade = Feel.To(0, dur, Motion.Out);
        fade.Completed += (_, _) =>
        {
            _flinging = false;
            _riseNext = true;
            write?.Invoke();
            Host.Refresh();
        };
        card.BeginAnimation(OpacityProperty, fade);
    }
}
