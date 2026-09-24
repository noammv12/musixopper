using System.Text;
using System.Text.RegularExpressions;
using Palon.Memory;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.Agentic;

/// <summary>One buying signal heard in a call ("asked about the minimum deposit").</summary>
sealed record BuyingSignal(string Kind, string Label, string Evidence);

/// <summary>
/// Buying signals, read locally from a note's summary, transcript and the
/// verified coaching questions. Plain phrase lists — no AI. A signal means the
/// client moved toward depositing; Palon offers the matching next move.
/// </summary>
static class BuyingSignals
{
    static readonly (string Kind, string Label, Regex Pattern)[] Patterns =
    {
        ("minimum", "שאל על הפקדה מינימלית",
            new(@"מינימום\s*(?:ה)?הפקדה|הפקדה\s*(?:ה)?מינימלית|סכום\s*(?:ה)?מינימלי|(?:מה|כמה)\s*(?:ה)?מינימום|כמה\s*(?:צריך|אפשר|חייב)\s*(?:כדי\s*)?להפקיד|minimum\s*deposit|min(?:imum)?\s*amount|how\s*much\s*(?:do\s*i\s*)?(?:need\s*to\s*)?deposit", RegexOptions.IgnoreCase)),
        ("how-to-deposit", "שאל איך מפקידים",
            new(@"איך\s*(?:אני\s*)?(?:מפקיד|מפקידים|להפקיד|מעביר|מעבירים|להעביר)|פרטי\s*(?:ה)?(?:העברה|הפקדה|חשבון)|לאן\s*(?:ל)?העביר|how\s*(?:do\s*i|to)\s*(?:deposit|transfer|pay)|bank\s*details|wire\s*details", RegexOptions.IgnoreCase)),
        ("start", "מוכן להתחיל",
            new(@"(?:מתי|איך)\s*(?:אפשר|אני\s*יכול)\s*(?:ל)?(?:התחיל|פתוח|לפתוח)|רוצה\s*(?:ל)?(?:התחיל|פתוח|לפתוח)\s*(?:חשבון)?|בוא\s*נתחיל|let'?s\s*(?:start|do\s*it|open)|ready\s*to\s*(?:start|open|deposit)|open\s*(?:an?\s*)?account", RegexOptions.IgnoreCase)),
        ("fees", "שאל על עמלות",
            new(@"עמל(?:ה|ות)|כמה\s*(?:זה\s*)?עולה|דמי\s*ניהול|\bfees?\b|commission|spread", RegexOptions.IgnoreCase)),
        ("returns", "שאל על תשואה",
            new(@"כמה\s*(?:אפשר\s*)?(?:ל)?(?:הרוויח|להרוויח|תשואה)|(?:ה)?תשואה\s*(?:ה)?צפויה|\breturns?\b|how\s*much\s*can\s*i\s*(?:make|earn)", RegexOptions.IgnoreCase)),
        ("withdraw", "שאל על משיכה",
            new(@"(?:איך|מתי)\s*(?:אפשר\s*)?(?:ל)?(?:משוך|למשוך|משיכה)|withdraw", RegexOptions.IgnoreCase)),
    };

    /// <summary>Signals in one note, strongest first (distinct kinds).</summary>
    public static List<BuyingSignal> Detect(CallNote note)
    {
        var sources = new List<string>();
        if (note.Coach?.ClientQuestions is { } qs) sources.AddRange(qs.Select(q => q.Quote));
        if (!string.IsNullOrWhiteSpace(note.Summary)) sources.Add(note.Summary!);
        if (!string.IsNullOrWhiteSpace(note.Transcript)) sources.Add(note.Transcript);
        var found = new List<BuyingSignal>();
        foreach (var (kind, label, pattern) in Patterns)
        {
            foreach (var text in sources)
            {
                var m = pattern.Match(text);
                if (!m.Success) continue;
                found.Add(new BuyingSignal(kind, label, Around(text, m.Index, m.Length)));
                break;
            }
        }
        return found;
    }

    /// <summary>The template that answers a signal: deposit details for money questions.</summary>
    public static string? TemplateFor(BuyingSignal s) => s.Kind switch
    {
        "minimum" or "how-to-deposit" or "start" => "deposit-pro",
        _ => null,
    };

