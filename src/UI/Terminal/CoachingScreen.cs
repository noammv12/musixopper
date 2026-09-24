using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Palon.Coaching;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Call coaching, private and local: the week's talk/listen numbers against
/// last week, objections heard, phrases that go with deposits, the best
/// call (with one lazily-generated line on why), Palon's tips, and a
/// drill-down per call. Everything shown is computed on this PC; the only
/// AI request here is the cached best-call line.
/// </summary>
sealed class CoachingScreen : TerminalScreen
{
    const string ChevronPrev = "M9,6 L15,12 L9,18";  // RTL: "back" points right
    const string ChevronNext = "M15,6 L9,12 L15,18";

    DateTime? _week;
    static readonly HashSet<string> WhyRequested = new();

    public CoachingScreen(TerminalWindow host) : base(host)
    {
    }

    public override string Title => "אימון";

    public override void Render(TermData d, bool entrance)
    {
        Children.Clear();
        var thisWeek = CoachReports.WeekStart(d.Now);
        var week = _week ?? thisWeek;
        var records = CoachStore.Load();
        var deals = SalesStore.ListMonths().SelectMany(m => m.Deals).ToList();
        var r = CoachReports.Build(records, deals, week);

        var grid = FourColumns();
        var cards = new List<FrameworkElement>();
        void Put(FrameworkElement el, int row, int col, int span = 1)
        {
            Place(grid, el, row, col, span);
            cards.Add(el);
        }

        Put(HeaderRow(r, week, thisWeek), 0, 0, 4);
        Put(PalonCard(r), 1, 0, 4);

        var k = r.This;
        var p = r.Previous;
        Put(Tile("דיבור שלך", k.TalkPct, v => $"{v:0}%", p.TalkPct, lowerIsBetter: null,
            foot: $"ייחוס: {CoachReports.ReferenceTalkPct:0}%"), 2, 0);
        Put(Tile("מונולוג ארוך", k.LongestMonologueSec, v => Secs(v), p.LongestMonologueSec, lowerIsBetter: true,
            foot: "ממוצע לשיחה"), 2, 1);
        Put(Tile("שאלות לשיחה", k.QuestionsPerCall, v => v.ToString("0.#", CultureInfo.InvariantCulture), p.QuestionsPerCall, lowerIsBetter: false,
            foot: "שאלות ששאלת"), 2, 2);
        Put(Tile("צעד הבא נקבע", k.NextStepRate, v => $"{v:0}%", p.NextStepRate, lowerIsBetter: false,
            foot: k.AvgResponseSec is { } lat ? $"זמן תגובה {lat:0.0} ש׳" : "מהשיחות"), 2, 3);

        Put(ObjectionsCard(r), 3, 0, 2);
        Put(BestCard(r, week), 3, 2, 2);
        Put(PhrasesCard("הולך עם הפקדה", r.DepositPhrases, Tone.GreenText, r.PhraseWindowCalls), 4, 0, 2);
        Put(PhrasesCard("הולך בלי הפקדה", r.OtherPhrases, Tone.MutedSoft, r.PhraseWindowCalls), 4, 2, 2);
        Put(CallsCard(r), 5, 0, 4);

        Children.Add(grid);
        if (entrance) Kit.Rise(cards);
    }

    // ---- header + week picker -------------------------------------------------------

