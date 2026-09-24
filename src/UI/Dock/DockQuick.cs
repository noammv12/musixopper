using System.Globalization;
using System.Text.RegularExpressions;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>Someone the quick field can book a callback for.</summary>
sealed record QuickPerson(string Name, string? Phone);

/// <summary>
/// What the quick field understood so far: who, when, and the one line the
/// field previews ("דני לוי · מחר 11:00"). Miss = something typed but no
/// person found; Empty = nothing typed.
/// </summary>
sealed record QuickParse(QuickPerson? Who, DateTime? WhenLocal, string? Miss = null, bool IsNew = false)
{
    public bool Empty => Who is null && Miss is null;
    public bool CanBook => Who is not null && WhenLocal is not null;
}

/// <summary>
/// The dock's one-gesture callback rules, pure so they're testable:
/// the quick-field parser ("דני 11", "מאיה מחר בערב", "בעוד שעה"), the
/// smart default the in-call ⏰ books, the four after-call chips, the
/// "today" rings, the pinned templates, and hotkey conflicts.
/// </summary>
static class DockQuick
{
    // Words that open a time phrase — when the text starts with one, there's
    // no name and the callback is for the current / last client.
    static readonly string[] TimeLeads =
    {
        "בעוד", "עוד", "מחר", "מחרתיים", "היום", "הערב", "בערב", "בבוקר", "בצהריים", "אחה\"צ", "ביום", "יום",
        "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "בשבוע", "שבוע", "בשעה",
        "in", "tomorrow", "today", "tonight", "at", "next", "this",
    };

    static readonly Regex BareNumber = new(@"(?<![\d:\-])(\d{1,2})(?::(\d{2}))?(?![\d:])");