    static string Around(string text, int index, int length)
    {
        var start = Math.Max(0, index - 30);
        var end = Math.Min(text.Length, index + length + 30);
        var s = text[start..end].Replace('\n', ' ').Trim();
        return (start > 0 ? "…" : "") + s + (end < text.Length ? "…" : "");
    }
}

/// <summary>A lead worth reviving: talked, didn't deposit, and it went quiet.</summary>
sealed record SilentLead(ClientCard Client, int DaysQuiet, int Warmth, string Why, CallNote LastNote);

/// <summary>Lead warmth and the silent-lead list. Pure.</summary>
static class Leads
{
    public const int MinQuietDays = 3;
    public const int MaxQuietDays = 21;
    public const int MinWarmth = 2;

    /// <summary>0..8 — how close this client looked to depositing.</summary>
    public static (int Score, string Why) Warmth(ClientCard card, IReadOnlyList<ClientFact> facts)
    {
        var last = card.Calls.OrderByDescending(n => n.StartedUtc).FirstOrDefault();
        if (last is null) return (0, "");
        var score = 0;
        var why = new List<string>();
        var signals = card.Calls.OrderByDescending(n => n.StartedUtc).Take(3).SelectMany(BuyingSignals.Detect)
            .GroupBy(s => s.Kind).Select(g => g.First()).ToList();
        if (signals.Count > 0)
        {
            score += Math.Min(4, 2 + signals.Count);
            why.Add(signals[0].Label);
        }
        if (last.Coach?.NextStep is { Agreed: true } step)
        {
            score += 2;
            if (!string.IsNullOrWhiteSpace(step.Text)) why.Add("סוכם: " + step.Text!.Trim());
        }
        if (card.Calls.Max(n => n.DurationSec) >= 300) { score += 1; why.Add("שיחה ארוכה"); }
        if (card.Calls.Count >= 2) score += 1;
        if (facts.Any(f => f.Type == "budget")) { score += 1; why.Add("יודע תקציב"); }
        if (last.Coach?.Objections is { Count: > 0 } obj && obj.Any(o => o.Category is Coaching.Objections.HasBroker or Coaching.Objections.NoMoney))
            score -= 1;
        return (Math.Clamp(score, 0, 8), string.Join(" · ", why.Take(2)));
    }

    /// <summary>Talked, no deposit, no open callback, quiet 3–21 days, warm enough. Warmest first.</summary>
    public static List<SilentLead> Silent(WorkSnapshot s)
    {
        var list = new List<SilentLead>();
        foreach (var card in s.Clients)
        {
            if (card.Calls.Count == 0 || card.Deals.Count > 0) continue;
            if (card.Callbacks.Any(c => c.IsActive)) continue; // a callback is already booked — the promise keeper owns it
            var last = card.Calls.MaxBy(n => n.StartedUtc)!;
            var lastContact = card.LastLocal;
            var days = (int)(s.Now.Date - lastContact.Date).TotalDays;
            if (days < MinQuietDays || days > MaxQuietDays) continue;
            var (warmth, why) = Warmth(card, s.FactsFor(card));
            if (warmth < MinWarmth) continue;
            list.Add(new SilentLead(card, days, warmth, why, last));
        }
        return list.OrderByDescending(l => l.Warmth).ThenBy(l => l.DaysQuiet).ToList();
    }
}

/// <summary>A compact, local summary of one client (summarize_client, command bar, nudges).</summary>
sealed record ClientSummary(
    string Name,
    string? Phone,
    string Status,
    DateTime? LastContactLocal,
    int CallCount,
    int TalkMinutes,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> RecentCalls,
    IReadOnlyList<BuyingSignal> Signals,
    IReadOnlyList<string> Objections,
    string? NextCallback,
    string? Deposit,
    string? AgreedNextStep,
    string? Suggestion)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.Append(Name);
        if (Phone is not null && Phone != Name) sb.Append($" ({Phone})");
        sb.Append(" — ").Append(Status);
        if (CallCount > 0) sb.Append($"\n{CallCount} שיחות · {TalkMinutes} דק׳");
        if (Deposit is not null) sb.Append($"\nהפקדה: {Deposit}");
        if (NextCallback is not null) sb.Append($"\nחזרה: {NextCallback}");
        if (AgreedNextStep is not null) sb.Append($"\nסוכם: {AgreedNextStep}");
        if (Facts.Count > 0) sb.Append("\nמה שאני יודע:\n").Append(string.Join("\n", Facts.Select(f => "• " + f)));
        if (Signals.Count > 0) sb.Append("\nסימני קנייה: ").Append(string.Join(", ", Signals.Select(x => x.Label)));
        if (Objections.Count > 0) sb.Append("\nהתנגדויות: ").Append(string.Join(", ", Objections));
        if (RecentCalls.Count > 0) sb.Append("\nשיחות אחרונות:\n").Append(string.Join("\n", RecentCalls.Select(c => "• " + c)));
        if (Suggestion is not null) sb.Append("\n→ ").Append(Suggestion);
        return sb.ToString();
    }
}

