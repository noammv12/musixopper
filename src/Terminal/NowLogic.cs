using Palon.Notes;
using Palon.Sales;

namespace Palon.Terminal;

// Pure logic behind the Now window: what the action stack holds, the daily
// rings, the pay streak, the rituals' timing, the command bar's palette and
// Palon's contextual line. No WPF, no disk — everything here is unit-tested.

enum NowKind { Callback, Heard, Salesforce, FollowUp, NextStep, Draft }

/// <summary>
/// One card in the Now action stack. Only things the user asked for (a callback
/// they set that is due) or work Palon already prepared (a heard callback to
/// confirm, a Salesforce log, a drafted follow-up) — never a generic "call this lead".
/// </summary>
sealed record NowCard(
    string Id,
    NowKind Kind,
    string Kicker,
    string Who,
    string Sub,
    IReadOnlyList<string> Lines,
    string Primary,
    IReadOnlyList<string> Secondary,
    string? Quote = null,
    string? QuoteSub = null,
    string? CallbackId = null,
    string? NoteId = null,
    string? TemplateId = null,
    bool Overdue = false);

static class NowStack
{
    public const int MaxCards = 8;
    /// <summary>A callback is "now" this long before it's due.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);
    static readonly TimeSpan HeardWindow = TimeSpan.FromDays(3);
    static readonly TimeSpan SalesforceWindow = TimeSpan.FromHours(12);
    static readonly TimeSpan FollowUpWindow = TimeSpan.FromHours(24);

    public static List<NowCard> Build(
        IEnumerable<Callback> callbacks,
        IEnumerable<CallNote> notes,
        IReadOnlyList<MessageTemplate> templates,
        ISet<string> handled,
        DateTime nowLocal)
    {
        var cbList = callbacks.ToList();
        var noteList = notes.OrderByDescending(n => n.StartedUtc).ToList();
        var cards = new List<NowCard>();

        foreach (var cb in cbList.Where(c => c.IsActive && c.DueAtUtc.ToLocalTime() <= nowLocal + Lead).OrderBy(c => c.DueAtUtc))
        {
            var id = "cb:" + cb.Id;
            if (handled.Contains(id)) continue;
            var due = cb.DueAtUtc.ToLocalTime();
            var overdue = due < nowLocal - CallbackPlanner.MissedThreshold;
            var hasWho = !string.IsNullOrEmpty(cb.Name) || cb.HasPhone;
            var sub = !string.IsNullOrEmpty(cb.Name) && cb.HasPhone ? cb.Phone! : cb.Source == CallbackSource.AutoCall ? "חזרה שקבעת מהשיחה" : "חזרה שקבעת";
            var secondary = new List<string> { "עוד שעה", "מחר בבוקר" };
            if (cb.HasUrl) secondary.Insert(0, "פתח קישור");
            cards.Add(new NowCard(id, NowKind.Callback,
                (overdue ? "חזרה באיחור · " : "חזרה עכשיו · ") + He.When(due, nowLocal),
                cb.DisplayLabel, sub, hasWho ? Lines(cb.Note, 2) : Array.Empty<string>(),
                "חזרתי אליו", secondary, CallbackId: cb.Id, Overdue: overdue));
        }

        foreach (var note in noteList)
        {
            if (note.ProposedCallback is not { IsPending: true } p) continue;
            if (nowLocal - note.StartedUtc.ToLocalTime() > HeardWindow) continue;
            var id = "heard:" + note.Id;
            if (handled.Contains(id)) continue;
            var when = p.WhenUtc.ToLocalTime();
            var who = Who(cbList, note);
            cards.Add(new NowCard(id, NowKind.Heard, "Palon שמע בשיחה", who,
                p.Reason is { Length: > 0 } r ? r : "הבטחת לחזור",
                Array.Empty<string>(), "קבע ל" + He.When(when, nowLocal), new[] { "לא צריך" },
                Quote: p.Phrase.Length > 0 ? $"״{p.Phrase}״" : null,
                QuoteSub: $"מהשיחה של {He.Clock(note.StartedUtc.ToLocalTime())} · הצעה: {He.When(when, nowLocal)}",
                NoteId: note.Id));
        }

        foreach (var note in noteList)
        {
            if (string.IsNullOrWhiteSpace(note.Summary) || note.DurationSec < 60) continue;
            if (nowLocal - note.StartedUtc.ToLocalTime() > SalesforceWindow) continue;
            var id = "sf:" + note.Id;
            if (handled.Contains(id)) continue;
            cards.Add(new NowCard(id, NowKind.Salesforce, "סיכום שיחה · Salesforce", Who(cbList, note),
                $"שיחה של {He.Clock(note.StartedUtc.ToLocalTime())} · {He.Duration(note.DurationSec)}",
                Lines(note.Summary!, 3), "רשום ב-Salesforce", new[] { "העתק סיכום", "לא צריך" }, NoteId: note.Id));
        }

        foreach (var note in noteList)
        {
            if (nowLocal - note.StartedUtc.ToLocalTime() > FollowUpWindow) continue;
            var id = "tpl:" + note.Id;
            if (handled.Contains(id)) continue;
            if (TemplateFill.Suggest(templates, (note.Summary ?? "") + "\n" + note.Transcript) is not { } tpl) continue;
            var who = Who(cbList, note);
            cards.Add(new NowCard(id, NowKind.FollowUp, "המשך עם תבנית", who, $"{tpl.Title} · מוכנה עם השם",
                Lines(note.Summary ?? "", 2), "העתק " + tpl.Title, new[] { "לא עכשיו" }, NoteId: note.Id, TemplateId: tpl.Id));
        }

        return cards.Take(MaxCards).ToList();
    }

