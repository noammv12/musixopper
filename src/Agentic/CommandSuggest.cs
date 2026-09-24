using Palon.Memory;

namespace Palon.Agentic;

/// <summary>A client as the suggester sees it: name, last contact, and whether something is pending.</summary>
sealed record SuggestClient(string Name, DateTime LastLocal, bool DueToday = false, bool Warm = false);

/// <summary>What completions can be built from. Clients are in any order (ranked here).</summary>
sealed record SuggestContext(
    DateTime Now,
    IReadOnlyList<SuggestClient> Clients,
    IReadOnlyList<string> TemplateTitles,
    bool FocusOn = false);

/// <summary>
/// Completions for the command bar. Pure and fast (runs on every keystroke):
/// an empty bar gets a few contextual starters; otherwise candidate phrases
/// (verbs × recent clients, templates, pages) are ranked by how well they
/// continue what was typed, and a half-typed client name is completed in place.
/// </summary>
static class CommandSuggest
{
    public const int Max = 6;

    static readonly string[] Starters =
    {
        "מה עכשיו", "בריף", "סיכום יום", "פתח חודש", "פתח חזרות", "פתח לקוחות", "פתח תבניות",
        "קרא מסך", "קרא קבלה", "תזכור ש", "פוקוס שעה", "חפש ",
    };

    static readonly string[] EnglishStarters =
    {
        "plan my day", "brief", "recap", "open month", "read screen", "remember that ", "focus 1h", "search ",
    };

