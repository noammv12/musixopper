using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>The rituals: morning briefing, welcome back, end-of-day recap and lights-out, shiny Palon.</summary>
sealed partial class NowScreen
{
    Border? _welcome;
    bool _recapOfferDismissed;

    void OpeningRituals(TermData d)
    {
        try
        {
            if (_shiny) Shiny();
            var onCall = Host.CallStateSource() == CallState.OnCall;
            if (!onCall && NowRituals.ShowMorning(_state.LastMorningLocal, DateTime.Now))
            {
                _state.LastMorningLocal = DateTime.Now;
                _state.Save();
                Morning(d);
            }
            else if (NowRituals.WelcomeBack(_state.LastActiveLocal, DateTime.Now)) WelcomeBack(_state.LastActiveLocal!.Value);
            _state.LastActiveLocal = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Write($"Now rituals failed: {ex}");
        }
    }

    void OnActivated()
    {
        if (_data is null) return;
        if (NowRituals.WelcomeBack(_state.LastActiveLocal, DateTime.Now)) WelcomeBack(_state.LastActiveLocal!.Value);
        _state.LastActiveLocal = DateTime.Now;
    }

    void RenderHeroExtras(TermData d, List<CallRecord> calls)
    {
        foreach (var old in _heroExtras.Children.OfType<FrameworkElement>().Where(e => e.Tag as string == "extra").ToList())
            _heroExtras.Children.Remove(old);
        if (Sales.SalesStats.ShouldRemindToSetTarget(d.Book, d.Now))
        {
            var target = Kit.Pill($"חודש חדש — מה היעד ל{He.Month(d.Now.Month)}?", PillKind.Primary, () => Sheets.NewMonth(Host, d.Now.Year, d.Now.Month), height: 38, fontSize: 13.5);
            target.HorizontalAlignment = HorizontalAlignment.Center;
            target.Tag = "extra";
            target.Margin = new Thickness(0, 0, 0, 8);
            _heroExtras.Children.Add(target);
        }
        var today = calls.Count(c => c.StartedUtc.ToLocalTime().Date == d.Now.Date);
        if (_recapOfferDismissed || !NowRituals.OfferRecap(_state.LastRecapLocal, d.Now, today)) return;
        var pill = Kit.Pill("סיכום היום", PillKind.Secondary, Recap, height: 36, fontSize: 13.5,
            content: Kit.Row(8, Kit.Icon(NowIcons.Moon, 15, Tone.Text), Kit.T("סיכום היום", 13.5, Tone.Text)));
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.Tag = "extra";
        _heroExtras.Children.Add(pill);
    }

    // ---- welcome back ----------------------------------------------------------------------

