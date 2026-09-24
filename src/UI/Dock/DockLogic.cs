using System.Globalization;
using System.Text;
using Palon.Notes;

namespace Palon.UI;

/// <summary>
/// Hooks the dock calls into features that live elsewhere (and may land on a
/// different branch). <see cref="OpenTerminal"/> defaults to a no-op; the
/// Terminal window's owner sets it at startup, e.g.
/// <c>DockActions.OpenTerminal = TerminalWindow.ShowSingleton;</c>.
/// Until then the dock hides the Terminal chip (<see cref="HasTerminal"/>);
/// setting it later re-renders the chips.
/// </summary>
static class DockActions
{
    static Action? _openTerminal;

    public static Action OpenTerminal
    {
        get => _openTerminal ?? (() => { });
        set
        {
            _openTerminal = value;
            Changed?.Invoke(); // the dock re-renders its chips (the Terminal chip appears)
        }
    }

    public static event Action? Changed;

    public static bool HasTerminal => _openTerminal is not null;

    /// <summary>"Log to Salesforce" for a finished call (note + the callback the user booked, if any).</summary>
    public static Action<CallNote, DateTime?> LogToSalesforce { get; set; } = (_, _) => { };

    /// <summary>Raised by the Settings sheet when a dock-visible setting changed (pins, calls goal).</summary>
    public static event Action? SettingsChanged;

    public static void NotifySettingsChanged() => SettingsChanged?.Invoke();
}

enum DockCardKind { AfterCall, CallbackDue, Nudge }

/// <summary>One card the dock owes the user: a finished call's note, or a
/// callback that came due (Missed = fired late).</summary>
sealed record DockCard(DockCardKind Kind, CallNote? Note = null, Callback? Callback = null, bool Missed = false,
    Palon.Agentic.Nudge? Nudge = null)
{
    public string Key => Kind switch
    {
        DockCardKind.AfterCall => "note:" + Note?.Id,
        DockCardKind.Nudge => "nudge:" + Nudge?.Id,
        _ => "cb:" + Callback?.Id,
    };

    public static DockCard ForNudge(Palon.Agentic.Nudge nudge) => new(DockCardKind.Nudge, Nudge: nudge);

    /// <summary>The dock shows only high-priority nudges: reminders, and
    /// suggestions that offer an action. Insights live in the Now window.</summary>
    public static bool DockWorthy(Palon.Agentic.Nudge nudge) =>
        // A missed callback already has the dock's own callback-due card (CallbackScheduler) — never both.
        !nudge.Id.StartsWith("missed:", StringComparison.Ordinal) &&
        (nudge.Kind == Palon.Agentic.NudgeKind.Reminder
        || (nudge.Kind == Palon.Agentic.NudgeKind.Suggestion && nudge.Act is not null));

    public static DockCard ForNote(CallNote note) => new(DockCardKind.AfterCall, Note: note);
    public static DockCard ForCallback(Callback callback, bool missed) => new(DockCardKind.CallbackDue, Callback: callback, Missed: missed);
}

/// <summary>
/// The dock's card line-up, pure so the rules are testable:
/// <list type="bullet">
/// <item>the showing card is never replaced by an arrival — arrivals queue;</item>
/// <item>due callbacks queue ahead of after-call cards (time-sensitive);</item>
/// <item>the same note/callback twice replaces its queued entry, never doubles;</item>
/// <item>a call starting drops after-call cards (the note lives in Notes) and
/// parks a showing callback card at the front of the line.</item>
/// </list>
/// </summary>
sealed class DockCardQueue
{
    readonly List<DockCard> _waiting = new();

    public DockCard? Current { get; private set; }
    public int WaitingCount => _waiting.Count;
    public IReadOnlyList<DockCard> Waiting => _waiting;

    /// <summary>Adds a card. Returns true when it became Current (nothing was showing).</summary>
    public bool Enqueue(DockCard card)
    {
        if (Current is { } cur && cur.Key == card.Key)
        {
            Current = card; // fresher data for the showing card
            return false;
        }
        _waiting.RemoveAll(c => c.Key == card.Key);
        if (card.Kind == DockCardKind.Nudge)
        {
            // At most one nudge in the line-up: a fresher one replaces the
            // waiting one; a showing nudge is never swapped under the pointer.
            if (Current is { Kind: DockCardKind.Nudge }) return false;
            _waiting.RemoveAll(c => c.Kind == DockCardKind.Nudge);
        }
        if (Current is null)
        {
            Current = card;
            return true;
        }
        if (card.Kind == DockCardKind.CallbackDue)
        {
            var firstNote = _waiting.FindIndex(c => c.Kind != DockCardKind.CallbackDue);
            _waiting.Insert(firstNote < 0 ? _waiting.Count : firstNote, card);
        }
        else if (card.Kind == DockCardKind.AfterCall)
        {
            var firstNudge = _waiting.FindIndex(c => c.Kind == DockCardKind.Nudge);
            _waiting.Insert(firstNudge < 0 ? _waiting.Count : firstNudge, card);
        }
        else
        {
            _waiting.Add(card);
        }
        return false;
    }