static class ClientSummaries
{
    public static ClientSummary Build(WorkSnapshot s, ClientCard card)
    {
        var facts = s.FactsFor(card);
        var calls = card.Calls.OrderByDescending(n => n.StartedUtc).ToList();
        var last = calls.FirstOrDefault();
        var signals = calls.Take(3).SelectMany(BuyingSignals.Detect).GroupBy(x => x.Kind).Select(g => g.First()).ToList();
        var objections = calls.Take(5).SelectMany(n => n.Coach?.Objections ?? new())
            .Select(o => Coaching.Objections.Label(o.Category ?? Coaching.Objections.Other)).Distinct().Take(3).ToList();
        var next = card.NextCallback;
        var deal = card.Deals.OrderByDescending(d => d.Date).FirstOrDefault();
        var tone = card.Tone(s.Now);
        var status = tone switch
        {
            ClientTone.Deposited => "הפקיד",
            ClientTone.Overdue => "חזרה באיחור",
            ClientTone.Due => "חזרה קרובה",
            _ => calls.Count == 0 ? "בלי שיחות מתועדות" : "בתהליך",
        };
        var lastContact = card.LastLocal == DateTime.MinValue ? (DateTime?)null : card.LastLocal;
        string? step = last?.Coach?.NextStep is { Agreed: true, Text: { } t } && t.Trim().Length > 0 ? t.Trim() : null;

        string? suggestion = null;
        if (deal is null)
        {
            if (next is null && signals.FirstOrDefault(x => BuyingSignals.TemplateFor(x) is not null) is { } sig)
                suggestion = $"{sig.Label} — שלח לו את פרטי ההפקדה.";
            else if (next is null && step is not null)
                suggestion = "סוכם צעד הבא אבל אין חזרה ביומן — כדאי לקבוע.";
            else if (next is null && lastContact is { } lc && (s.Now - lc).TotalDays >= Leads.MinQuietDays && calls.Count > 0)
                suggestion = $"שקט כבר {(int)(s.Now.Date - lc.Date).TotalDays} ימים — הודעת המשך תחמם אותו.";
        }

        return new ClientSummary(
            card.Name,
            card.Phone,
            status,
            lastContact,
            calls.Count,
            calls.Sum(n => n.DurationSec) / 60,
            facts.OrderByDescending(f => f.Pinned).ThenByDescending(f => f.ValidAt).Take(8).Select(f => f.Text).ToList(),
            calls.Take(3).Select(n => $"{He.Ago(n.StartedUtc.ToLocalTime(), s.Now)}: {CallSummary.Line(n)}").ToList(),
            signals,
            objections,
            next is null ? null : $"{He.When(next.DueAtUtc.ToLocalTime(), s.Now)}{(next.Note.Length > 0 ? " · " + next.Note : "")}",
            deal is null ? null : $"${He.N(deal.Amount)} · {deal.TierLabel} · {He.DayMonth(deal.Date)}",
            step,
            suggestion);
    }
}

enum PlanKind { Promise, Overdue, ProposedCallback, UnbookedNextStep, BuyingSignal, SilentLead }

/// <summary>One concrete step for today — always tied to something the user or the client said.</summary>
sealed record PlanItem(
    PlanKind Kind,
    DateTime? WhenLocal,
    string Who,
    string? Phone,
    string Why,
    string Suggestion,
    string? CallbackId = null,
    string? NoteId = null)
{
    public string ToLine(DateTime nowLocal)
    {
        var when = WhenLocal is { } w ? He.When(w, nowLocal) + " · " : "";
        return $"{when}{Who} — {Why}. {Suggestion}";
    }
}

/// <summary>
/// "What should I do today" — built from the user's own promises (callbacks),
/// proposals Palon heard on calls, agreed next steps with nothing booked, fresh
/// buying signals and warm leads that went quiet. Never a generic call list.
/// </summary>
static class NextSteps
{
    public const int MaxItems = 8;