    /// <summary>
    /// Cards from the brain's plan (NextSteps.Plan) that the stack doesn't already hold — only
    /// work tied to something said or prepared, never "call X now": an agreed next step with
    /// nothing booked, a buying signal with a template to send, a follow-up Palon already drafted.
    /// Promises, overdue callbacks and heard proposals are skipped (the callback/heard cards own them).
    /// </summary>
    public static List<NowCard> FromPlan(
        IEnumerable<Palon.Agentic.PlanItem> plan,
        IReadOnlyList<MessageTemplate> templates,
        ISet<string> handled,
        IReadOnlyCollection<NowCard> existing,
        Func<Palon.Agentic.PlanItem, string?> templateIdFor,
        Func<Palon.Agentic.PlanItem, string?> draftFor)
    {
        var cards = new List<NowCard>();
        var taken = existing.Select(c => c.NoteId).OfType<string>().ToHashSet();
        foreach (var item in plan)
        {
            if (item.NoteId is not { } noteId || taken.Contains(noteId)) continue;
            NowCard? card = null;
            switch (item.Kind)
            {
                case Palon.Agentic.PlanKind.UnbookedNextStep:
                    card = new NowCard("step:" + noteId, NowKind.NextStep, "סוכם צעד הבא · אין חזרה ביומן", item.Who,
                        item.Why, Array.Empty<string>(), "קבע חזרה למחר", new[] { "לא צריך" }, NoteId: noteId);
                    break;
                case Palon.Agentic.PlanKind.BuyingSignal when templateIdFor(item) is { } tid && templates.FirstOrDefault(t => t.Id == tid) is { } tpl:
                    card = new NowCard("signal:" + noteId, NowKind.FollowUp, "סימן קנייה · " + item.Why, item.Who,
                        $"{tpl.Title} · מוכנה עם השם", Array.Empty<string>(), "העתק " + tpl.Title, new[] { "לא עכשיו" },
                        NoteId: noteId, TemplateId: tpl.Id);
                    break;
                case Palon.Agentic.PlanKind.SilentLead when draftFor(item) is { Length: > 0 } draft:
                    card = new NowCard("draft:" + noteId, NowKind.Draft, "הודעת המשך מוכנה", item.Who, item.Why,
                        Lines(draft, 2), "העתק הודעה", new[] { "לא צריך" }, NoteId: noteId);
                    break;
            }
            if (card is null || handled.Contains(card.Id)) continue;
            taken.Add(noteId);
            cards.Add(card);
        }
        return cards;
    }

