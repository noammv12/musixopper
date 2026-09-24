using System.Text.RegularExpressions;

namespace Palon.Salesforce;

/// <summary>The composer a step's locators are scoped to, or why none can be chosen.</summary>
sealed record ComposerPick(AxNode? Root, IReadOnlyList<AxNode> Scope, string? Error)
{
    public bool Refused => Error is not null;
}

/// <summary>
/// Lightning can have several composers open at once (a docked "Log a Call"
/// next to a "New Task" modal). Before Palon types or saves, every locator is
/// scoped to the one composer whose header matches the planned action and
/// that is visible and topmost; when that is ambiguous, the run refuses
/// instead of guessing.
/// </summary>
static class ComposerScope
{
    static readonly HashSet<string> ContainerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "dialog", "alertdialog", "region", "form", "complementary",
    };

    /// <summary>Region/form names that are composers (dialogs always count).</summary>
    static readonly Regex ComposerName = new(
        @"log a call|new task|new event|email|call|task|composer|publisher|תיעוד|שיחה|משימה|אירוע|דוא""?ל",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The header regex for a skill's composer, or null when the skill
    /// does not type into one (search, field edits on the record page).</summary>
    public static string? HeaderFor(string skill) => skill switch
    {
        "LogCall" => @"log a call|logged call|\bcall\b|תיעוד שיחה|שיחה",
        "NewTask" => @"new task|\btask\b|משימה",
        _ => null,
    };

    /// <summary>Open composers in document order (later = on top).</summary>
    public static List<AxNode> Candidates(IReadOnlyList<AxNode> all) => all
        .Where(n => !n.Ignored && ContainerRoles.Contains(n.Role)
                    && (n.Role is "dialog" or "alertdialog" ? n.Modal || n.Name.Length > 0 : ComposerName.IsMatch(n.Name)))
        .ToList();

    /// <summary>
    /// Picks the scope. <paramref name="visible"/> filters candidates (the
    /// caller checks boxes through CDP). Rules: no composer open → the page
    /// (AxTree.Scope); exactly one header match → it; several matches → the
    /// single modal among them (it is on top), else refuse; no match → the
    /// only open composer, else refuse.
    /// </summary>
    public static ComposerPick Pick(IReadOnlyList<AxNode> all, string? headerPattern, Func<AxNode, bool>? visible = null)
    {
        var open = Candidates(all).Where(n => visible?.Invoke(n) ?? true).ToList();
        // A composer nested in another composer (a region inside the modal) is the same composer.
        open = open.Where(n => !open.Any(o => o != n && IsAncestor(all, o, n))).ToList();
        if (open.Count == 0) return new ComposerPick(null, AxTree.Scope(all), null);

        var matching = headerPattern is null
            ? open
            : open.Where(n => Regex.IsMatch(n.Name, headerPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();

        AxNode? chosen = null;
        if (matching.Count == 1) chosen = matching[0];
        else if (matching.Count > 1)
        {
            var modal = matching.Where(n => n.Modal).ToList();
            if (modal.Count == 1) chosen = modal[0];
            else return new ComposerPick(null, Array.Empty<AxNode>(),
                $"{matching.Count} חלונות פתוחים מתאימים לפעולה ({string.Join(", ", matching.Select(m => $"\"{m.Name}\""))}) — סגור את המיותרים ונסה שוב");
        }
        else if (open.Count == 1) chosen = open[0];
        else return new ComposerPick(null, Array.Empty<AxNode>(),
            $"כמה חלונות פתוחים ואף אחד מהם לא מתאים לפעולה ({string.Join(", ", open.Select(m => $"\"{m.Name}\""))})");

        return new ComposerPick(chosen, AxTree.Subtree(all, chosen.Id), null);
    }

    static bool IsAncestor(IReadOnlyList<AxNode> all, AxNode ancestor, AxNode node)
    {
        var byId = all.ToDictionary(n => n.Id);
        var cur = node.ParentId;
        for (var guard = 0; cur is not null && guard < 500; guard++)
        {
            if (cur == ancestor.Id) return true;
            cur = byId.TryGetValue(cur, out var p) ? p.ParentId : null;
        }
        return false;
    }
}
