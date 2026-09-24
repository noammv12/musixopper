using System.Globalization;
using Palon.Notes;

namespace Palon.Terminal;

/// <summary>Hebrew formatting shared by the Terminal: numbers, days, times,
/// relative "when" labels. Pure — everything takes "now".</summary>
static class He
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    static readonly string[] Days = { "ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "שבת" };

    static readonly string[] Months =
        { "ינואר", "פברואר", "מרץ", "אפריל", "מאי", "יוני", "יולי", "אוגוסט", "ספטמבר", "אוקטובר", "נובמבר", "דצמבר" };

    public static string Day(DayOfWeek d) => Days[(int)d];

    public static string Month(int month) => Months[Math.Clamp(month, 1, 12) - 1];

    /// <summary>1,234 — grouping with commas, like the sheet.</summary>
    public static string N(double value) => Math.Round(value).ToString("#,0", En);

    public static string N(decimal value) => Math.Round(value).ToString("#,0", En);

    public static string N(int value) => value.ToString("#,0", En);

    /// <summary>One decimal, trailing ".0" dropped: 2.5, 3.</summary>
    public static string R1(double value) => Math.Round(value, 1).ToString("0.#", En);

    /// <summary>24.9 — the day.month shorthand the user writes.</summary>
    public static string DayMonth(DateTime d) => $"{d.Day}.{d.Month}";

    public static string Clock(DateTime d) => d.ToString("HH:mm", En);

    /// <summary>6:12 — call length as m:ss.</summary>
    public static string Duration(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>"חמישי 24.9 · 14:20" — the menu bar clock.</summary>
    public static string MenuClock(DateTime now) => $"{Day(now.DayOfWeek)} {DayMonth(now)} · {Clock(now)}";

    /// <summary>Greeting by hour, no name: the app never asked for one.</summary>
    public static string Greeting(DateTime now) => now.Hour switch
    {
        >= 5 and < 12 => "בוקר טוב",
        >= 12 and < 17 => "צהריים טובים",
        >= 17 and < 22 => "ערב טוב",
        _ => "לילה טוב",
    };

    /// <summary>When a callback is due, as short as it can be: "16:00" today
    /// (also for today's overdue), "מחר 10:00", "ראשון 12:00" this week,
    /// "3.10 · 10:00" beyond; "אתמול 11:30" / "22.9 · 11:30" when long overdue.</summary>
    public static string When(DateTime dueLocal, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var day = dueLocal.Date;
        var clock = Clock(dueLocal);
        if (day == today) return clock;
        if (day == today.AddDays(1)) return $"מחר {clock}";
        if (day == today.AddDays(-1)) return $"אתמול {clock}";
        if (day > today && day < today.AddDays(7)) return $"{Day(day.DayOfWeek)} {clock}";
        return $"{DayMonth(day)} · {clock}";
    }

    /// <summary>"היום 14:12" / "אתמול 17:48" / "22.9 · 11:15" — for timelines.</summary>
    public static string Ago(DateTime local, DateTime nowLocal)
    {
        var clock = Clock(local);
        if (local.Date == nowLocal.Date) return $"היום {clock}";
        if (local.Date == nowLocal.Date.AddDays(-1)) return $"אתמול {clock}";
        return $"{DayMonth(local)} · {clock}";
    }

    public static string Plural(int n, string one, string many) => n == 1 ? one : $"{n} {many}";
}

/// <summary>The call-summary text the user pastes into Salesforce.</summary>
static class CallSummary
{
    /// <summary>
    /// "סיכום שיחה · דני לוי · 24.9 14:12 (6:12)" then the note, then the
    /// callback when one was agreed. Pure; the CALLBACK marker line is
    /// already stripped by the pipeline, but a stray one is dropped too.
    /// </summary>
    public static string Format(CallNote note, string? who, DateTime startedLocal)
    {
        var name = string.IsNullOrWhiteSpace(who) ? note.Number ?? "שיחה" : who!.Trim();
        var header = $"סיכום שיחה · {name} · {He.DayMonth(startedLocal)} {He.Clock(startedLocal)} ({He.Duration(note.DurationSec)})";
        var body = (note.Summary ?? "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith(CallbackProposals.Marker, StringComparison.Ordinal));
        var text = string.Join("\n", body);
        if (text.Length == 0) text = Shorten(note.Transcript, 400);
        var lines = new List<string> { header };
        if (text.Length > 0) lines.Add(text);
        if (note.ProposedCallback is { State: not "dismissed" } p)
        {
            var when = p.WhenUtc.ToLocalTime();
            lines.Add($"חזרה: {He.Day(when.DayOfWeek)} {He.DayMonth(when)} {He.Clock(when)}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>The one line a list row shows for a call.</summary>
    public static string Line(CallNote note)
    {
        var s = (note.Summary ?? "").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith(CallbackProposals.Marker, StringComparison.Ordinal));
        return s ?? (note.Transcript.Length > 0 ? Shorten(note.Transcript, 140) : "אין סיכום");
    }

    static string Shorten(string text, int max)
    {
        var t = (text ?? "").Replace('\n', ' ').Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }
}