    static string Who(IEnumerable<Callback> callbacks, CallNote note) =>
        ClientIndex.NameForPhone(callbacks, note.Number) ?? note.Number ?? "שיחה";

    /// <summary>The first <paramref name="max"/> meaningful lines, bullets stripped.</summary>
    public static IReadOnlyList<string> Lines(string text, int max) =>
        text.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '•', '*', '·').Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Length > 90 ? l[..90].TrimEnd() + "…" : l)
            .Take(max)
            .ToList();
}

/// <summary>One daily ring: calls, callbacks cleared or deposits vs. pace.</summary>
readonly record struct DayRing(string Key, string Name, double Value, double Goal, string Hint)
{
    public double Fraction => Goal <= 0 ? 1 : Math.Clamp(Value / Goal, 0, 1);
    public bool Closed => Goal <= 0 || Value >= Goal;
}

static class DayRings
{
    public const int DefaultCallsGoal = 40;

    /// <summary>Calls goal: the user's own (Settings.CallsGoal) when set; otherwise 10% above
    /// the average of the last 5 prior days with calls, rounded up to 5 — so the ring stretches
    /// the rep a little, never absurdly. Shared by the Now window and the dock.</summary>
    public static int CallsGoal(IEnumerable<CallRecord> calls, DateTime nowLocal, int? manual = null)
    {
        if (manual is int m && m > 0) return m;
        var days = calls.Select(c => c.StartedUtc.ToLocalTime().Date)
            .Where(d => d < nowLocal.Date)
            .GroupBy(d => d).OrderByDescending(g => g.Key).Take(5)
            .Select(g => g.Count()).ToList();
        if (days.Count == 0) return DefaultCallsGoal;
        var goal = (int)Math.Ceiling(days.Average() * 1.1 / 5) * 5;
        return Math.Max(10, goal);
    }

    public static IReadOnlyList<DayRing> Build(
        IEnumerable<CallRecord> calls, IEnumerable<Callback> callbacks, MonthBook? book, MonthStats? stats, DateTime nowLocal, int? callsGoalSetting = null)
    {
        var callList = calls.ToList();
        var today = nowLocal.Date;
        var callsToday = callList.Count(c => c.StartedUtc.ToLocalTime().Date == today);
        var callsGoal = CallsGoal(callList, nowLocal, callsGoalSetting);

        var cbs = callbacks.ToList();
        var cleared = cbs.Count(c => c.Status == CallbackStatus.Done && c.CompletedUtc?.ToLocalTime().Date == today);
        var owed = cbs.Where(c => c.IsActive && c.DueAtUtc.ToLocalTime().Date <= today).ToList();
        var late = owed.Count(c => c.DueAtUtc.ToLocalTime() < nowLocal);
        var cbGoal = cleared + owed.Count;

        var depsToday = book?.Deals.Count(d => d.Date.Date == today) ?? 0;
        var depGoal = Math.Round(stats?.RequiredPerDay ?? Math.Max(1, stats?.PacePerDay ?? 1), 1);
        if (depGoal <= 0) depGoal = 0;

        return new[]
        {
            new DayRing("calls", "שיחות", callsToday, callsGoal,
                callsToday >= callsGoal ? "נסגרה" : $"עוד {callsGoal - callsToday}"),
            new DayRing("cbs", "חזרות", cleared, cbGoal,
                cbGoal == 0 ? "אין חזרות היום" : owed.Count == 0 ? "נסגרה" : late > 0 ? $"{late} באיחור" : $"עוד {owed.Count}"),
            new DayRing("deps", "הפקדות", depsToday, depGoal,
                depsToday >= depGoal ? "בקצב" : $"חסרות {Math.Ceiling(depGoal - depsToday)} לקצב"),
        };
    }

    /// <summary>"2 מתוך 3 נסגרו" / "כל הטבעות סגורות".</summary>
    public static string Head(IReadOnlyList<DayRing> rings)
    {
        var closed = rings.Count(r => r.Closed);
        return closed == rings.Count ? "כל הטבעות סגורות" : $"{closed} מתוך {rings.Count} נסגרו";
    }