    public static IReadOnlyList<string> Rank(string? typed, SuggestContext ctx)
    {
        var prefix = (typed ?? "").TrimStart();
        var clients = ctx.Clients
            .Where(c => c.Name.Length >= 2 && !c.Name.Any(char.IsDigit))
            .OrderByDescending(c => c.DueToday).ThenByDescending(c => c.Warm).ThenByDescending(c => c.LastLocal)
            .Take(30)
            .ToList();

        if (prefix.Length == 0) return Empty(ctx, clients);

        var english = prefix.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') && !prefix.Any(HebrewText.IsHebrew);
        var scored = new Dictionary<string, double>(StringComparer.Ordinal);
        void Offer(string candidate, double bonus)
        {
            if (candidate.Trim().Equals(prefix.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            var s = Score(prefix, candidate);
            if (s <= 0) return;
            s += bonus;
            if (!scored.TryGetValue(candidate, out var old) || old < s) scored[candidate] = s;
        }

        // 1. Complete a half-typed client name in place ("נסח הודעה לש" → "נסח הודעה לשרית").
        foreach (var (candidate, rank) in CompleteLastWord(prefix, clients)) Offer(candidate, 40 - rank);

        // 2. Phrases built from verbs × clients, ranked by client order.
        for (var i = 0; i < clients.Count; i++)
        {
            var name = clients[i].Name;
            var bonus = 24 - 2 * Math.Min(12, i);
            if (english)
            {
                Offer($"draft follow-up to {name}", bonus);
                Offer($"summarize {name}", bonus);
                Offer($"call {name} tomorrow at 10", bonus);
                Offer($"what did {name} say about ", bonus);
            }
            else
            {
                Offer($"נסח הודעה ל{name}", bonus);
                Offer($"סכם את {name}", bonus);
                Offer($"{name} מחר ב-10", bonus - 1);
                Offer($"מה אמר {name} על ", bonus - 1);
                Offer($"תבנית הפקדה ל{name}", bonus - 2);
                Offer($"תעד את {name} בסיילספורס", bonus - 3);
                Offer($"{name} הפקיד ", bonus - 3);
            }
        }

        // 3. Templates and fixed starters.
        foreach (var title in ctx.TemplateTitles.Where(t => t.Trim().Length > 0))
            Offer(english ? $"template {title} for " : $"תבנית {title.Trim()} ל", 4);
        foreach (var s in english ? EnglishStarters : Starters) Offer(s, 2);
        if (ctx.FocusOn) Offer(english ? "focus off" : "בטל פוקוס", 6);

        return scored.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Length).Select(p => p.Key).Take(Max).ToList();
    }

    static IReadOnlyList<string> Empty(SuggestContext ctx, List<SuggestClient> clients)
    {
        var list = new List<string> { "מה עכשיו" };
        var h = ctx.Now.Hour;
        if (h < 11) list.Add("בריף");
        if (h >= 17) list.Add("סיכום יום");
        if (clients.FirstOrDefault(c => c.DueToday) is { } due) list.Add($"סכם את {due.Name}");
        if (clients.FirstOrDefault(c => c.Warm && !c.DueToday) is { } warm) list.Add($"נסח הודעה ל{warm.Name}");
        if (clients.FirstOrDefault() is { } recent && !list.Any(x => x.EndsWith(recent.Name, StringComparison.Ordinal)))
            list.Add($"{recent.Name} מחר ב-10");
        list.Add(ctx.FocusOn ? "בטל פוקוס" : "פתח חודש");
        list.Add("תזכור ש");
        return list.Distinct().Take(Max).ToList();
    }

    /// <summary>
    /// How well <paramref name="candidate"/> continues <paramref name="typed"/>: 100 for a straight
    /// prefix, 70 when every typed word starts a candidate word in order, 40 when every typed word
    /// appears inside it; 0 = no match. Hebrew-normalized (final letters, case).
    /// </summary>
    internal static double Score(string typed, string candidate)
    {
        var t = HebrewText.Normalize(typed);
        var c = HebrewText.Normalize(candidate);
        if (t.Length == 0) return 1;
        var trailingSpace = typed.EndsWith(' ');
        if (c.StartsWith(t, StringComparison.Ordinal))
            return 100 - Math.Min(10, (c.Length - t.Length) / 6.0) + (trailingSpace && c.Length > t.Length && c[t.Length] == ' ' ? 5 : 0);
        var tw = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cw = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var j = 0;
        var inOrder = true;
        foreach (var w in tw)
        {
            while (j < cw.Length && !Starts(cw[j], w)) j++;
            if (j == cw.Length) { inOrder = false; break; }
            j++;
        }
        if (inOrder) return 70 - Math.Min(20, cw.Length - tw.Length);
        if (tw.All(w => w.Length >= 2 && c.Contains(w, StringComparison.Ordinal))) return 40;
        return 0;
    }

    static bool Starts(string word, string typed) =>
        word.StartsWith(typed, StringComparison.Ordinal)
        || (word.Length > 1 && "לבשוהמכ".Contains(word[0]) && word[1..].StartsWith(typed, StringComparison.Ordinal))
        || (typed.Length > 1 && "לבשוהמכ".Contains(typed[0]) && word.StartsWith(typed[1..], StringComparison.Ordinal) && typed[0] != word[0]);

    /// <summary>Completes the last typed word with client names (keeping a one-letter Hebrew prefix like ל).</summary>
    static IEnumerable<(string Candidate, int Rank)> CompleteLastWord(string typed, List<SuggestClient> clients)
    {
        if (typed.EndsWith(' ')) yield break;
        var cut = typed.LastIndexOf(' ');
        var head = cut < 0 ? "" : typed[..(cut + 1)];
        var last = cut < 0 ? typed : typed[(cut + 1)..];
        if (last.Length == 0) yield break;
        var forms = new List<(string Prefix, string Stem)> { ("", last) };
        if (last.Length >= 1 && "לבשוה".Contains(last[0])) forms.Add((last[..1], last[1..]));
        var rank = 0;
        foreach (var client in clients)
        {
            var n = HebrewText.Normalize(client.Name);
            foreach (var (pre, stem) in forms)
            {
                if (stem.Length == 0 && pre.Length == 0) continue;
                var s = HebrewText.Normalize(stem);
                if (!n.StartsWith(s, StringComparison.Ordinal) || n == s) continue;
                // A bare single letter only completes after a verb ("נסח הודעה ל" + "ש").
                if (s.Length == 0 && head.Length == 0) continue;
                yield return (head + pre + client.Name, rank);
                break;
            }
            rank++;
        }
    }
}