    public static List<PlanItem> Plan(WorkSnapshot s)
    {
        var now = s.Now;
        var items = new List<PlanItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Claim(ClientCard? card, string fallback) => seen.Add(card?.Key ?? fallback);

        // 1. Promises due today (in time order) and overdue ones.
        foreach (var cb in s.Callbacks.Where(c => c.IsActive).OrderBy(c => c.DueAtUtc))
        {
            var due = cb.DueAtUtc.ToLocalTime();
            if (due.Date > now.Date) continue;
            var card = s.Clients.FirstOrDefault(c => c.Callbacks.Any(x => x.Id == cb.Id));
            Claim(card, "cb:" + cb.Id);
            var overdue = due < now;
            var context = card is null ? null : s.LatestNote(card) is { } n ? CallSummary.Line(n) : null;
            items.Add(new PlanItem(
                overdue ? PlanKind.Overdue : PlanKind.Promise,
                due,
                cb.DisplayLabel,
                cb.Phone,
                cb.Note.Length > 0 && cb.Note != cb.DisplayLabel ? cb.Note : context ?? "חזרה שקבעת",
                overdue ? "הבטחת לחזור — לחזור עכשיו או לדחות לשעה מסודרת." : "חזרה שקבעת.",
                CallbackId: cb.Id));
        }

        // 2. Callback promises Palon heard on calls and the user hasn't accepted yet.
        foreach (var note in s.Notes.Where(n => n.ProposedCallback is { IsPending: true }).OrderByDescending(n => n.StartedUtc).Take(5))
        {
            var card = s.Clients.FirstOrDefault(c => c.Calls.Any(x => x.Id == note.Id));
            if (!Claim(card, "note:" + note.Id)) continue;
            var p = note.ProposedCallback!;
            items.Add(new PlanItem(PlanKind.ProposedCallback, p.WhenUtc.ToLocalTime(), card?.Name ?? s.Who(note.Number), note.Number,
                $"בשיחה נאמר \"{p.Phrase}\"", "לאשר את החזרה ביומן?", NoteId: note.Id));
        }

        // 3. Agreed next step, nothing booked.
        foreach (var card in s.Clients.Where(c => c.Deals.Count == 0 && !c.Callbacks.Any(x => x.IsActive)))
        {
            var last = s.LatestNote(card);
            if (last?.Coach?.NextStep is not { Agreed: true } step) continue;
            if ((now - last.StartedUtc.ToLocalTime()).TotalDays > 10) continue;
            if (!Claim(card, card.Key)) continue;
            items.Add(new PlanItem(PlanKind.UnbookedNextStep, null, card.Name, card.Phone,
                "סוכם: " + (step.Text?.Trim() is { Length: > 0 } t ? t : "צעד הבא"), "אין חזרה ביומן — לקבוע זמן.", NoteId: last.Id));
        }

        // 4. Buying signals from the last few days, not deposited.
        foreach (var card in s.Clients.Where(c => c.Deals.Count == 0))
        {
            var last = s.LatestNote(card);
            if (last is null || (now - last.StartedUtc.ToLocalTime()).TotalDays > 4) continue;
            var sig = BuyingSignals.Detect(last).FirstOrDefault();
            if (sig is null || !Claim(card, card.Key)) continue;
            items.Add(new PlanItem(PlanKind.BuyingSignal, null, card.Name, card.Phone, sig.Label,
                BuyingSignals.TemplateFor(sig) is not null ? "לשלוח פרטי הפקדה." : "לענות לו על זה בהודעה קצרה.", NoteId: last.Id));
        }

        // 5. Warm leads that went quiet.
        foreach (var lead in Leads.Silent(s).Take(3))
        {
            if (!Claim(lead.Client, lead.Client.Key)) continue;
            items.Add(new PlanItem(PlanKind.SilentLead, null, lead.Client.Name, lead.Client.Phone,
                $"שקט {lead.DaysQuiet} ימים{(lead.Why.Length > 0 ? " · " + lead.Why : "")}", "הודעת המשך אישית — אנסח לך.", NoteId: lead.LastNote.Id));
        }

        return items.Take(MaxItems).ToList();
    }
}