    /// <summary>The day's score, 0–100: the rings' average fill.</summary>
    public static int Score(IReadOnlyList<DayRing> rings) =>
        rings.Count == 0 ? 0 : (int)Math.Round(rings.Average(r => r.Fraction) * 100);
}

static class PayMath
{
    /// <summary>Consecutive work days with at least one deposit, counting back from
    /// today (today counts only once it has one — an empty morning doesn't break it).</summary>
    public static int Streak(MonthBook? book, DateTime nowLocal, IEnumerable<Deal>? earlier = null)
    {
        var days = (book?.Deals ?? new List<Deal>()).Concat(earlier ?? Enumerable.Empty<Deal>())
            .Select(d => d.Date.Date).ToHashSet();
        var streak = 0;
        var day = nowLocal.Date;
        if (!days.Contains(day)) day = day.AddDays(-1);
        for (var guard = 0; guard < 400; guard++, day = day.AddDays(-1))
        {
            if (!CallbackPlanner.IsWorkDay(day.DayOfWeek)) continue;
            if (!days.Contains(day)) break;
            streak++;
        }
        return streak;
    }

    /// <summary>The ₪ a refresh just added — only when a deal was added and pay rose.</summary>
    public static int? DepositDelta(int prevCount, int prevPay, int newCount, int newPay) =>
        newCount > prevCount && newPay > prevPay ? newPay - prevPay : null;
}

/// <summary>A tasteful seasonal accent for the top bar (and a first-deposit line).</summary>
sealed record HolidayAccent(string Greeting, string FirstDepositLine);

static class NowRituals
{
    public const int ShinyOdds = 450;
    public static readonly TimeSpan WelcomeBackAfter = TimeSpan.FromMinutes(45);

    /// <summary>The morning briefing: first open of the day, before 14:00.</summary>
    public static bool ShowMorning(DateTime? lastMorningLocal, DateTime nowLocal) =>
        nowLocal.Hour is >= 5 and < 14 && lastMorningLocal?.Date != nowLocal.Date;

    /// <summary>End-of-day recap is offered from 17:00 once there was work today.</summary>
    public static bool OfferRecap(DateTime? lastRecapLocal, DateTime nowLocal, int callsToday) =>
        nowLocal.Hour >= 17 && callsToday > 0 && lastRecapLocal?.Date != nowLocal.Date;

    /// <summary>Welcome back after ≥45 minutes away, same day only.</summary>
    public static bool WelcomeBack(DateTime? lastActiveLocal, DateTime nowLocal) =>
        lastActiveLocal is { } last && last.Date == nowLocal.Date && nowLocal - last >= WelcomeBackAfter;

    /// <summary>Rare shiny Palon: a roll in [0, ShinyOdds) of exactly 0.</summary>
    public static bool IsShiny(int roll) => roll == 0;

    // Start dates (day 1) of the holidays we accent, 2025–2028.
    static readonly DateTime[] RoshHashana = { new(2025, 9, 23), new(2026, 9, 12), new(2027, 10, 2), new(2028, 9, 21) };
    static readonly DateTime[] Hanukkah = { new(2025, 12, 15), new(2026, 12, 5), new(2027, 12, 25), new(2028, 12, 13) };
    static readonly DateTime[] Pesach = { new(2026, 4, 2), new(2027, 4, 22), new(2028, 4, 11) };

    /// <summary>"שנה טובה" from a week before Rosh Hashana through Sukkot (+22 days);
    /// "חנוכה שמח" for the eight days; "חג שמח" for Pesach week. Otherwise null.</summary>
    public static HolidayAccent? Holiday(DateTime dateLocal)
    {
        var d = dateLocal.Date;
        if (RoshHashana.Any(h => d >= h.AddDays(-7) && d <= h.AddDays(22)))
            return new HolidayAccent("שנה טובה", "שנה מתוקה ועסקאות עסיסיות. ההפקדה הראשונה של השנה.");
        if (Hanukkah.Any(h => d >= h && d <= h.AddDays(7)))
            return new HolidayAccent("חנוכה שמח", "עוד נר. עוד הפקדה.");
        if (Pesach.Any(h => d >= h.AddDays(-1) && d <= h.AddDays(6)))
            return new HolidayAccent("חג שמח", "חג של חירות — והפקדה.");
        return null;
    }

