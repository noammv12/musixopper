using Palon.Coaching;
using Palon.Memory;
using Palon.Salesforce;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.Agentic;

enum NudgeActionKind { OpenPage, ShowText, CopyTemplate, DraftFollowUp, SnoozeCallback, ClientSummary, NextSteps }

/// <summary>A nudge's offered action as data (so rules stay pure); the engine turns it into a Func.</summary>
sealed record NudgeAction(NudgeActionKind Kind, string Label, string? Arg = null, string? Arg2 = null);

/// <summary>
/// A nudge a rule would like to show. <see cref="Key"/> is the dedup identity (a nudge with the same
/// key is shown at most once, ever — daily ones carry the date). <see cref="UserReminder"/> marks
/// reminders the user set themselves: they pass focus mode and the rate limit.
/// </summary>
sealed record NudgeCandidate(
    string Key,
    NudgeKind Kind,
    int Priority,
    string Text,
    NudgeAction? Action = null,
    DateTime? ExpiresLocal = null,
    bool UserReminder = false,
    string? ClientName = null,
    string? Phone = null,
    bool Contact = false);

/// <summary>Inputs the rules need beyond the snapshot. All precomputed so the rules stay pure.</summary>
sealed record NudgeExtras(
    int TemplateProposals = 0,
    string TemplateProposalsKey = "",
    RehearsalStatus? Rehearsal = null,
    IReadOnlyList<string>? CoachTips = null,
    IReadOnlySet<string>? DraftsReady = null);

/// <summary>
/// The proactive rules. Each looks at the snapshot and proposes nudges with a
/// stable key; <see cref="NudgeGovernor"/> decides what actually reaches the user.
/// Everything is local — no AI in here.
/// </summary>
static class NudgeRules
{
    public static readonly TimeSpan PromiseLead = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan[] PaceSlots = { new(12, 30, 0), new(16, 30, 0) };
    public static readonly TimeSpan PaceWindow = TimeSpan.FromMinutes(90);
    public const int MaxRevivesPerTick = 2;

    public static List<NudgeCandidate> Evaluate(WorkSnapshot s, NudgeExtras x)
    {
        var list = new List<NudgeCandidate>();
        Promises(s, list);
        Celebrations(s, list);
        Pace(s, list);
        Signals(s, list);
        Revive(s, x, list);
        TargetMissing(s, list);
        Rehearsal(s, x, list);
        Templates(s, x, list);
        Tip(s, x, list);
        return list;
    }

    static bool WorkDay(DateTime d) => CallbackPlanner.IsWorkDay(d.DayOfWeek);

    // ---- promise keeper ------------------------------------------------------------------

    static void Promises(WorkSnapshot s, List<NudgeCandidate> list)
    {
        foreach (var cb in s.Callbacks.Where(c => c.IsActive))
        {
            var due = cb.DueAtUtc.ToLocalTime();
            var card = s.Clients.FirstOrDefault(c => c.Callbacks.Any(x => x.Id == cb.Id));
            var until = due - s.Now;
            if (until > TimeSpan.Zero && until <= PromiseLead + TimeSpan.FromSeconds(30))
            {
                var mins = Math.Max(1, (int)Math.Round(until.TotalMinutes));
                var context = card is not null && s.LatestNote(card) is { } last ? Shorten(CallSummary.Line(last), 70) : null;
                var text = $"בעוד {mins} דק׳: {cb.DisplayLine}" + (context is not null && cb.Note.Length == 0 ? $" — {context}" : "");
                list.Add(new NudgeCandidate($"promise:{cb.Id}:{due:yyyyMMddHHmm}", NudgeKind.Reminder, 100, text,
                    card is not null ? new NudgeAction(NudgeActionKind.ClientSummary, "תדריך", card.Name) : null,
                    ExpiresLocal: due.AddMinutes(15), UserReminder: true, ClientName: cb.Name, Phone: cb.Phone));
            }
            else if (until < -TimeSpan.FromMinutes(20) && until > -TimeSpan.FromHours(3) && due.Date == s.Now.Date)
            {
                list.Add(new NudgeCandidate($"missed:{cb.Id}:{due:yyyyMMddHHmm}", NudgeKind.Reminder, 80,
                    $"החזרה ל{cb.DisplayLabel} הייתה ב-{He.Clock(due)} ועדיין פתוחה.",
                    new NudgeAction(NudgeActionKind.SnoozeCallback, "דחה בשעה", cb.Id),
                    ExpiresLocal: s.Now.AddHours(2), UserReminder: true, ClientName: cb.Name, Phone: cb.Phone));
            }
        }
    }