    /// <summary>Retires the showing card and promotes the next one (or null).</summary>
    public DockCard? Advance()
    {
        Current = null;
        if (_waiting.Count == 0) return null;
        Current = _waiting[0];
        _waiting.RemoveAt(0);
        return Current;
    }

    /// <summary>Nothing showing but something waiting (e.g. after a park) → promote.</summary>
    public DockCard? Resume() => Current ?? Advance();

    public void OnCallStarted()
    {
        _waiting.RemoveAll(c => c.Kind is DockCardKind.AfterCall or DockCardKind.Nudge);
        if (Current is { Kind: DockCardKind.AfterCall or DockCardKind.Nudge }) Current = null;
        else if (Current is { } parked)
        {
            _waiting.Insert(0, parked);
            Current = null;
        }
    }

    /// <summary>Drops a queued/showing callback that was handled elsewhere (e.g. the flyout).</summary>
    public void Forget(string key)
    {
        _waiting.RemoveAll(c => c.Key == key);
        if (Current?.Key == key) Advance();
    }
}

/// <summary>One "when to call back" chip on the after-call card.</summary>
sealed record DockPick(string Key, string Label, DateTime DueLocal, bool Enabled, bool Heard);

/// <summary>The dock's Hebrew copy and small formatting rules — pure.</summary>
static class DockText
{
    static readonly string[] HebrewDays = { "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "שבת" };

    public static string Day(DayOfWeek day) => HebrewDays[(int)day];

    /// <summary>m:ss, or h:mm:ss past the hour.</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    /// <summary>Call timer: mm:ss, or h:mm:ss past the hour.</summary>
    public static string Timer(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

    /// <summary>"היום 15:00" / "מחר 10:00" / "ראשון 12:00" (within a week) / "3.10 10:00".</summary>
    public static string When(DateTime dueLocal, DateTime nowLocal)
    {
        var time = dueLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (dueLocal.Date - nowLocal.Date).Days;
        return days switch
        {
            0 => $"היום {time}",
            1 => $"מחר {time}",
            > 1 and < 7 => $"{Day(dueLocal.DayOfWeek)} {time}",
            _ => $"{dueLocal.Day}.{dueLocal.Month} {time}",
        };
    }

    /// <summary>Monthly progress "40/55"; null when no target is set.</summary>
    public static string? Progress(int count, int? target) =>
        target is int t && t > 0 ? $"{count}/{t}" : null;

    public static string? Overdue(int overdue) => overdue > 0 ? $"{overdue} באיחור" : null;

    public static string RemainingToday(int remaining) => remaining switch
    {
        <= 0 => "סומן כבוצע · סיימת את החזרות להיום",
        1 => "סומן כבוצע · נשארה לך חזרה אחת היום",
        _ => $"סומן כבוצע · נשארו לך {remaining} היום",
    };

    /// <summary>The due card's "done" line: "בוצע · נשארו 3 היום".</summary>
    public static string DoneLeft(int remaining) => remaining switch
    {
        <= 0 => "בוצע · סיימת להיום",
        1 => "בוצע · נשארה אחת היום",
        _ => $"בוצע · נשארו {remaining} היום",
    };

    public static string SnoozedUntil(DateTime dueLocal) =>
        $"נזכיר שוב ב-{dueLocal.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>Wraps a run in FIRST STRONG ISOLATE … POP so a number or a
    /// Latin name inside a Hebrew line keeps its own order and can't drag
    /// the neighbouring separators around (Bidi.Isolate only wraps RTL runs).</summary>
    public static string Iso(string text) => "\u2068" + text + "\u2069";

    /// <summary>"הגיע הזמן לחזור לדני" — a Hebrew name takes the ל prefix
    /// directly; a number or Latin name gets "ל-" and a bidi isolate.</summary>
    public static string TimeToCall(string who) =>
        who.Length > 0 && Bidi.HasRtl(who[..1]) ? $"הגיע הזמן לחזור ל{who}" : $"הגיע הזמן לחזור ל-{Iso(who)}";

    /// <summary>"השיחה עם 050… הסתיימה".</summary>
    public static string CallEnded(string who) => $"השיחה עם {Iso(who)} הסתיימה";

    /// <summary>"קבעת ל-10:00" (today) / "קבעת ל-מחר 10:00"; missed ones lead with "באיחור".</summary>
    public static string DueLine(DateTime dueLocal, DateTime nowLocal, bool missed)
    {
        var at = dueLocal.Date == nowLocal.Date
            ? dueLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : When(dueLocal, nowLocal);
        return (missed ? "באיחור · " : "") + $"קבעת ל-{at}";
    }

    /// <summary>Who the call was with: the number (notes carry no names yet).</summary>
    public static string Who(CallNote note) => note.Number is { Length: > 0 } n ? n : "הלקוח";

    public static string Who(Callback callback) =>
        !string.IsNullOrEmpty(callback.Name) ? callback.Name!
        : callback.HasPhone ? callback.Phone!
        : callback.DisplayLabel;

    /// <summary>The card's one-liner: the summary's first real line, bullet dressing stripped.</summary>
    public static string OneLine(CallNote note)
    {
        if (note.Summary is not { Length: > 0 } summary)
            return note.SummaryError is { Length: > 0 } reason
                ? $"לא נוצר סיכום: {reason} — התמליל שמור בהערות"
                : "לא נוצר סיכום — התמליל שמור בהערות";
        foreach (var raw in summary.Split('\n'))
        {
            var line = raw.Trim().TrimStart('•', '-', '*', ' ').Trim();
            if (line.Length > 0) return line;
        }
        return summary.Trim();
    }

    /// <summary>
    /// The pick row: the planner's four standard chips in Hebrew, with
    /// Palon's heard proposal marked. A proposal that lands on a standard
    /// chip's minute highlights that chip; otherwise it's its own chip, first.
    /// </summary>
    public static List<DockPick> Picks(IReadOnlyList<QuickPick> standard, CallbackProposal? proposal, DateTime nowLocal)
    {
        var heardLocal = proposal is { IsPending: true } p ? p.WhenUtc.ToLocalTime() : (DateTime?)null;
        var picks = new List<DockPick>(standard.Count + 1);
        var matched = false;
        foreach (var q in standard)
        {
            var label = PickLabel(q, nowLocal);
            // "Sunday" folds away when "tomorrow" already is Sunday — two
            // identical chips read as a bug, not a stable layout.
            if (picks.Exists(p => p.Label == label)) continue;
            var heard = heardLocal is { } h && q.Enabled && Math.Abs((q.DueLocal - h).TotalMinutes) < 1;
            matched |= heard;
            picks.Add(new DockPick(q.Key, label, q.DueLocal, q.Enabled, heard));
        }
        if (heardLocal is { } when && !matched && when > nowLocal)
            picks.Insert(0, new DockPick("heard", When(when, nowLocal), when, true, Heard: true));
        return picks;
    }

    static string PickLabel(QuickPick q, DateTime nowLocal) => q.Key switch
    {
        "in1h" => "בעוד שעה",
        "tonight" => "הערב 19:00",
        "sunday" => "ראשון 10:00",
        _ => When(q.DueLocal, nowLocal),
    };

    /// <summary>"Palon שמע: ״…״" — the phrase, clipped for one line.</summary>
    public static string? Heard(CallbackProposal? proposal)
    {
        if (proposal is not { IsPending: true, Phrase.Length: > 0 } p) return null;
        var phrase = p.Phrase.Trim().Trim('"', '״', '\'');
        if (phrase.Length > 48) phrase = phrase[..48].TrimEnd() + "…";
        return $"Palon שמע בשיחה: ״{phrase}״";
    }

    /// <summary>
    /// A Salesforce-ready activity note: who, date/time, duration, the
    /// summary, and the next step (the note's own next-step line, else the
    /// callback just booked). Plain text, one fact per line.
    /// </summary>
    public static string SalesforceSummary(CallNote note, DateTime? callbackLocal, DateTime nowLocal)
    {
        var started = note.StartedUtc.ToLocalTime();
        var sb = new StringBuilder();
        sb.Append("סיכום שיחה · ").Append(Who(note)).Append(" · ")
          .Append(started.ToString("d.M.yyyy HH:mm", CultureInfo.InvariantCulture))
          .Append(" (").Append(Duration(note.DurationSec)).Append(')').Append('\n');

        string? nextStep = null;
        var body = note.Summary is { Length: > 0 } s ? s : note.Transcript;
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var bare = line.TrimStart('•', '-', '*', ' ').Trim();
            if (bare.StartsWith("הצעד הבא:") || bare.StartsWith("Next step:", StringComparison.OrdinalIgnoreCase))
            {
                nextStep = bare[(bare.IndexOf(':') + 1)..].Trim();
                continue;
            }
            sb.Append(line.StartsWith('•') ? line : "• " + bare).Append('\n');
        }
        if (callbackLocal is { } cb)
        {
            var booked = "חזרה " + When(cb, nowLocal);
            nextStep = nextStep is { Length: > 0 } ? $"{nextStep} ({booked})" : booked;
        }
        if (nextStep is { Length: > 0 }) sb.Append("צעד הבא: ").Append(nextStep).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
