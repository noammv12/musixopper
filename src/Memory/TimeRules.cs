using System.Globalization;
using System.Text.RegularExpressions;

namespace Palon.Memory;

/// <summary>
/// A scheduling rule from "About you", enforced in code (not just the
/// prompt): calls to <see cref="Who"/> (or <see cref="Phone"/>) only between
/// NotBefore and NotAfter, local time. "Don't call Dani before 12" =
/// { Who: "Dani", NotBefore: 12:00 }.
/// </summary>
sealed record TimeRule(string Who, string? Phone = null, TimeSpan? NotBefore = null, TimeSpan? NotAfter = null)
{
    public bool IsValid => (Who.Trim().Length > 0 || !string.IsNullOrWhiteSpace(Phone)) && (NotBefore is not null || NotAfter is not null)
                           && !(NotBefore is { } b && NotAfter is { } a && a <= b);

    public string Describe()
    {
        var who = Who.Trim().Length > 0 ? Who.Trim() : Phone ?? "";
        return (NotBefore, NotAfter) switch
        {
            ({ } b, { } a) => $"{who}: רק בין {b:hh\\:mm} ל-{a:hh\\:mm}",
            ({ } b, null) => $"{who}: לא לפני {b:hh\\:mm}",
            (null, { } a) => $"{who}: לא אחרי {a:hh\\:mm}",
            _ => who,
        };
    }
}

static class TimeRules
{
    static readonly TimeSpan DefaultMorning = TimeSpan.FromHours(10);

    // "אל תתקשר לדני לפני 12", "לא להתקשר לדני אחרי 18:30", "אסור לחייג לדני לפני השעה 12"
    static readonly Regex Hebrew = new(
        @"(?:אל|לא|אסור|אין)\s+(?:ל?ה?תקשר|תתקשר|להתקשר|לחייג|תחייג|להרים\s+טלפון|תרים\s+טלפון)\s+(?:אל\s+|ל-?)?(?<who>[^\s,.]+(?:\s+(?!לפני|אחרי|מאחרי|בשעה)[^\s,.\d]+)?)\s+(?<dir>לפני|אחרי|מאחרי)\s+(?:השעה\s+|ה-?)?(?<h>\d{1,2})(?::(?<m>\d{2}))?",
        RegexOptions.CultureInvariant);

    // "don't call Dani before 12", "never call Dani after 6pm", "do not phone Dani before 11:30"
    static readonly Regex English = new(
        @"(?:don'?t|do\s+not|never|no)\s+(?:call|phone|ring|calls?\s+to)\s+(?<who>[A-Za-zא-ת][\wא-ת'-]*(?:\s+(?!before|after)[A-Za-zא-ת][\wא-ת'-]*)?)\s+(?<dir>before|after)\s+(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Reads a time-window rule out of free text, or null.</summary>
    public static TimeRule? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Hebrew.Match(text);
        var hebrew = m.Success;
        if (!hebrew) m = English.Match(text);
        if (!m.Success) return null;

        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var ap = m.Groups["ap"].Success ? m.Groups["ap"].Value.ToLowerInvariant() : "";
        if (ap.StartsWith("p") && hour < 12) hour += 12;
        else if (ap.StartsWith("a") && hour == 12) hour = 0;
        else if (ap.Length == 0 && hour is >= 1 and <= 7) hour += 12; // "after 6" = 18:00 in a sales day
        if (hour > 23 || minute > 59) return null;

        var who = m.Groups["who"].Value.Trim();
        var at = new TimeSpan(hour, minute, 0);
        var dir = m.Groups["dir"].Value.ToLowerInvariant();
        var before = dir is "לפני" or "before";
        var rule = before ? new TimeRule(who, NotBefore: at) : new TimeRule(who, NotAfter: at);
        return rule.IsValid ? rule : null;
    }

    /// <summary>Rules that apply to this client — phone match wins; else a name token match.</summary>
    public static List<TimeRule> For(IEnumerable<TimeRule> rules, string? name, string? phone) => rules
        .Where(r => r.IsValid)
        .Where(r => (!string.IsNullOrWhiteSpace(r.Phone) && Palon.Agent.PhoneMatch.Same(r.Phone, phone))
                    || (r.Who.Trim().Length > 0 && HebrewText.SameName(r.Who, name)))
        .ToList();

    public static bool Allowed(TimeRule rule, DateTime local)
    {
        var t = local.TimeOfDay;
        if (rule.NotBefore is { } b && t < b) return false;
        if (rule.NotAfter is { } a && t > a) return false;
        return true;
    }

    /// <summary>The first time at or after <paramref name="local"/> every rule
    /// allows (same day when possible, else the next morning window).</summary>
    public static DateTime NextAllowed(IReadOnlyList<TimeRule> rules, DateTime local)
    {
        var t = local;
        for (var guard = 0; guard < 8; guard++)
        {
            var broken = rules.FirstOrDefault(r => !Allowed(r, t));
            if (broken is null) return t;
            if (broken.NotBefore is { } b && t.TimeOfDay < b) t = t.Date + b;
            else t = t.Date.AddDays(1) + (rules.Select(r => r.NotBefore).Where(x => x is not null).Max() ?? DefaultMorning);
        }
        return t;
    }

    /// <summary>Validation for a callback time: null when fine, else the
    /// violated rule and the earliest allowed alternative.</summary>
    public static (TimeRule Rule, DateTime Suggested)? Check(IReadOnlyList<TimeRule> applicable, DateTime local)
    {
        var broken = applicable.FirstOrDefault(r => !Allowed(r, local));
        return broken is null ? null : (broken, NextAllowed(applicable, local));
    }

    /// <summary>Quick-pick chips under the client's rules: a chip that
    /// breaks a rule moves to the first allowed time (key "rule", labelled
    /// by its time) — duplicates after the move fold away.</summary>
    public static List<QuickPick> Apply(IReadOnlyList<QuickPick> picks, IReadOnlyList<TimeRule> applicable)
    {
        if (applicable.Count == 0) return picks.ToList();
        var result = new List<QuickPick>(picks.Count);
        foreach (var p in picks)
        {
            var moved = p.Enabled && Check(applicable, p.DueLocal) is not null
                ? new QuickPick("rule", NextAllowed(applicable, p.DueLocal).ToString("ddd HH:mm", CultureInfo.InvariantCulture), NextAllowed(applicable, p.DueLocal))
                : p;
            if (result.Any(r => r.Enabled && moved.Enabled && r.DueLocal == moved.DueLocal)) continue;
            result.Add(moved);
        }
        return result;
    }
}