    /// <summary>"יום חמישי · 24 בספטמבר · 08:52".</summary>
    public static string LongDate(DateTime nowLocal, bool withClock = true) =>
        $"יום {He.Day(nowLocal.DayOfWeek)} · {nowLocal.Day} ב{He.Month(nowLocal.Month)}" + (withClock ? $" · {He.Clock(nowLocal)}" : "");

    /// <summary>What happened while the rep was away (welcome back), newest facts first. Empty when nothing did.</summary>
    public static List<string> AwayItems(IEnumerable<CallNote> notes, IEnumerable<Callback> callbacks, DateTime sinceLocal, DateTime nowLocal)
    {
        var items = new List<string>();
        var cbs = callbacks.ToList();
        var newNotes = notes.Where(n => n.StartedUtc.ToLocalTime() >= sinceLocal).ToList();
        var summarized = newNotes.Count(n => !string.IsNullOrWhiteSpace(n.Summary));
        if (summarized == 1)
        {
            var n = newNotes.First(x => !string.IsNullOrWhiteSpace(x.Summary));
            items.Add($"סיכמתי את השיחה עם {ClientIndex.NameForPhone(cbs, n.Number) ?? n.Number ?? "הלקוח"}");
        }
        else if (summarized > 1) items.Add($"סיכמתי {summarized} שיחות");
        var heard = newNotes.Count(n => n.ProposedCallback is { IsPending: true });
        if (heard > 0) items.Add(heard == 1 ? "שמעתי הבטחה לחזור — מחכה לאישור שלך בתור" : $"שמעתי {heard} הבטחות לחזור — מחכות לאישור בתור");
        var due = cbs.Count(c => c.IsActive && c.DueAtUtc.ToLocalTime() > sinceLocal && c.DueAtUtc.ToLocalTime() <= nowLocal);
        if (due > 0) items.Add(due == 1 ? "חזרה אחת שקבעת הגיעה לזמנה" : $"{due} חזרות שקבעת הגיעו לזמנן");
        return items;
    }

    /// <summary>The three numbered lines of the morning briefing.</summary>
    /// <summary>The morning curtain's lines from the agentic brief (Rituals.Brief + NextSteps):
    /// the headline, the pace, then up to two concrete opportunities ("who — why"). At most 4.</summary>
    public static List<string> BriefItems(Palon.Agentic.DailyBriefData brief)
    {
        var items = new List<string> { brief.Headline.TrimEnd('.') };
        if (brief.Pace is { Length: > 0 } pace) items.Add(pace.TrimEnd('.'));
        if (brief.TargetMissing) items.Add("חודש חדש — כדאי להגדיר יעד");
        foreach (var o in brief.Opportunities.Take(2)) items.Add($"{o.Who} — {o.Why}");
        return items.Take(4).ToList();
    }

    public static List<string> MorningItems(CallbackCounts counts, MonthStats? stats, IReadOnlyList<NowCard> stack)
    {
        var items = new List<string>();
        if (counts.Overdue > 0) items.Add(counts.Overdue == 1 ? "חזרה אחת באיחור מאתמול" : $"{counts.Overdue} חזרות באיחור");
        else if (counts.DueToday > 0) items.Add(counts.DueToday == 1 ? "חזרה אחת שקבעת להיום" : $"{counts.DueToday} חזרות שקבעת להיום");
        if (stats is { Target: int t } && t > 0)
        {
            if (stats.Remaining is 0) items.Add("היעד הושג — כל הפקדה עכשיו היא בונוס");
            else if (stats.RequiredPerDay is double need) items.Add($"חסרות {stats.Remaining} עסקאות לחודש · {He.R1(need)} ביום");
        }
        var prepared = stack.Count(c => c.Kind != NowKind.Callback);
        if (prepared > 0) items.Add(prepared == 1 ? "הכנתי לך דבר אחד מהשיחות של אתמול" : $"הכנתי לך {prepared} דברים מהשיחות");
        if (items.Count == 0) items.Add("יום נקי. הכלים מוכנים כשתצטרך.");
        return items.Take(3).ToList();
    }
}

