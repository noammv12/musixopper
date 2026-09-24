using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Notes;

/// <summary>
/// A callback promise heard on a call ("אחזור אליך מחר ב-11") — proposed,
/// never auto-created: the UI shows "Palon heard: '…' → Tomorrow 11:00" and
/// the user accepts or dismisses. State: "proposed" | "accepted" | "dismissed".
/// </summary>
sealed record CallbackProposal(
    DateTime WhenUtc,
    string Phrase,
    string? Reason = null,
    string State = "proposed",
    string? CallbackId = null)
{
    public bool IsPending => State == "proposed";
}

/// <summary>
/// Pulls the optional trailing <c>CALLBACK: {...}</c> line out of a summary
/// reply (the summary prompt asks for it only when a time was agreed) and
/// validates it. The model's when_iso is checked against — and, when it's
/// missing or implausible, replaced by — a deterministic read of the phrase.
/// Anything malformed yields no proposal; the summary itself is never lost.
/// </summary>
static class CallbackProposals
{
    public const string Marker = "CALLBACK:";
    static readonly TimeSpan MaxAhead = TimeSpan.FromDays(90);
    static readonly TimeSpan Slack = TimeSpan.FromMinutes(5);

    /// <summary>Splits the reply into the clean note and an optional proposal.
    /// <paramref name="callEndLocal"/> anchors relative phrases.</summary>
    public static (string Summary, CallbackProposal? Proposal) Extract(string reply, DateTime callEndLocal)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindLastIndex(l => l.TrimStart().TrimStart('*', '`', ' ').StartsWith(Marker, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (reply.Trim(), null);

        var line = lines[index];
        lines.RemoveAt(index);
        var summary = string.Join("\n", lines).Trim();
        if (summary.Length == 0) summary = reply.Trim(); // never swap a note for nothing
        return (summary, ParseLine(line, callEndLocal));
    }

    internal static CallbackProposal? ParseLine(string line, DateTime callEndLocal)
    {
        var start = line.IndexOf('{');
        var end = line.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(line[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var phrase = Str(root, "phrase") ?? "";
            var reason = Str(root, "reason");

            DateTime? model = null;
            if (Str(root, "when_iso") is { } iso &&
                DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                // Offsets/Z are converted to local; naive stamps are already local.
                var local = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
                if (Plausible(local, callEndLocal)) model = DateTime.SpecifyKind(local, DateTimeKind.Local);
            }

            // The phrase wins when it pins down a full time on its own (it's
            // exact for what it understands); otherwise trust the model.
            DateTime? when = TimePhrase.TryResolve(phrase, callEndLocal, out var fromPhrase, out var complete) &&
                             Plausible(fromPhrase, callEndLocal) && (complete || model is null)
                ? fromPhrase
                : model;
            if (when is not { } w) return null;
            return new CallbackProposal(w.ToUniversalTime(), phrase.Trim(), string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static bool Plausible(DateTime whenLocal, DateTime callEndLocal) =>
        whenLocal > callEndLocal - Slack && whenLocal < callEndLocal + MaxAhead;

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Turns a pending proposal into a callback (phone from the
    /// note, note text from the reason/phrase) and marks it accepted.</summary>
    public static Callback? Accept(CallNote note, DateTime? overrideDueUtc = null)
    {
        if (note.ProposedCallback is not { IsPending: true } proposal) return null;
        var text = proposal.Reason ?? (proposal.Phrase.Length > 0 ? $"\"{proposal.Phrase}\"" : "Call back");
        var callback = CallbackStore.Add(text, overrideDueUtc ?? proposal.WhenUtc, phone: note.Number,
            source: CallbackSource.AutoCall, callNoteId: note.Id);
        if (callback is null) return null;
        NotesStore.Update(note with { ProposedCallback = proposal with { State = "accepted", CallbackId = callback.Id } });
        return callback;
    }

    public static void Dismiss(CallNote note)
    {
        if (note.ProposedCallback is not { IsPending: true } proposal) return;
        NotesStore.Update(note with { ProposedCallback = proposal with { State = "dismissed" } });
    }
}

/// <summary>
/// Deterministic Hebrew/English callback-time reader for the common shapes:
/// relative ("in an hour", "בעוד שעתיים"), a day ("מחר", "ביום ראשון",
/// "Sunday", "next week") and/or an hour ("ב-11", "at 3:30pm", "בשלוש",
/// "בצהריים"). Days resolve literally — a client may well ask for Friday.
/// Bare hours 1–7 mean afternoon (nobody books a 3 a.m. callback).
/// </summary>
static class TimePhrase
{
    static readonly (string Word, int Hour)[] HebrewHours =
    {
        ("אחת עשרה", 11), ("אחת-עשרה", 11), ("שתים עשרה", 12), ("שתיים עשרה", 12),
        ("אחת", 1), ("שתיים", 2), ("שלוש", 3), ("ארבע", 4), ("חמש", 5), ("שש", 6),
        ("שבע", 7), ("שמונה", 8), ("תשע", 9), ("עשר", 10),
    };

    static readonly (string Word, DayOfWeek Day)[] HebrewDays =
    {
        ("ראשון", DayOfWeek.Sunday), ("שני", DayOfWeek.Monday), ("שלישי", DayOfWeek.Tuesday),
        ("רביעי", DayOfWeek.Wednesday), ("חמישי", DayOfWeek.Thursday), ("שישי", DayOfWeek.Friday),
    };

    static readonly Regex RelativeEn = new(@"\bin\s+(an?|one|two|three|half an?|\d+)\s*(hours?|hrs?|minutes?|mins?)\b", RegexOptions.IgnoreCase);
    static readonly Regex RelativeHeNum = new(@"(?:בעוד|עוד)\s+(\d+)\s*(שעות|שעה|דקות|דק)");
    static readonly Regex ClockHour = new(@"(?<![\d:])(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?(?![\d:])", RegexOptions.IgnoreCase);
    static readonly Regex HourCue = new(@"(?:\bat\s+|@\s*|בשעה\s*|(?<![א-ת])ב-?\s?)(\d{1,2})(?::(\d{2}))?\s*(am|pm)?", RegexOptions.IgnoreCase);

    /// <summary>True when something time-like was understood. <paramref name="complete"/>
    /// is true when the phrase fixes a moment (relative offset, or an hour /
    /// part of day) rather than just a day with a default 10:00.</summary>
    public static bool TryResolve(string? phrase, DateTime refLocal, out DateTime whenLocal, out bool complete)
    {
        whenLocal = default;
        complete = false;
        var text = (phrase ?? "").Trim();
        if (text.Length == 0) return false;
        var lower = text.ToLowerInvariant();
        var now = new DateTime(refLocal.Year, refLocal.Month, refLocal.Day, refLocal.Hour, refLocal.Minute, 0, refLocal.Kind);

        // ---- relative offsets: complete on their own
        if (Relative(lower) is { } offset)
        {
            whenLocal = now + offset;
            complete = true;
            return true;
        }

        // ---- day
        DateTime? day = null;
        if (lower.Contains("מחרתיים") || lower.Contains("day after tomorrow")) day = now.Date.AddDays(2);
        else if (lower.Contains("מחר") || lower.Contains("tomorrow")) day = now.Date.AddDays(1);
        else if (lower.Contains("שבוע הבא") || lower.Contains("next week")) day = CallbackPlanner.NextSundayMorning(now).Date;
        else if (lower.Contains("היום") || lower.Contains("today") || lower.Contains("הערב") || lower.Contains("tonight")) day = now.Date;
        else if (WeekdayOf(lower) is { } dow)
        {
            var ahead = ((int)dow - (int)now.DayOfWeek + 7) % 7;
            day = now.Date.AddDays(ahead == 0 ? 7 : ahead); // "on Thursday" said on Thursday → next week
        }

        // ---- part of day
        int? partHour = null;
        var pm = false;
        if (lower.Contains("אחר הצהריים") || lower.Contains("אחה\"צ") || lower.Contains("afternoon")) { partHour = 15; pm = true; }
        else if (lower.Contains("צהריים") || lower.Contains("noon") || lower.Contains("lunch")) { partHour = 12; pm = true; }
        else if (lower.Contains("ערב") || lower.Contains("evening") || lower.Contains("tonight")) { partHour = 19; pm = true; }
        else if (lower.Contains("בוקר") || lower.Contains("morning")) partHour = 10;

        // ---- hour
        var hour = HourOf(lower, out var minute, out var meridiem);
        if (hour is { } h)
        {
            if (meridiem == "pm" && h < 12) h += 12;
            else if (meridiem == "am" && h == 12) h = 0;
            else if (meridiem is null && h < 12 && (pm || (partHour is null && h <= 7))) h += 12;
            if (h > 23 || minute > 59) return false;
            hour = h;
        }

        if (day is null && hour is null && partHour is null) return false;
        complete = hour is not null || partHour is not null;
        var time = hour is { } hh ? new TimeSpan(hh, minute, 0)
            : partHour is { } ph ? TimeSpan.FromHours(ph)
            : CallbackPlanner.MorningAt;
        if (day is { } d)
        {
            whenLocal = d + time;
        }
        else
        {
            // Just a time: today if still ahead, else tomorrow.
            whenLocal = now.Date + time;
            if (whenLocal <= now) whenLocal = whenLocal.AddDays(1);
        }
        return true;
    }

    static TimeSpan? Relative(string lower)
    {
        if (lower.Contains("חצי שעה") || lower.Contains("half an hour") || lower.Contains("half hour")) return TimeSpan.FromMinutes(30);
        if (lower.Contains("בעוד שעתיים") || lower.Contains("עוד שעתיים")) return TimeSpan.FromHours(2);
        if (lower.Contains("בעוד שעה") || lower.Contains("עוד שעה")) return TimeSpan.FromHours(1);
        if (lower.Contains("בעוד רבע שעה") || lower.Contains("quarter of an hour")) return TimeSpan.FromMinutes(15);

        var he = RelativeHeNum.Match(lower);
        if (he.Success && int.TryParse(he.Groups[1].Value, out var hn) && hn is > 0 and < 1000)
            return he.Groups[2].Value.StartsWith("ד") ? TimeSpan.FromMinutes(hn) : TimeSpan.FromHours(hn);

        var en = RelativeEn.Match(lower);
        if (!en.Success) return null;
        var qty = en.Groups[1].Value switch
        {
            "a" or "an" or "one" => 1.0,
            "two" => 2,
            "three" => 3,
            var s when s.StartsWith("half") => 0.5,
            var s => int.TryParse(s, out var n) && n is > 0 and < 1000 ? n : 0,
        };
        if (qty <= 0) return null;
        return en.Groups[2].Value.StartsWith("h") ? TimeSpan.FromHours(qty) : TimeSpan.FromMinutes(qty);
    }

    static DayOfWeek? WeekdayOf(string lower)
    {
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
            if (Regex.IsMatch(lower, $@"\b{d.ToString().ToLowerInvariant()}\b")) return d;
        foreach (var (word, d) in HebrewDays)
            if (lower.Contains("יום " + word) || lower.Contains("ביום " + word)) return d;
        if (Regex.IsMatch(lower, "(?<![א-ת])ב?שבת(?![א-ת])")) return DayOfWeek.Saturday;
        return null;
    }

    static int? HourOf(string lower, out int minute, out string? meridiem)
    {
        minute = 0;
        meridiem = null;
        var cue = HourCue.Match(lower);
        var match = cue.Success ? cue : null;
        if (match is null)
        {
            // A bare clock time only counts with a colon or am/pm ("11:30", "3pm") —
            // otherwise "2" in "in 2 weeks" or a price would read as an hour.
            foreach (Match m in ClockHour.Matches(lower))
                if (m.Groups[2].Success || m.Groups[3].Success) { match = m; break; }
        }
        if (match is not null && int.TryParse(match.Groups[1].Value, out var h))
        {
            if (match.Groups[2].Success) minute = int.Parse(match.Groups[2].Value);
            if (match.Groups[3].Success) meridiem = match.Groups[3].Value.StartsWith("p") ? "pm" : "am";
            return h;
        }
        foreach (var (word, hour) in HebrewHours)
            if (Regex.IsMatch(lower, $"(?<![א-ת])(?:ב|בשעה\\s)-?{word}(?![א-ת])"))
            {
                if (lower.Contains(word + " וחצי")) minute = 30;
                return hour;
            }
        return null;
    }
}