    // ---- celebrations --------------------------------------------------------------------

    static void Celebrations(WorkSnapshot s, List<NudgeCandidate> list)
    {
        if (s.Month is null) return;
        var ordered = SalesStats.Ordered(s.Month.Deals);
        var target = s.Month.Target;
        for (var i = 0; i < ordered.Count; i++)
        {
            var d = ordered[i];
            if (d.Date.Date != s.Now.Date || d.CreatedFrom == DealOrigin.Import) continue;
            var bonus = s.Rules.FtdBonus(d);
            var n = i + 1;
            var progress = target is int t && t > 0 ? $"{n}/{t} החודש" : $"{n} החודש";
            list.Add(new NudgeCandidate($"deal:{d.Id}", NudgeKind.Celebration, 90,
                $"הפקדה של {d.ClientName}! ${He.N(d.Amount)}{(bonus > 0 ? $" · +₪{He.N(bonus)}" : "")} · {progress}",
                ExpiresLocal: s.Now.AddMinutes(30)));
            if (target is int tt && tt > 0 && n == tt)
                list.Add(new NudgeCandidate($"target-hit:{s.Month.Key}", NudgeKind.Celebration, 95,
                    $"הגעת ליעד — {tt}/{tt}! מעכשיו כל הפקדה מוסיפה ₪{s.Rules.TargetBonusPerDealIls}.", ExpiresLocal: s.Now.AddHours(2)));
        }
    }

    // ---- pace ----------------------------------------------------------------------------

    static void Pace(WorkSnapshot s, List<NudgeCandidate> list)
    {
        if (!WorkDay(s.Now) || s.Stats is not { Target: int target } st || target <= 0 || st.Remaining is 0) return;
        var today = s.Month!.Deals.Count(d => d.Date.Date == s.Now.Date);
        if (Agentic.Pace.TodayGoal(st, today) is not int goal || goal <= 0) return;
        for (var i = 0; i < PaceSlots.Length; i++)
        {
            var slot = s.Now.Date + PaceSlots[i];
            if (s.Now < slot || s.Now > slot + PaceWindow) continue;
            var late = i == PaceSlots.Length - 1;
            var key = $"pace:{s.Now:yyyyMMdd}:{i}";
            if (today >= goal)
            {
                if (late)
                    list.Add(new NudgeCandidate($"pace-ok:{s.Now:yyyyMMdd}", NudgeKind.Celebration, 45,
                        $"עמדת בקצב של היום: {today}/{goal}. {st.Count}/{target} החודש.", ExpiresLocal: slot + PaceWindow));
                continue;
            }
            var expected = late ? goal : (int)Math.Ceiling(goal * Agentic.Pace.DayFraction(s.Now));
            var missing = expected - today;
            if (missing <= 0) continue;
            var text = late
                ? $"עד סוף היום: עוד {He.Plural(goal - today, "הפקדה אחת", "הפקדות")} לקצב ({today}/{goal})."
                : $"{(missing == 1 ? "חסרה הפקדה אחת" : $"חסרות {missing} הפקדות")} לקצב של היום ({today}/{goal}).";
            list.Add(new NudgeCandidate(key, NudgeKind.Insight, 50, text,
                new NudgeAction(NudgeActionKind.NextSteps, "מה עכשיו"), ExpiresLocal: slot + PaceWindow));
        }
    }

    // ---- buying signals ------------------------------------------------------------------

    static void Signals(WorkSnapshot s, List<NudgeCandidate> list)
    {
        foreach (var note in s.Notes)
        {
            var ended = note.StartedUtc.ToLocalTime().AddSeconds(note.DurationSec);
            if (s.Now - ended > TimeSpan.FromHours(3) || ended > s.Now) continue;
            var card = s.Clients.FirstOrDefault(c => c.Calls.Any(n => n.Id == note.Id));
            if (card?.Deals.Count > 0) continue;
            var sig = BuyingSignals.Detect(note).FirstOrDefault();
            if (sig is null) continue;
            var who = card?.Name ?? s.Who(note.Number);
            var templateId = BuyingSignals.TemplateFor(sig);
            var template = templateId is null ? null : s.Templates.FirstOrDefault(t => t.Id == templateId);
            var action = template is not null
                ? new NudgeAction(NudgeActionKind.CopyTemplate, "העתק פרטי הפקדה", template.Id, card?.Name)
                : card is not null ? new NudgeAction(NudgeActionKind.DraftFollowUp, "נסח תשובה", card.Name) : null;
            list.Add(new NudgeCandidate($"signal:{note.Id}", NudgeKind.Insight, 70,
                $"{who} {sig.Label} — סימן קנייה." + (template is not null ? " לשלוח לו את פרטי ההפקדה עכשיו, כשזה חם?" : ""),
                action, ExpiresLocal: ended.AddHours(4), ClientName: card?.Name, Phone: note.Number));
        }
    }