    FrameworkElement HeaderRow(WeekReport r, DateTime week, DateTime thisWeek)
    {
        var end = week.AddDays(6);
        var label = week == thisWeek ? "השבוע" : week == thisWeek.AddDays(-7) ? "שבוע שעבר" : $"{He.DayMonth(week)}–{He.DayMonth(end)}";
        var head = Header("אימון", $"{label} · {r.This.Calls} שיחות · הכל נשאר במחשב שלך", out _);

        var picker = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var prev = Kit.IconButton(ChevronPrev, 38, () => Go(week.AddDays(-7)), PillKind.Secondary);
        prev.ToolTip = "שבוע קודם";
        var range = Kit.Num($"{He.DayMonth(week)}–{He.DayMonth(end)}", 14, Tone.TextSoft, FontWeights.Medium);
        range.Margin = new Thickness(12, 0, 12, 0);
        range.VerticalAlignment = VerticalAlignment.Center;
        range.MinWidth = 86;
        range.TextAlignment = TextAlignment.Center;
        var next = Kit.IconButton(ChevronNext, 38, () => Go(week.AddDays(7)), PillKind.Secondary);
        next.ToolTip = "שבוע הבא";
        if (week >= thisWeek)
        {
            next.IsEnabled = false;
            next.Opacity = 0.35;
        }
        picker.Children.Add(prev);
        picker.Children.Add(range);
        picker.Children.Add(next);
        if (week != thisWeek)
        {
            var now = Kit.Pill("השבוע", PillKind.Ghost, () => Go(thisWeek), height: 34, fontSize: 13);
            now.Margin = new Thickness(8, 0, 0, 0);
            picker.Children.Add(now);
        }
        return Kit.Bar(head, picker);
    }

    void Go(DateTime week)
    {
        _week = week;
        Host.Navigate(TerminalPage.Coaching, force: true);
    }

    // ---- Palon -------------------------------------------------------------------