/// <summary>The morning brief the ritual renders.</summary>
sealed record DailyBriefData(
    string Greeting,
    string Headline,
    IReadOnlyList<PlanItem> Promises,
    string? Pace,
    IReadOnlyList<PlanItem> Opportunities,
    string? Tip,
    bool TargetMissing)
{
    public string ToText(DateTime nowLocal)
    {
        var sb = new StringBuilder(Greeting).Append(". ").Append(Headline);
        if (Promises.Count > 0) sb.Append("\n\nהחזרות של היום:\n").Append(string.Join("\n", Promises.Select(p => "• " + p.ToLine(nowLocal))));
        if (Pace is not null) sb.Append("\n\n").Append(Pace);
        if (Opportunities.Count > 0) sb.Append("\n\nהזדמנויות:\n").Append(string.Join("\n", Opportunities.Select(p => "• " + p.ToLine(nowLocal))));
        if (Tip is not null) sb.Append("\n\nטיפ: ").Append(Tip);
        return sb.ToString();
    }
}

/// <summary>The end-of-day recap the ritual renders.</summary>
sealed record DayRecapData(
    int Calls,
    int TalkMinutes,
    int Deals,
    decimal Deposited,
    int BonusIls,
    int CallbacksDone,
    int CallbacksMissed,
    IReadOnlyList<PlanItem> Tomorrow,
    IReadOnlyList<PlanItem> OpenLoops,
    string? Pace,
    string Headline,
    string? Highlight)
{
    public string ToText(DateTime nowLocal)
    {
        var sb = new StringBuilder(Headline);
        if (Highlight is not null) sb.Append("\n").Append(Highlight);
        if (Pace is not null) sb.Append("\n").Append(Pace);
        if (OpenLoops.Count > 0) sb.Append("\n\nנשאר פתוח:\n").Append(string.Join("\n", OpenLoops.Select(p => "• " + p.ToLine(nowLocal))));
        if (Tomorrow.Count > 0) sb.Append("\n\nמחר:\n").Append(string.Join("\n", Tomorrow.Select(p => "• " + p.ToLine(nowLocal))));
        return sb.ToString();
    }
}

/// <summary>The two daily rituals: morning brief and end-of-day recap. Pure, local, Hebrew.</summary>
static class Rituals
{
    public static DailyBriefData Brief(WorkSnapshot s, string? repName = null, string? tip = null)
    {
        var plan = NextSteps.Plan(s);
        var promises = plan.Where(p => p.Kind is PlanKind.Promise or PlanKind.Overdue).ToList();
        var opportunities = plan.Where(p => p.Kind is not (PlanKind.Promise or PlanKind.Overdue)).Take(4).ToList();
        var overdue = promises.Count(p => p.Kind == PlanKind.Overdue);
        var today = promises.Count - overdue;

        var parts = new List<string>();
        parts.Add(today switch
        {
            0 when overdue == 0 => "אין חזרות קבועות להיום",
            1 => "חזרה אחת קבועה להיום",
            _ => $"{today} חזרות קבועות להיום",
        });
        if (overdue > 0) parts[^1] += overdue == 1 ? ", ואחת מאתמול שעוד פתוחה" : $", ועוד {overdue} פתוחות מקודם";
        if (opportunities.Count > 0) parts.Add(opportunities.Count == 1 ? "והזדמנות אחת ששווה תשומת לב" : $"ו-{opportunities.Count} הזדמנויות ששוות תשומת לב");

        return new DailyBriefData(
            He.Greeting(s.Now, repName),
            string.Join(" ", parts) + ".",
            promises,
            PaceLine(s, morning: true),
            opportunities,
            tip,
            SalesStats.ShouldRemindToSetTarget(s.Month, s.Now));
    }