/// <summary>Palon's contextual line under his portrait.</summary>
static class NowLines
{
    public static string Hero(DateTime nowLocal, string? name, NowCard? top, IReadOnlyList<DayRing> rings, bool onCall,
        CallbackCounts counts, MonthStats? stats)
    {
        if (onCall) return "בשיחה. אני מקשיב ורושם בשבילך.";
        if (top is not null)
        {
            var first = TemplateFill.FirstName(top.Who);
            var who = first.Length > 0 ? first : top.Who;
            return top.Kind switch
            {
                NowKind.Callback => top.Overdue ? $"ביקשת להזכיר לך: {who} מחכה לחזרה." : $"הגיע הזמן לחזרה שקבעת ל{who}.",
                NowKind.Heard => top.Quote is { } q ? $"שמעתי את {who}: {q}. לקבוע?" : $"שמעתי ש{who} מחכה לחזרה. לקבוע?",
                NowKind.Salesforce => $"סיכמתי את השיחה עם {who}. לרשום ב-Salesforce?",
                NowKind.NextStep => $"סיכמתם צעד הבא עם {who}, ואין חזרה ביומן. לקבוע?",
                NowKind.Draft => $"ניסחתי ל{who} הודעת המשך. להעתיק?",
                _ => $"הכנתי ל{who} תבנית עם השם. להעתיק?",
            };
        }
        if (rings.Count > 0 && rings.All(r => r.Closed)) return "כל הטבעות סגורות. יום מושלם.";
        return He.Greeting(nowLocal, name) + ". " + MonthView.Brief(counts, stats);
    }
}

/// <summary>One row in the command bar's template palette ("תב").</summary>
sealed record PaletteItem(string Key, string Title, string Sub, string Text, bool IsSnippet, string? TemplateId);

static class CommandText
{
    public const int Pinned = 5;

    /// <summary>"תב", "תבנית", "תבניות הפקדה" → palette mode.</summary>
    public static bool IsPalette(string text) => text.TrimStart().StartsWith("תב", StringComparison.Ordinal);

    /// <summary>The filter after the palette word: "תבנית הפקדה" → "הפקדה".</summary>
    public static string PaletteQuery(string text)
    {
        var t = text.TrimStart();
        if (!IsPalette(t)) return t.Trim();
        var space = t.IndexOf(' ');
        return space < 0 ? "" : t[(space + 1)..].Trim();
    }

    /// <summary>Templates (1–5 keyed) then snippets, filtered by title/tag/text.</summary>
    public static List<PaletteItem> Palette(IReadOnlyList<MessageTemplate> templates, IReadOnlyList<Snippet> snippets, string query, string? name)
    {
        var all = new List<PaletteItem>();
        for (var i = 0; i < templates.Count; i++)
        {
            var t = templates[i];
            var filled = TemplateFill.Fill(t, name);
            all.Add(new PaletteItem(i < Pinned ? (i + 1).ToString() : "·", t.Title, OneLine(filled, 60), filled, false, t.Id));
        }
        foreach (var s in snippets) all.Add(new PaletteItem("“", s.Label, OneLine(s.Text, 60), s.Text, true, null));
        var q = query.Trim();
        if (q.Length == 0) return all;
        return all.Where(p => p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || p.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                              || (p.TemplateId is { } id && templates.First(t => t.Id == id).Tag.Contains(q, StringComparison.OrdinalIgnoreCase)))
                  .ToList();
    }

    /// <summary>Digit 1–5 on an empty bar → the pinned template's index.</summary>
    public static int? PinnedIndex(string barText, int digit, int templateCount) =>
        barText.Length == 0 && digit >= 1 && digit <= Math.Min(Pinned, templateCount) ? digit - 1 : null;

    /// <summary>↑/↓ selection that wraps.</summary>
    public static int Move(int selected, int delta, int count) =>
        count <= 0 ? 0 : ((selected + delta) % count + count) % count;

    public static string OneLine(string text, int max)
    {
        var s = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }
}