    static FrameworkElement PalonCard(WeekReport r)
    {
        var card = Kit.Glass(padding: new Thickness(24, 20, 24, 20));
        var tips = CoachTips.For(r);
        var avatar = new PalonAvatar
        {
            Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top,
            Mood = r.This.AvgScore is { } s && r.Previous.AvgScore is { } ps && s > ps ? PalonMood.Happy : PalonMood.Idle,
        };
        var lines = new StackPanel { Margin = new Thickness(18, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Kit.T("Palon על השבוע", 12.5, Tone.Muted));
        for (var i = 0; i < tips.Count; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var dot = Kit.Dot(i == 0 ? Tone.Accent : Tone.Graphite, 6);
            dot.Margin = new Thickness(0, 7, 10, 0);
            dot.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(dot);
            row.Children.Add(Kit.T(tips[i], Display.Body, i == 0 ? Tone.Text : Tone.TextSoft, wrap: true));
            lines.Children.Add(row);
        }
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(avatar);
        Grid.SetColumn(lines, 1);
        g.Children.Add(lines);
        card.Child = g;
        return card;
    }

    // ---- KPI tiles ------------------------------------------------------------------

    static FrameworkElement Tile(string label, double? value, Func<double, string> fmt, double? prev, bool? lowerIsBetter, string foot)
    {
        var card = Kit.Glass(padding: new Thickness(22, 20, 22, 20), lift: true);
        var col = new StackPanel();
        col.Children.Add(Kit.T(label, 13, Tone.Muted));
        var num = Kit.Num(value is null ? "—" : "", Display.NumM, Tone.Text, FontWeights.Light);
        num.Margin = new Thickness(0, 6, 0, 2);
        num.HorizontalAlignment = HorizontalAlignment.Right;
        if (value is { } v) Kit.CountUp(num, v, fmt);
        col.Children.Add(num);

        var footRow = new DockPanel();
        if (value is { } cur && prev is { } before && Math.Abs(cur - before) >= 0.05)
        {
            var delta = cur - before;
            var good = lowerIsBetter is null ? Math.Abs(cur - CoachReports.ReferenceTalkPct) < Math.Abs(before - CoachReports.ReferenceTalkPct)
                : lowerIsBetter.Value ? delta < 0 : delta > 0;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0),
                Background = good ? Tone.B("#1F32D74B") : Tone.B("#1FFF453A"),
                Child = Kit.Num((delta > 0 ? "▲ " : "▼ ") + fmt(Math.Abs(delta)), 11.5, good ? Tone.GreenText : Tone.RedText, FontWeights.SemiBold),
                ToolTip = "לעומת שבוע שעבר",
            };
            DockPanel.SetDock(chip, Dock.Left);
            footRow.Children.Add(chip);
        }
        footRow.Children.Add(Kit.T(foot, 12, Tone.Faint));
        col.Children.Add(footRow);
        card.Child = col;
        return card;
    }

    static string Secs(double s) => s >= 60 ? $"{(int)s / 60}:{(int)s % 60:00}" : $"{s:0}ש׳";

    // ---- objections -------------------------------------------------------------

    static FrameworkElement ObjectionsCard(WeekReport r)
    {
        var card = Kit.Glass(lift: true);
        var col = new StackPanel();
        col.Children.Add(Kit.SectionTitle("התנגדויות"));
        var sub = Kit.T("מה שהלקוחות אמרו — כל אחת מאומתת מול התמלול", 12.5, Tone.Muted);
        sub.Margin = new Thickness(0, 3, 0, 14);
        col.Children.Add(sub);
        if (r.Objections.Count == 0)
            col.Children.Add(Kit.T("לא נשמעו התנגדויות השבוע.", Display.Body, Tone.MutedSoft));
        var max = Math.Max(1, r.Objections.Select(o => o.Count).DefaultIfEmpty(1).Max());
        var i = 0;
        foreach (var (cat, count) in r.Objections)
        {
            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 12, 0, 0) };
            row.Children.Add(Kit.Bar(Kit.T(Objections.Label(cat), 14, Tone.TextSoft), Kit.Num(count.ToString(), 14, Tone.Text, FontWeights.SemiBold)));
            var bar = Kit.HBar((double)count / max, i == 0 ? Tone.Accent : Tone.Graphite, 120 + i * 60, 6);
            bar.Margin = new Thickness(0, 6, 0, 0);
            row.Children.Add(bar);
            col.Children.Add(row);
            i++;
        }
        card.Child = col;
        return card;
    }

    // ---- best call ------------------------------------------------------------------

    FrameworkElement BestCard(WeekReport r, DateTime week)
    {
        var card = Kit.Glass(lift: true);
        var col = new StackPanel();
        col.Children.Add(Kit.Bar(Kit.SectionTitle("השיחה הטובה של השבוע"),
            r.Best is { } b0 ? ScoreBadge(b0.Score) : new Border()));
        if (r.Best is not { } best)
        {
            var none = Kit.T("עוד אין שיחה מדורגת השבוע.", Display.Body, Tone.MutedSoft);
            none.Margin = new Thickness(0, 14, 0, 0);
            col.Children.Add(none);
            card.Child = col;
            return card;
        }
        var local = best.Call.StartedUtc.ToLocalTime();
        var meta = Kit.T($"{He.Day(local.DayOfWeek)} {He.DayMonth(local)} · {He.Clock(local)} · {He.Duration(best.Call.DurationSec)}" +
                         (best.Deal is { } dl ? $" · הפקדה {dl.ClientName}" : ""), 12.5, Tone.Muted);
        meta.Margin = new Thickness(0, 4, 0, 12);
        col.Children.Add(meta);

        var why = Kit.T("", Display.Body, Tone.Text, wrap: true);
        var cached = CoachStore.BestWhy(week, best.Call.Id);
        if (cached is not null) why.Text = cached;
        else if (!AiChat.HasKey) why.Text = FallbackWhy(best);
        else
        {
            why.Text = "Palon מנסח למה…";
            why.Foreground = Tone.MutedSoft;
            RequestWhy(week, best);
        }
        col.Children.Add(why);

        if (best.Call.Summary is { Length: > 0 } sum)
        {
            var s = Kit.T(sum, 13, Tone.MutedSoft, wrap: true);
            s.Margin = new Thickness(0, 10, 0, 0);
            s.MaxHeight = 60;
            s.TextTrimming = TextTrimming.CharacterEllipsis;
            col.Children.Add(s);
        }
        var open = Kit.Pill("לפרטי השיחה", PillKind.Secondary, () => Drill(best), height: 34, fontSize: 13);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 14, 0, 0);
        col.Children.Add(open);
        card.Child = col;
        return card;
    }

    /// <summary>The one AI line, generated once per week+call and cached. Sends
    /// only the local numbers and the short note — never the transcript.</summary>
    void RequestWhy(DateTime week, ScoredCall best)
    {
        var key = week.ToString("yyyy-MM-dd") + best.Call.Id;
        if (!WhyRequested.Add(key)) return;
        var facts = Facts(best);
        _ = Task.Run(async () =>
        {
            string? text = null;
            try
            {
                text = (await AiChat.BestCallWhyAsync(facts, CancellationToken.None))?.Trim().Split('\n')[0];
            }
            catch (Exception ex)
            {
                Log.Write($"Coaching best-call line failed: {ex.Message}");
            }
            CoachStore.SaveBestWhy(week, best.Call.Id, string.IsNullOrWhiteSpace(text) ? FallbackWhy(best) : text!);
            _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) Host.Refresh(); });
        });
    }

    static string Facts(ScoredCall s)
    {
        var m = s.Call.Metrics;
        var c = s.Call.Coach;
        var parts = new List<string> { $"score {s.Score:0}/100", $"duration {s.Call.DurationSec / 60} min" };
        if (m?.TalkPct is { } t) parts.Add($"user talk {t:0}%");
        if (m?.LongestMonologueSec is { } l) parts.Add($"longest monologue {l:0}s");
        if (CoachReports.UserQuestions(s.Call) is int q) parts.Add($"questions asked {q}");
        if (m?.Interruptions is int i) parts.Add($"interruptions {i}");
        if (c?.NextStep is { Agreed: true } ns) parts.Add($"next step agreed: {ns.Text}");
        if (c is not null && c.Objections.Count > 0) parts.Add("objections: " + string.Join(", ", c.Objections.Select(o => o.Category)));
        if (s.Deal is not null) parts.Add("ended in a deposit");
        return string.Join("; ", parts) + (s.Call.Summary is { } sum ? "\nNote: " + sum : "");
    }

    static string FallbackWhy(ScoredCall s)
    {
        var bits = new List<string>();
        if (s.Call.Metrics?.TalkPct is { } t && Math.Abs(t - CoachReports.ReferenceTalkPct) < 12) bits.Add("יחס דיבור מאוזן");
        if (CoachReports.UserQuestions(s.Call) is >= 4) bits.Add("שאלות טובות");
        if (s.Call.Coach?.NextStep is { Agreed: true }) bits.Add("צעד הבא ברור");
        return bits.Count > 0 ? string.Join(", ", bits) + "." : "השיחה המאוזנת ביותר שלך השבוע.";
    }

    static Border ScoreBadge(double score) => new()
    {
        CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3),
        Background = Tone.AccentSoft, BorderBrush = Tone.AccentLine, BorderThickness = new Thickness(1),
        Child = Kit.Num($"{score:0}", 14, Tone.Accent, FontWeights.SemiBold),
        ToolTip = "ציון השיחה (0–100)",
    };

    // ---- phrases ------------------------------------------------------------------

    static FrameworkElement PhrasesCard(string title, List<PhraseStat> phrases, Brush tint, int windowCalls)
    {
        var card = Kit.Glass(lift: true);
        var col = new StackPanel();
        col.Children.Add(Kit.SectionTitle(title));
        var sub = Kit.T($"{CoachReports.PhraseWindowWeeks} שבועות · {windowCalls} שיחות · מופיע ב־{CoachReports.MinPhraseCalls}+ שיחות", 12.5, Tone.Muted);
        sub.Margin = new Thickness(0, 3, 0, 12);
        col.Children.Add(sub);
        if (phrases.Count == 0)
        {
            col.Children.Add(Kit.T("עוד אין מספיק שיחות מקושרות להפקדות כדי לומר משהו אמין.", 14, Tone.MutedSoft, wrap: true));
        }
        var maxZ = phrases.Select(x => Math.Abs(x.Z)).DefaultIfEmpty(1).Max();
        var i = 0;
        foreach (var ph in phrases)
        {
            var text = Kit.Auto(Kit.T($"\"{ph.Phrase}\"", 14.5, Tone.TextSoft));
            var n = Kit.Num($"{ph.DepositCalls}/{ph.DepositCalls + ph.OtherCalls}", 12.5, Tone.Faint);
            n.ToolTip = "שיחות עם הפקדה / כל השיחות שבהן נאמר";
            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 9, 0, 0) };
            row.Children.Add(Kit.Bar(text, n));
            var bar = Kit.HBar(Math.Abs(ph.Z) / Math.Max(0.01, maxZ), tint, 150 + i * 50, 3);
            bar.Margin = new Thickness(0, 5, 0, 0);
            row.Children.Add(bar);
            col.Children.Add(row);
            i++;
        }
        card.Child = col;
        return card;
    }

    // ---- calls + drill-down ----------------------------------------------------------

    FrameworkElement CallsCard(WeekReport r)
    {
        var card = Kit.Glass(radius: Display.ListRadius, padding: new Thickness(14, 18, 14, 14));
        var col = new StackPanel();
        var title = Kit.SectionTitle("שיחות השבוע");
        title.Margin = new Thickness(12, 0, 0, 10);
        col.Children.Add(title);
        if (r.Calls.Count == 0)
        {
            var none = Kit.T("אין שיחות עם נתוני אימון בשבוע הזה.", Display.Body, Tone.MutedSoft);
            none.Margin = new Thickness(12, 0, 0, 6);
            col.Children.Add(none);
        }
        foreach (var s in r.Calls.Take(60))
        {
            var local = s.Call.StartedUtc.ToLocalTime();
            var left = new StackPanel();
            left.Children.Add(Kit.T($"{He.Day(local.DayOfWeek)} {He.DayMonth(local)} · {He.Clock(local)}" +
                                    (s.Call.Number is { } n ? $" · {n}" : ""), 14.5, Tone.Text, FontWeights.Medium));
            var bits = new List<string> { He.Duration(s.Call.DurationSec) };
            if (s.Call.Metrics?.TalkPct is { } t) bits.Add($"דיבור {t:0}%");
            if (CoachReports.UserQuestions(s.Call) is int q) bits.Add($"{q} שאלות");
            if (s.Call.Coach is { } c && c.Objections.Count > 0) bits.Add($"{c.Objections.Count} התנגדויות");
            if (s.Call.Coach?.NextStep is { Agreed: true }) bits.Add("צעד הבא ✓");
            left.Children.Add(Kit.T(string.Join(" · ", bits), 12.5, Tone.Muted));
            var end = new StackPanel { Orientation = Orientation.Horizontal };
            if (s.Deal is not null)
            {
                var dep = Kit.T("הפקדה", 12, Tone.GreenText, FontWeights.SemiBold);
                dep.Margin = new Thickness(0, 0, 10, 0);
                dep.VerticalAlignment = VerticalAlignment.Center;
                end.Children.Add(dep);
            }
            end.Children.Add(ScoreBadge(s.Score));
            var row = Kit.ListRow(Kit.Bar(left, end));
            row.Cursor = Cursors.Hand;
            var call = s;
            Kit.Clickable(row, () => Drill(call));
            Kit.Press(row, 0.985);
            col.Children.Add(row);
        }
        card.Child = col;
        return card;
    }

    void Drill(ScoredCall s)
    {
        Action close = () => { };
        var body = new StackPanel();
        var local = s.Call.StartedUtc.ToLocalTime();
        var titleCol = new StackPanel();
        titleCol.Children.Add(Kit.T($"{He.Day(local.DayOfWeek)} {He.DayMonth(local)} · {He.Clock(local)}", Display.Sheet, Tone.Text, FontWeights.SemiBold));
        titleCol.Children.Add(Kit.T($"{He.Duration(s.Call.DurationSec)}" + (s.Call.Number is { } n ? $" · {n}" : "") +
                                    (s.Deal is { } d ? $" · הפקדה: {d.ClientName}" : ""), 13.5, Tone.MutedSoft));
        var x = Kit.IconButton(Icons.Close, 34, () => close());
        body.Children.Add(Kit.Bar(titleCol, ScoreBadge(s.Score), x));

        var m = s.Call.Metrics;
        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 18, 0, 0) };
        void Stat(string label, string value)
        {
            var c = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            c.Children.Add(Kit.T(label, 12, Tone.Muted));
            c.Children.Add(Kit.Num(value, 20, Tone.Text, FontWeights.Light));
            stats.Children.Add(c);
        }
        Stat("דיבור שלך", m?.TalkPct is { } t ? $"{t:0}%" : "—");
        Stat("מונולוג ארוך", m?.LongestMonologueSec is { } l ? Secs(l) : "—");
        Stat("שאלות ששאלת", CoachReports.UserQuestions(s.Call)?.ToString() ?? "—");
        Stat("קטיעות", m?.Interruptions?.ToString() ?? "—");
        Stat("זמן תגובה", m?.AvgResponseSec is { } rs ? $"{rs:0.0}ש׳" : "—");
        Stat("מילים לדקה", m?.WordsPerMin is { } w ? $"{w:0}" : "—");
        body.Children.Add(stats);
        if (m?.TalkPct is null)
            body.Children.Add(Kit.T("ערוץ אחד חסר בהקלטה — אין יחס דיבור לשיחה הזו.", 12.5, Tone.Faint));

        var c = s.Call.Coach;
        void Section(string title, IEnumerable<(string Tag, string Quote)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            var h = Kit.T(title, 13, Tone.Muted, FontWeights.SemiBold);
            h.Margin = new Thickness(0, 16, 0, 6);
            body.Children.Add(h);
            foreach (var (tag, quote) in list)
            {
                var q = Kit.Auto(Kit.T($"\"{quote}\"", 14.5, Tone.TextSoft, wrap: true));
                var row = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                if (tag.Length > 0) row.Children.Add(Kit.T(tag, 12, Tone.Accent, FontWeights.Medium));
                row.Children.Add(q);
                body.Children.Add(row);
            }
        }
        if (c is null)
        {
            var none = Kit.T("אין ניתוח AI לשיחה הזו (בלי מפתח, או שהתשובה לא עברה אימות).", 13, Tone.Faint, wrap: true);
            none.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(none);
        }
        else
        {
            Section("התנגדויות", c.Objections.Select(o => (Objections.Label(o.Category ?? ""), o.Quote)));
            Section("שאלות של הלקוח", c.ClientQuestions.Select(q => ("", q.Quote)));
            Section("צעד הבא", c.NextStep is { Agreed: true } ns ? new[] { (ns.Text ?? "", ns.Quote ?? "") } : Array.Empty<(string, string)>());
            if (c.NextStep is not { Agreed: true })
            {
                var noStep = Kit.T("לא נקבע צעד הבא.", 13.5, Tone.AmberText);
                noStep.Margin = new Thickness(0, 16, 0, 0);
                body.Children.Add(noStep);
            }
        }
        if (s.Call.Summary is { Length: > 0 } sum)
        {
            var h = Kit.T("הסיכום", 13, Tone.Muted, FontWeights.SemiBold);
            h.Margin = new Thickness(0, 16, 0, 6);
            body.Children.Add(h);
            body.Children.Add(Kit.Auto(Kit.T(sum, 14, Tone.Body, wrap: true)));
        }
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };
        close = Host.ShowSheet(scroll, 560, 700);
    }
}