    // ---- silent-lead reviver -------------------------------------------------------------

    static void Revive(WorkSnapshot s, NudgeExtras x, List<NudgeCandidate> list)
    {
        if (!WorkDay(s.Now) || s.Now.Hour < 10 || s.Now.Hour >= 18) return;
        foreach (var lead in Leads.Silent(s).Take(MaxRevivesPerTick))
        {
            var name = lead.Client.Name;
            var ready = x.DraftsReady?.Contains(lead.Client.Key) == true;
            var why = lead.Why.Length > 0 ? $" ({lead.Why})" : "";
            var text = ready
                ? $"{name} שקט כבר {lead.DaysQuiet} ימים{why}. ניסחתי לו הודעת המשך."
                : $"{name} שקט כבר {lead.DaysQuiet} ימים{why}. לנסח לו הודעת המשך?";
            list.Add(new NudgeCandidate($"revive:{lead.Client.Key}:{lead.LastNote.Id}", NudgeKind.Suggestion, 40 + lead.Warmth + (ready ? 5 : 0), text,
                new NudgeAction(NudgeActionKind.DraftFollowUp, ready ? "הצג טיוטה" : "נסח", name),
                ExpiresLocal: s.Now.Date.AddHours(19), ClientName: name, Phone: lead.Client.Phone, Contact: true));
        }
    }

    // ---- month start ---------------------------------------------------------------------

    static void TargetMissing(WorkSnapshot s, List<NudgeCandidate> list)
    {
        if (!WorkDay(s.Now) || s.Now.Day > 7 || s.Now.Hour < 9) return;
        if (!SalesStats.ShouldRemindToSetTarget(s.Month, s.Now)) return;
        list.Add(new NudgeCandidate($"target:{s.Now:yyyy-MM}:{s.Now:dd}", NudgeKind.Reminder, 60,
            $"חודש חדש — מה היעד של {He.Month(s.Now.Month)}? תכניס אותו ואחשב לך קצב יומי.",
            new NudgeAction(NudgeActionKind.OpenPage, "פתח חודש", "month")));
    }

    // ---- salesforce rehearsal ------------------------------------------------------------

    static void Rehearsal(WorkSnapshot s, NudgeExtras x, List<NudgeCandidate> list)
    {
        if (x.Rehearsal is not { Ok: false } r) return;
        if (s.Now - r.AtLocal > TimeSpan.FromHours(20) || r.AtLocal > s.Now) return;
        list.Add(new NudgeCandidate($"sf:{r.AtLocal:yyyyMMddHHmm}", NudgeKind.Insight, 55,
            r.Summary() + " — כדאי ללמד אותי שוב את השלב הזה.",
            new NudgeAction(NudgeActionKind.OpenPage, "פתח הגדרות", "settings")));
    }

    // ---- template learning ---------------------------------------------------------------

    static void Templates(WorkSnapshot s, NudgeExtras x, List<NudgeCandidate> list)
    {
        if (x.TemplateProposals <= 0) return;
        list.Add(new NudgeCandidate($"tpl:{x.TemplateProposalsKey}", NudgeKind.Suggestion, 20,
            x.TemplateProposals == 1 ? "יש לי הצעה לשיפור תבנית, מתוך העריכות שלך." : $"יש לי {x.TemplateProposals} הצעות לשיפור התבניות, מתוך העריכות שלך.",
            new NudgeAction(NudgeActionKind.OpenPage, "הצג", "templates")));
    }

    // ---- coaching tip --------------------------------------------------------------------

    static void Tip(WorkSnapshot s, NudgeExtras x, List<NudgeCandidate> list)
    {
        if (!WorkDay(s.Now) || s.Now.TimeOfDay < new TimeSpan(10, 30, 0) || s.Now.Hour >= 13) return;
        if (PickTip(x.CoachTips, s.Now) is not { } tip) return;
        list.Add(new NudgeCandidate($"tip:{s.Now:yyyyMMdd}", NudgeKind.Insight, 10, tip,
            new NudgeAction(NudgeActionKind.OpenPage, "עוד", "coaching"), ExpiresLocal: s.Now.Date.AddHours(14)));
    }