    void WelcomeBack(DateTime since)
    {
        if (_data is null) return;
        var now = DateTime.Now;
        var items = NowRituals.AwayItems(_data.Notes, _data.Callbacks, since, now);
        var minutes = (int)(now - since).TotalMinutes;
        Say(items.Count == 0 ? "ברוך שובך. הכל שקט פה." : "ברוך שובך. הנה מה שקרה.", holdMs: 5000);
        Cheer(soft: true);
        if (items.Count == 0) return;
        if (_welcome is not null) _heroExtras.Children.Remove(_welcome);
        var card = Fx.Glass2(22, new Thickness(18, 16, 18, 14));
        var col = new StackPanel();
        col.Children.Add(Kit.T($"Palon · בזמן שלא היית ({(minutes >= 90 ? $"{minutes / 60} שע׳" : $"{minutes} דק׳")})", 12.5, Tone.MutedSoft, FontWeights.Medium));
        foreach (var t in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Kit.Icon(Icons.Check, 13, Tone.B("#FFA6D6BE"), 2.2));
            var tb = Kit.T(t, 13.5, Tone.Text, wrap: true);
            tb.Margin = new Thickness(8, 0, 0, 0);
            tb.MaxWidth = 280;
            row.Children.Add(tb);
            col.Children.Add(row);
        }
        var ok = Kit.Pill("תודה, Palon", PillKind.Ghost, () =>
        {
            _heroExtras.Children.Remove(card);
            _welcome = null;
        }, height: 30, fontSize: 12.5);
        ok.HorizontalAlignment = HorizontalAlignment.Left;
        ok.Margin = new Thickness(0, 10, 0, 0);
        col.Children.Add(ok);
        card.Child = col;
        _welcome = card;
        _heroExtras.Children.Insert(0, card);
        if (Fx.Allowed(Host)) Kit.Rise(new[] { card });
    }

    // ---- shiny ----------------------------------------------------------------------------------

    void Shiny()
    {
        _heroGlow.Background = Fx.Glow("#4DECC76E", "#00000000");
        Say("Palon זהב. פעם ב-450 פתיחות — היום שלך.", holdMs: 7000);
        Cheer();
        if (!Fx.Allowed(Host)) return;
        var pulse = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(1600)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3), EasingFunction = Motion.Sine };
        _heroGlow.BeginAnimation(OpacityProperty, pulse);
    }

    // ---- morning briefing --------------------------------------------------------------------

    void Morning(TermData d)
    {
        var moving = Fx.Allowed(Host);
        var avatar = new PalonAvatar { Width = 150, Height = 150, Mood = PalonMood.Talk, HorizontalAlignment = HorizontalAlignment.Center };
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560 };
        col.Children.Add(avatar);
        var date = Kit.T(NowRituals.LongDate(DateTime.Now), 13.5, Tone.Muted);
        date.HorizontalAlignment = HorizontalAlignment.Center;
        date.Margin = new Thickness(0, 18, 0, 0);
        col.Children.Add(date);
        var hello = new TextBlock { FontFamily = Font.Family, FontSize = 42, FontWeight = FontWeights.SemiBold, Foreground = Tone.Text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), MinHeight = 56 };
        col.Children.Add(hello);
        var persona = Kit.T("", 16, Tone.MutedSoft, wrap: true);
        persona.TextAlignment = TextAlignment.Center;
        persona.MaxWidth = 520;
        persona.HorizontalAlignment = HorizontalAlignment.Center;
        persona.Margin = new Thickness(0, 10, 0, 0);
        persona.Visibility = Visibility.Collapsed;
        col.Children.Add(persona);
        var (items, brief) = MorningBrief(d);
        var list = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var rows = new List<FrameworkElement>();
        for (var i = 0; i < items.Count; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            var n = Kit.Mono((i + 1).ToString("00"), 13, Tone.Faint);
            n.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(n);
            var t = Kit.T(items[i], 17, Tone.TextSoft, wrap: true);
            t.MaxWidth = 500;
            t.Margin = new Thickness(14, 0, 0, 0);
            row.Children.Add(t);
            list.Children.Add(row);
            rows.Add(row);
        }
        col.Children.Add(list);
        Action close = () => { };
        var go = Kit.Pill("יאללה", PillKind.Primary, () => close(), height: 46, fontSize: 15.5);
        go.HorizontalAlignment = HorizontalAlignment.Center;
        go.Width = 160;
        go.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(go);

        var text = He.Greeting(DateTime.Now, Settings.RepName);
        if (moving)
        {
            foreach (var r in rows) r.Opacity = 0;
            go.Opacity = 0;
            var i = 0;
            var typer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
            typer.Tick += (_, _) =>
            {
                i++;
                hello.Text = text[..Math.Min(i, text.Length)];
                if (i < text.Length) return;
                typer.Stop();
                avatar.Mood = PalonMood.Happy;
                avatar.Cheer();
                Kit.Rise(rows.Append(go), 120);
                var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                settle.Tick += (_, _) => { settle.Stop(); avatar.Mood = PalonMood.Idle; };
                settle.Start();
            };
            typer.Start();
        }
        else
        {
            hello.Text = text;
            avatar.Mood = PalonMood.Idle;
        }
        var personaCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        close = Host.ShowCurtain(col, dark: false, onClosed: () =>
        {
            personaCts.Cancel();
            Say(HeroLine());
        });
        if (brief is not null && AiChat.HasKey) _ = PersonaLine(brief, persona, personaCts.Token);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => go.Focus());
    }

    /// <summary>The morning lines from the agentic brief (Rituals.Brief + NextSteps.Plan);
    /// the older local list when the brain can't build one.</summary>
    (List<string> Items, Agentic.DailyBriefData? Brief) MorningBrief(TermData d)
    {
        try
        {
            var brief = Agentic.Rituals.Brief(Agentic.AgenticRouter.Snapshot(), Settings.RepName, Agentic.NudgeRules.TipOfTheDay(DateTime.Now));
            return (NowRituals.BriefItems(brief), brief);
        }
        catch (Exception ex)
        {
            Log.Write($"Morning brief failed, using the local list: {ex.Message}");
            return (NowRituals.MorningItems(d.Counts, d.Stats, _cards), null);
        }
    }

    /// <summary>Palon's own opener (AI, persona voice) — fades in under the greeting when it arrives;
    /// the local lines already say everything, so a slow or failed call just shows nothing.</summary>
    async Task PersonaLine(Agentic.DailyBriefData brief, TextBlock target, CancellationToken ct)
    {
        try
        {
            var line = await AiChat.BriefAsync(brief.ToText(DateTime.Now), ct);
            if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(line)) return;
            target.Text = line.Trim();
            target.Visibility = Visibility.Visible;
            if (Fx.Allowed(Host)) Kit.Rise(new[] { target });
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write($"Morning persona line failed: {ex.Message}");
        }
    }

    // ---- end of day ------------------------------------------------------------------------------

    void Recap()
    {
        if (_data is null) return;
        _state.LastRecapLocal = DateTime.Now;
        _state.Save();
        _recapOfferDismissed = true;
        RenderHeroExtras(_data, CallStatsStore.Load());
        var d = _data;
        var moving = Fx.Allowed(Host);
        var score = DayRings.Score(_dayRings);

        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var col = new StackPanel { Width = 460 };
        var avatar = new PalonAvatar { Width = 120, Height = 120, Mood = PalonMood.Happy, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(avatar);
        var title = Kit.T("סיכום היום", 15, Tone.Muted, FontWeights.Medium);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(title);
        var big = Kit.Num("0", 84, Tone.Text, FontWeights.ExtraLight);
        big.HorizontalAlignment = HorizontalAlignment.Center;
        col.Children.Add(big);
        var sub = Kit.T("ציון היום", 13, Tone.Muted);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        col.Children.Add(sub);
        if (moving)
        {
            Kit.Entering = true;
            Kit.CountUp(big, score, v => He.N(v), 1400);
            Kit.Entering = false;
            avatar.Cheer();
        }
        else big.Text = score.ToString();

        var rows = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        var rowEls = new List<FrameworkElement>();
        for (var i = 0; i < _dayRings.Count; i++)
        {
            var r = _dayRings[i];
            var ring = new RingGauge(26, 4) { Margin = new Thickness(0, 0, 12, 0) };
            ring.Progress = r.Fraction;
            var left = Kit.Row(0, ring, Kit.T(r.Name, 15, Tone.TextSoft));
            var bar = Kit.Bar(left, Kit.T($"{He.R1(r.Value)} / {He.R1(r.Goal)}", 15, Tone.Text, FontWeights.Medium));
            var row = new Border { Padding = new Thickness(4, 8, 4, 8), BorderBrush = Tone.Hairline, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            rows.Children.Add(row);
            rowEls.Add(row);
        }
        col.Children.Add(rows);
        // What's still open (unaccepted promises, agreed steps with nothing booked) from the brain's recap.
        Agentic.DayRecapData? recap = null;
        try
        {
            recap = Agentic.Rituals.Recap(Agentic.AgenticRouter.Snapshot());
        }
        catch (Exception ex)
        {
            Log.Write($"Recap data failed: {ex.Message}");
        }
        if (recap is not null && (recap.Highlight is not null || recap.OpenLoops.Count > 0))
        {
            var lines = new List<UIElement>();
            if (recap.Highlight is { } hl) lines.Add(Kit.T(hl, 14, Tone.Text, wrap: true));
            if (recap.OpenLoops.Count > 0)
            {
                lines.Add(Kit.T("נשאר פתוח", 12.5, Tone.Muted, FontWeights.Medium));
                foreach (var loop in recap.OpenLoops.Take(3)) lines.Add(Kit.T($"• {loop.Who} — {loop.Why}", 13.5, Tone.TextSoft, wrap: true));
            }
            var open = new Border
            {
                Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(16), Background = Tone.FillSoft,
                Child = Kit.Col(4, lines.ToArray()),
            };
            col.Children.Add(open);
            rowEls.Add(open);
        }
        var tipText = d.Book is { } book && d.Stats is { } stats ? MonthView.Insights(book, stats, d.Rules).FirstOrDefault() : null;
        if (tipText is not null)
        {
            var tip = new Border
            {
                Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(16), Background = Tone.FillSoft,
                Child = Kit.Col(4, Kit.T("משהו אחד למחר", 12.5, Tone.Muted, FontWeights.Medium), Kit.T(tipText, 14, Tone.Text, wrap: true)),
            };
            col.Children.Add(tip);
            rowEls.Add(tip);
        }
        Action close = () => { };
        var done = Kit.Pill("סיימנו להיום", PillKind.Primary, () => LightsOut(root, d), height: 46, fontSize: 15);
        done.HorizontalAlignment = HorizontalAlignment.Center;
        done.Width = 180;
        done.Margin = new Thickness(0, 22, 0, 0);
        col.Children.Add(done);
        var later = Kit.Pill("עוד לא", PillKind.Ghost, () => close(), height: 34, fontSize: 13);
        later.HorizontalAlignment = HorizontalAlignment.Center;
        later.Margin = new Thickness(0, 6, 0, 0);
        col.Children.Add(later);
        root.Children.Add(col);
        if (moving) Kit.Rise(rowEls, 400);
        close = Host.ShowCurtain(root, dark: false, onClosed: null);
        _closeCurtain = close;
    }

    Action? _closeCurtain;

    void LightsOut(Grid root, TermData d)
    {
        root.Children.Clear();
        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var avatar = new PalonAvatar { Width = 110, Height = 110, Mood = PalonMood.Idle, HorizontalAlignment = HorizontalAlignment.Center, FollowPointer = false };
        var moon = Kit.Icon(NowIcons.Moon, 22, Tone.B("#FFDCC7A0"));
        moon.HorizontalAlignment = HorizontalAlignment.Center;
        col.Children.Add(moon);
        col.Children.Add(avatar);
        var name = Settings.RepName is { Length: > 0 } n ? $", {n}" : "";
        var bye = Kit.T($"לילה טוב{name}", 30, Tone.Text, FontWeights.SemiBold);
        bye.HorizontalAlignment = HorizontalAlignment.Center;
        bye.Margin = new Thickness(0, 16, 0, 0);
        col.Children.Add(bye);
        var tomorrow = d.Callbacks.Where(c => c.IsActive && c.DueAtUtc.ToLocalTime().Date > d.Now.Date).OrderBy(c => c.DueAtUtc).FirstOrDefault();
        var line = $"מחר נמשיך מ-{d.Stats?.Count ?? 0}." + (tomorrow is null ? "" : $" שמרתי לך את {tomorrow.DisplayLabel} ל-{He.When(tomorrow.DueAtUtc.ToLocalTime(), d.Now)}.");
        var t = Kit.T(line, 15, Tone.Muted, wrap: true);
        t.TextAlignment = TextAlignment.Center;
        t.MaxWidth = 420;
        t.Margin = new Thickness(0, 6, 0, 0);
        col.Children.Add(t);
        root.Children.Add(col);
        Host.DarkenCurtain();
        if (Fx.Allowed(Host)) Kit.Rise(new FrameworkElement[] { moon, avatar, bye, t });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _closeCurtain?.Invoke();
            _closeCurtain = null;
        };
        timer.Start();
    }
}
