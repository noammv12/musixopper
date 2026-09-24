using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Salesforce;

/// <summary>One user action Palon saw in teach mode.</summary>
sealed record TeachObservation(string Kind, ElementFacts Facts, string? Value, string Url); // Kind: click | fill

/// <summary>What a teach session changed, for the UI.</summary>
sealed record TeachOutcome(SfSkill Skill, IReadOnlyList<string> Learned, IReadOnlyList<string> Unmatched);

/// <summary>
/// Pure: folds what the user did into a skill. Fills are mapped by the
/// sentinel text the user was asked to type (language-independent), by the
/// value's shape (digits → the search term, a date → the date and the org's
/// date format) or by field api-name; clicks align in order with the skill's
/// click steps, preferring a step whose existing locators already match.
/// Learned locators go first; old ones stay as fallbacks.
/// </summary>
static class TeachMerge
{
    public const string SubjectSentinel = "PALON-SUBJECT";
    public const string CommentsSentinel = "PALON-COMMENTS";

    public static TeachOutcome Merge(SfSkill skill, IReadOnlyList<TeachObservation> observations, DateOnly? today = null)
    {
        var learned = new List<string>();
        var unmatched = new List<string>();
        var filledKeys = observations.Where(o => o.Kind == "fill").Select(o => Key(o.Facts)).ToHashSet();
        var pointer = 0;
        foreach (var o in observations)
        {
            var locs = LocatorRanking.DeriveAll(o.Facts).ToList();
            if (locs.Count == 0) { unmatched.Add($"{o.Kind} on an element with no stable name"); continue; }
            if (o.Kind == "click" && filledKeys.Contains(Key(o.Facts))) continue; // focusing a field it then filled
            if (o.Kind == "click" && o.Facts.Role == "option") continue;           // picklist choice: a value, not a step

            int idx;
            if (o.Kind == "fill")
            {
                idx = FillStep(skill, o, today);
                if (idx < 0) { unmatched.Add($"typed into {locs[0]}"); continue; }
            }
            else
            {
                idx = ClickStep(skill, o, pointer);
                if (idx < 0) { unmatched.Add($"clicked {locs[0]}"); continue; }
            }
            var step = skill.Steps[idx];
            foreach (var l in Enumerable.Reverse(locs)) step.Locators = LocatorRanking.Promote(step.Locators, l);
            learned.Add($"{step.Intent} ← {locs[0]}");
            pointer = Math.Max(pointer, idx + 1);
        }
        if (learned.Count > 0)
        {
            skill.Version++;
            skill.TaughtAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }
        return new TeachOutcome(skill, learned, unmatched);
    }

    static string Key(ElementFacts f) => $"{f.Role}|{f.Name}|{f.FieldApiName}|{f.AriaLabel}";

    static int FillStep(SfSkill skill, TeachObservation o, DateOnly? today)
    {
        var value = (o.Value ?? "").Trim();
        string? variable =
            value.Contains(SubjectSentinel, StringComparison.OrdinalIgnoreCase) ? "%subject%"
            : value.Contains(CommentsSentinel, StringComparison.OrdinalIgnoreCase) ? "%comments%"
            : Regex.IsMatch(value, @"^\+?[\d\s\-]{7,}$") ? "%term%"
            : null;
        if (variable is null && SfVars.InferDateFormat(value) is not null)
        {
            variable = "%date%";
            // Learn the date format; prefer an exact match against a plausible date near today.
            var fmt = today is { } t
                ? Enumerable.Range(-400, 800).Select(d => SfVars.InferDateFormat(value, t.AddDays(d))).FirstOrDefault(f => f is not null)
                : null;
            skill.DateFormat = fmt ?? SfVars.InferDateFormat(value)!;
        }
        if (variable is not null)
        {
            var i = skill.Steps.FindIndex(s => s.Method is "fill" or "select" && s.Args.Contains(variable));
            if (i >= 0) return i;
        }
        if (o.Facts.FieldApiName is { } f)
            return skill.Steps.FindIndex(s => s.Method is "fill" or "select" && s.Locators.Any(l => l.FieldApiName == f));
        return -1;
    }

    static int ClickStep(SfSkill skill, TeachObservation o, int pointer)
    {
        var commit = SfSafety.LooksLikeCommit(o.Facts.Name);
        var candidates = Enumerable.Range(pointer, Math.Max(0, skill.Steps.Count - pointer))
            .Where(i => skill.Steps[i].Method == "click" && skill.Steps[i].Commit == commit)
            .ToList();
        var matching = candidates.FirstOrDefault(i => skill.Steps[i].Locators.Any(l => Matches(l, o.Facts)), -1);
        if (matching >= 0) return matching;
        return candidates.FirstOrDefault(i => !skill.Steps[i].Optional, -1);
    }

    static bool Matches(SfLocator l, ElementFacts f) =>
        (l.FieldApiName is not null && l.FieldApiName == f.FieldApiName)
        || (l.Role is not null && string.Equals(l.Role, f.Role, StringComparison.OrdinalIgnoreCase) && LocatorRanking.NameMatches(l, f.Name))
        || (l.AriaLabel is not null && string.Equals(l.AriaLabel, f.AriaLabel, StringComparison.OrdinalIgnoreCase));
}