    /// <summary>One tip per day, rotating through the week's tips.</summary>
    public static string? PickTip(IReadOnlyList<string>? tips, DateTime day) =>
        tips is { Count: > 0 } ? tips[day.DayOfYear % tips.Count] : null;

    /// <summary>The week's coaching tips from the coach store (local, no AI); empty when there's too little data.</summary>
    public static IReadOnlyList<string> CoachTipsNow(DateTime nowLocal)
    {
        try
        {
            var calls = CoachStore.Load();
            var week = CoachReports.WeekStart(nowLocal);
            // Early in the week, coach from last week's calls.
            if (calls.Count(c => c.StartedUtc.ToLocalTime() >= week) < 3) week = week.AddDays(-7);
            if (calls.Count(c => c.StartedUtc.ToLocalTime() >= week) < 3) return Array.Empty<string>();
            var deals = SalesStore.ListMonths().Take(3).SelectMany(m => m.Deals).ToList();
            return CoachTips.For(CoachReports.Build(calls, deals, week));
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: coach tips failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static string? TipOfTheDay(DateTime nowLocal) => PickTip(CoachTipsNow(nowLocal), nowLocal);

    static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}

/// <summary>What the governor knows about the moment and about what was shown before.</summary>
sealed record GovernorInput(
    DateTime NowLocal,
    bool OnCall,
    DateTime? LastCallEndedLocal,
    bool Focus,
    bool NudgesEnabled,
    IReadOnlyDictionary<string, DateTime> Shown,
    IReadOnlyDictionary<string, DateTime> Dismissed,
    int AmbientShownToday,
    DateTime? LastAmbientLocal,
    IReadOnlyList<TimeRule> Rules);

/// <summary>
/// Decides which candidates reach the user now. Pure. The rules:
/// nothing during a call; reminders the user set always pass (after the call);
/// everything else ("ambient") waits out a quiet minute after a call, respects
/// focus, the on/off switch and working hours, is shown at most once per key,
/// one per tick, at least <see cref="AmbientGap"/> apart and <see cref="MaxAmbientPerDay"/>
/// a day (celebrations skip the gap), and never suggests contacting a client at a
/// time the user's own rules forbid.
/// </summary>
static class NudgeGovernor
{
    public static readonly TimeSpan AmbientGap = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan AfterCallQuiet = TimeSpan.FromSeconds(90);
    public const int MaxAmbientPerDay = 6;
    public static readonly TimeSpan WorkStart = new(8, 30, 0);
    public static readonly TimeSpan WorkEnd = new(20, 0, 0);

    public static List<NudgeCandidate> Select(IEnumerable<NudgeCandidate> candidates, GovernorInput g)
    {
        var result = new List<NudgeCandidate>();
        if (g.OnCall) return result;

        var fresh = candidates
            .Where(c => !g.Shown.ContainsKey(c.Key) && !g.Dismissed.ContainsKey(c.Key))
            .Where(c => c.ExpiresLocal is null || c.ExpiresLocal > g.NowLocal)
            .GroupBy(c => c.Key).Select(grp => grp.First())
            .OrderByDescending(c => c.Priority)
            .ToList();

        result.AddRange(fresh.Where(c => c.UserReminder));

        var ambient = fresh.Where(c => !c.UserReminder).ToList();
        if (ambient.Count == 0 || g.Focus || !g.NudgesEnabled) return result;
        if (!CallbackPlanner.IsWorkDay(g.NowLocal.DayOfWeek) || g.NowLocal.TimeOfDay < WorkStart || g.NowLocal.TimeOfDay > WorkEnd) return result;
        if (g.LastCallEndedLocal is { } ended && g.NowLocal - ended < AfterCallQuiet) return result;

        ambient = ambient.Where(c => !c.Contact || ContactAllowed(c, g)).ToList();

        // One celebration right away (joy doesn't wait), then at most one other ambient nudge.
        if (ambient.FirstOrDefault(c => c.Kind == NudgeKind.Celebration) is { } party)
        {
            result.Add(party);
            ambient.Remove(party);
        }
        if (g.AmbientShownToday >= MaxAmbientPerDay) return result;
        if (g.LastAmbientLocal is { } last && g.NowLocal - last < AmbientGap) return result;
        if (ambient.FirstOrDefault(c => c.Kind != NudgeKind.Celebration) is { } next) result.Add(next);
        return result;
    }

    static bool ContactAllowed(NudgeCandidate c, GovernorInput g)
    {
        var rules = TimeRules.For(g.Rules, c.ClientName, c.Phone);
        return rules.Count == 0 || TimeRules.Check(rules, g.NowLocal) is null;
    }
}