    /// <summary>
    /// Reads "name when". The name is the first one or two words, matched
    /// against <paramref name="people"/> (most recent first): an exact first
    /// name or full name wins, else a word prefix. Unknown names still book
    /// (flagged <see cref="QuickParse.IsNew"/>) — a new lead is a real case.
    /// A text that opens with a time word books <paramref name="context"/>
    /// (the current or last caller). No time → tomorrow 10:00 (next work day).
    /// </summary>
    public static QuickParse Parse(string? text, DateTime nowLocal, IReadOnlyList<QuickPerson> people, QuickPerson? context = null)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return new QuickParse(null, null);
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        QuickPerson? who = null;
        var isNew = false;
        var used = 0;
        var nameWord = people.Any(p => p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.Equals(tokens[0], StringComparison.OrdinalIgnoreCase)));
        if (tokens[0].Count(char.IsAsciiDigit) >= 7 && CallbackStore.NormalizePhone(tokens[0]) is { } phone)
        {
            who = people.FirstOrDefault(p => Palon.Agent.PhoneMatch.Same(p.Phone, phone)) ?? new QuickPerson(phone, phone);
            used = 1;
        }
        else if (StartsWithTime(tokens[0]) && !nameWord) // "שני" is Monday — unless a client is called Shani
        {
            if (context is null) return new QuickParse(null, null, Miss: "למי? התחל בשם");
            who = context;
        }
        else
        {
            (who, used) = Match(tokens, people);
            if (who is null)
            {
                if (tokens[0].Any(char.IsDigit)) return new QuickParse(null, null, Miss: "לא מצאתי לקוח");
                who = new QuickPerson(tokens[0], null);
                isNew = true;
                used = 1;
            }
        }

        var rest = string.Join(' ', tokens.Skip(used));
        var when = When(rest, nowLocal);
        return new QuickParse(who, when, IsNew: isNew);
    }

    static bool StartsWithTime(string token)
    {
        var t = token.ToLowerInvariant().TrimStart('ב', '-');
        if (t.Length > 0 && char.IsAsciiDigit(t[0])) return true;
        return TimeLeads.Any(w => token.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    static (QuickPerson? Who, int Used) Match(string[] tokens, IReadOnlyList<QuickPerson> people)
    {
        if (people.Count == 0) return (null, 0);
        static string Key(string s) => s.Trim().ToLowerInvariant();
        // Two words first ("דני לוי"), then one.
        if (tokens.Length >= 2)
        {
            var two = Key(tokens[0] + " " + tokens[1]);
            var hit = people.FirstOrDefault(p => Key(p.Name) == two)
                      ?? people.FirstOrDefault(p => Key(p.Name).StartsWith(two, StringComparison.Ordinal) && two.Length >= 4);
            if (hit is not null) return (hit, 2);
        }
        var one = Key(tokens[0]);
        if (one.Length == 0) return (null, 0);
        var exact = people.FirstOrDefault(p => Key(p.Name) == one)
                    ?? people.FirstOrDefault(p => Words(p.Name).Any(w => w == one));
        if (exact is not null) return (exact, 1);
        if (one.Length >= 2)
        {
            var prefix = people.FirstOrDefault(p => Words(p.Name).Any(w => w.StartsWith(one, StringComparison.Ordinal)));
            if (prefix is not null) return (prefix, 1);
        }
        return (null, 0);

        static IEnumerable<string> Words(string name) => name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>The time part: TimePhrase first; a bare "11" / "16:30"
    /// reads as a clock hour; nothing at all → the next work morning.</summary>
    public static DateTime When(string rest, DateTime nowLocal)
    {
        var now = Trim(nowLocal);
        rest = Weekdays((rest ?? "").Trim());
        if (rest.Length == 0) return CallbackPlanner.NextWorkMorning(now);
        if (TimePhrase.TryResolve(rest, now, out var when, out var complete) && complete) return when;
        // "11", "מחר 11", "ראשון 16:30" — a bare number is the hour here
        // (TimePhrase ignores bare numbers in free speech; in this field they mean time).
        var cued = BareNumber.Replace(rest, m => "ב-" + m.Value, 1);
        if (cued != rest && TimePhrase.TryResolve(cued, now, out var cuedWhen, out _)) return cuedWhen;
        if (TimePhrase.TryResolve(rest, now, out when, out _)) return when;
        return CallbackPlanner.NextWorkMorning(now);
    }

    static readonly string[] HebrewWeekdays = { "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי" };

    /// <summary>"ראשון 10" → "ביום ראשון 10": TimePhrase wants the "יום" cue, the field doesn't.</summary>
    static string Weekdays(string rest)
    {
        var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var bare = tokens[i].StartsWith('ב') && HebrewWeekdays.Contains(tokens[i][1..]) ? tokens[i][1..] : tokens[i];
            if (!HebrewWeekdays.Contains(bare)) continue;
            if (i > 0 && tokens[i - 1] is "יום" or "ביום") continue;
            tokens[i] = "ביום " + bare;
        }
        return string.Join(' ', tokens);
    }

    /// <summary>"דני לוי · מחר 11:00" (a new name is marked "חדש").</summary>
    public static string Preview(QuickParse parse, DateTime nowLocal) =>
        parse.Who is null ? parse.Miss ?? ""
        : $"{parse.Who.Name}{(parse.IsNew ? " (חדש)" : "")} · {(parse.WhenLocal is { } w ? DockText.When(w, nowLocal) : "")}";

    /// <summary>What the in-call ⏰ books on its first press: the time Palon
    /// heard, when it's still ahead; else tomorrow 10:00 (next work day).</summary>
    public static DateTime SmartDefault(DateTime? heardLocal, DateTime nowLocal) =>
        heardLocal is { } h && h > nowLocal.AddMinutes(5) ? h : CallbackPlanner.NextWorkMorning(nowLocal);

    /// <summary>Clamp the booked time to the client's time rules ("not before 12").</summary>
    public static DateTime ApplyRules(DateTime whenLocal, IReadOnlyList<Palon.Memory.TimeRule> rules) =>
        rules.Count == 0 ? whenLocal : Palon.Memory.TimeRules.NextAllowed(rules, whenLocal);

    /// <summary>
    /// The after-call card's four chips: Palon's heard time first (highlighted),
    /// then the standard picks — disabled ones and same-day twins dropped —
    /// capped at four. Always at least "tomorrow".
    /// </summary>
    public static List<DockPick> Chips(IReadOnlyList<QuickPick> standard, CallbackProposal? proposal, DateTime nowLocal)
    {
        var all = DockText.Picks(standard, proposal, nowLocal).Where(p => p.Enabled).ToList();
        var heard = all.FindIndex(p => p.Heard);
        if (heard > 0)
        {
            var h = all[heard];
            all.RemoveAt(heard);
            all.Insert(0, h);
        }
        return all.Take(4).ToList();
    }

    /// <summary>A chip's two lines: top ("Palon שמע" / "בעוד שעה" / "מחר") and the time.</summary>
    public static (string Top, string Time) ChipLines(DockPick pick, DateTime dueLocal, DateTime nowLocal)
    {
        var time = dueLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (pick.Heard) return ("Palon שמע", DockText.When(dueLocal, nowLocal));
        var days = (dueLocal.Date - nowLocal.Date).Days;
        var top = pick.Key switch
        {
            "in1h" => "בעוד שעה",
            "tonight" => "הערב",
            _ => days switch
            {
                0 => "היום",
                1 => "מחר",
                > 1 and < 7 => DockText.Day(dueLocal.DayOfWeek),
                _ => $"{dueLocal.Day}.{dueLocal.Month}",
            },
        };
        return (top, time);
    }

    /// <summary>A chip nudged by the ±15m buttons / mouse wheel, never into the past.</summary>
    public static DateTime Nudge(DateTime dueLocal, int minutes, DateTime nowLocal)
    {
        var t = dueLocal.AddMinutes(minutes);
        return t <= nowLocal ? dueLocal : t;
    }

    /// <summary>People for the parser, most recent first: callbacks (name ↔ phone), then deals.</summary>
    public static List<QuickPerson> People(IEnumerable<ClientCard> cards) =>
        cards.Where(c => c.Name.Length > 0 && !c.Name.Any(char.IsAsciiDigit))
             .Select(c => new QuickPerson(c.Name, c.Phone))
             .ToList();

    static DateTime Trim(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
}

/// <summary>The three micro rings on the resting pill — today's calls,
/// deposits and callbacks done, each against a small daily goal.</summary>
readonly record struct DockRings(int Calls, int CallsGoal, int Deposits, int DepositsGoal, int CallbacksDone, int CallbacksGoal, int Overdue)
{
    public double CallsP => Frac(Calls, CallsGoal);
    public double DepositsP => Frac(Deposits, DepositsGoal);
    public double CallbacksP => Frac(CallbacksDone, CallbacksGoal);

    static double Frac(int n, int goal) => goal <= 0 ? 0 : Math.Clamp(n / (double)goal, 0, 1);

    public string Label => $"היום: {Calls} שיחות, {Deposits} הפקדות, {CallbacksDone} חזרות";

    public const int DefaultCallsGoal = 40;

    /// <summary>
    /// Deposits goal: the monthly target spread over the month's work days
    /// (at least 1). Callbacks goal: what's done today plus what's still owed today.
    /// </summary>
    public static DockRings Compute(DateTime nowLocal, IEnumerable<DateTime> callStartsLocal, IEnumerable<DateTime> dealDates,
        int? monthlyTarget, IReadOnlyList<Callback> callbacks)
    {
        var today = nowLocal.Date;
        var calls = callStartsLocal.Count(t => t.Date == today);
        var deposits = dealDates.Count(d => d.Date == today);
        var done = callbacks.Count(c => c.Status == CallbackStatus.Done && c.CompletedUtc is { } u && u.ToLocalTime().Date == today);
        var counts = CallbackPlanner.Counts(callbacks, nowLocal);
        var owedToday = callbacks.Count(c => c.IsActive && c.DueAtUtc.ToLocalTime().Date == today);
        var workDays = 0;
        for (var d = new DateTime(today.Year, today.Month, 1); d.Month == today.Month; d = d.AddDays(1))
            if (CallbackPlanner.IsWorkDay(d.DayOfWeek)) workDays++;
        var depGoal = monthlyTarget is int t && t > 0 ? Math.Max(1, (int)Math.Ceiling(t / (double)Math.Max(1, workDays))) : 1;
        return new DockRings(calls, DefaultCallsGoal, deposits, depGoal, done, Math.Max(1, done + owedToday), counts.Overdue);
    }
}

/// <summary>The 2–3 templates pinned to the hover row.</summary>
static class DockPins
{
    public static readonly string[] Defaults = { "open-pro", "deposit-pro" };

    /// <summary>Pinned ids in order (unknown ids skipped); none set → the
    /// defaults, else the first templates. At most <paramref name="max"/>.</summary>
    public static List<MessageTemplate> Pick(IReadOnlyList<MessageTemplate> templates, string? pinnedCsv, int max = 3)
    {
        var ids = (pinnedCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = ids.Select(id => templates.FirstOrDefault(t => t.Id == id)).OfType<MessageTemplate>().ToList();
        if (chosen.Count == 0 && ids.Length == 0)
            chosen = Defaults.Select(id => templates.FirstOrDefault(t => t.Id == id)).OfType<MessageTemplate>().ToList();
        if (chosen.Count == 0) chosen = templates.Take(2).ToList();
        return chosen.Distinct().Take(max).ToList();
    }

    /// <summary>"הפקדת כספים · פרו" → "הפקדת כספים"; clipped for a chip.</summary>
    public static string ShortLabel(MessageTemplate t)
    {
        var title = t.Title.Trim();
        var dot = title.IndexOf('·');
        if (dot > 0) title = title[..dot].Trim();
        return title.Length > 14 ? title[..13].TrimEnd() + "…" : title;
    }
}