    public static DayRecapData Recap(WorkSnapshot s)
    {
        var now = s.Now;
        var calls = s.Calls.Where(c => c.StartedUtc.ToLocalTime().Date == now.Date).ToList();
        var talk = (int)(calls.Sum(c => (long)c.DurationSec) / 60);
        var deals = s.Month?.Deals.Where(d => d.Date.Date == now.Date).ToList() ?? new List<Deal>();
        var bonus = deals.Sum(s.Rules.FtdBonus);
        var done = s.Callbacks.Count(c => c.Status == CallbackStatus.Done && c.CompletedUtc?.ToLocalTime().Date == now.Date);
        var missed = s.Callbacks.Count(c => c.IsActive && c.DueAtUtc.ToLocalTime() < now && c.DueAtUtc.ToLocalTime().Date == now.Date);

        var tomorrowDay = CallbackPlanner.NextWorkMorning(now).Date;
        var tomorrow = s.Callbacks.Where(c => c.IsActive && c.DueAtUtc.ToLocalTime().Date == tomorrowDay)
            .OrderBy(c => c.DueAtUtc)
            .Select(c => new PlanItem(PlanKind.Promise, c.DueAtUtc.ToLocalTime(), c.DisplayLabel, c.Phone,
                c.Note.Length > 0 && c.Note != c.DisplayLabel ? c.Note : "חזרה שקבעת", "", CallbackId: c.Id))
            .ToList();
        var loops = NextSteps.Plan(s).Where(p => p.Kind is PlanKind.Overdue or PlanKind.ProposedCallback or PlanKind.UnbookedNextStep).Take(5).ToList();

        var head = new List<string>
        {
            calls.Count == 0 ? "היום לא היו שיחות" : $"{He.Plural(calls.Count, "שיחה אחת", "שיחות")}, {talk} דק׳ על הקו",
        };
        if (deals.Count > 0) head.Add(deals.Count == 1 ? $"הפקדה אחת (${He.N(deals[0].Amount)})" : $"{deals.Count} הפקדות (${He.N(deals.Sum(d => d.Amount))})");
        if (done > 0) head.Add($"{He.Plural(done, "חזרה אחת בוצעה", "חזרות בוצעו")}");

        string? highlight = null;
        if (bonus > 0) highlight = $"הרווחת היום ₪{He.N(bonus)} בונוס FTD.";
        else if (calls.Count > 0 && s.Notes.Where(n => n.StartedUtc.ToLocalTime().Date == now.Date).MaxBy(n => n.DurationSec) is { DurationSec: >= 300 } longest)
            highlight = $"השיחה הארוכה של היום: {s.Who(longest.Number)}, {longest.DurationSec / 60} דק׳.";

        return new DayRecapData(calls.Count, talk, deals.Count, deals.Sum(d => d.Amount), bonus, done, missed,
            tomorrow, loops, PaceLine(s, morning: false), string.Join(" · ", head) + ".", highlight);
    }

    /// <summary>Target math in one line — null when there's no target.</summary>
    public static string? PaceLine(WorkSnapshot s, bool morning)
    {
        if (s.Stats is not { Target: int target } st || target <= 0) return null;
        if (st.Remaining is 0) return $"עברת את היעד ({st.Count}/{target}). כל הפקדה מעכשיו מוסיפה ₪{s.Rules.TargetBonusPerDealIls}.";
        var today = s.Month!.Deals.Count(d => d.Date.Date == s.Now.Date);
        var need = Pace.TodayGoal(st, today);
        if (need is null) return $"חסרים {st.Remaining} ליעד.";
        return morning
            ? $"{st.Count}/{target} החודש. כדי להישאר בקצב: {need} הפקדות היום."
            : today >= need ? $"עמדת בקצב היום ({today}). {st.Count}/{target} החודש." : $"היום {today} מתוך {need} לקצב. {st.Count}/{target} החודש.";
    }
}

/// <summary>Pace math for "how many today". Pure.</summary>
static class Pace
{
    /// <summary>Deposits needed today to stay on target pace, counting today's as part of it
    /// (required per day is computed on remaining days including today). Null without a target.</summary>
    public static int? TodayGoal(MonthStats st, int depositsToday)
    {
        if (st.Target is not int target || target <= 0) return null;
        // ElapsedWorkDays includes today, so the days left including today = remaining + 1 when today is a work day.
        var remainingBeforeToday = Math.Max(0, target - (st.Count - depositsToday));
        var daysLeft = st.RemainingWorkDays + (st.ElapsedWorkDays > 0 ? 1 : 0);
        if (daysLeft <= 0) return remainingBeforeToday;
        return (int)Math.Ceiling((double)remainingBeforeToday / daysLeft);
    }

    /// <summary>Share of the working day behind us (09:00–19:00), 0..1.</summary>
    public static double DayFraction(DateTime nowLocal)
    {
        var start = nowLocal.Date.AddHours(9);
        var end = nowLocal.Date.AddHours(19);
        if (nowLocal <= start) return 0;
        if (nowLocal >= end) return 1;
        return (nowLocal - start).TotalMinutes / (end - start).TotalMinutes;
    }
}
