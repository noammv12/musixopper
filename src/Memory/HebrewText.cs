using System.Security.Cryptography;
using System.Text;

namespace Palon.Memory;

/// <summary>
/// Hebrew-aware text normalization for memory dedup and keyword recall:
/// niqqud and cantillation stripped, final letters folded (ם→מ …),
/// punctuation dropped, case folded. Tokens also carry a prefix-stripped
/// form (ו/ה/ב/ל/מ/ש/כ) so "ולדני" still finds "דני".
/// </summary>
static class HebrewText
{
    const string Prefixes = "והבלמשכ";

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var space = true;
        for (var i = 0; i < text.Length; i++)
        {
            var raw = text[i];
            // Digit grouping never splits a number: "2,000" == "2000".
            if ((raw == ',' || raw == '\'') && i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
            if (raw >= '֑' && raw <= 'ׇ' && raw != '־') continue; // niqqud / te'amim (keep maqaf as a break)
            var c = raw switch
            {
                'ך' => 'כ',
                'ם' => 'מ',
                'ן' => 'נ',
                'ף' => 'פ',
                'ץ' => 'צ',
                _ => char.ToLowerInvariant(raw),
            };
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static bool IsHebrew(char c) => c >= 'א' && c <= 'ת';

    /// <summary>Normalized tokens plus their prefix-stripped forms (distinct).</summary>
    public static HashSet<string> Tokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(token);
            var stem = token;
            // At most two prefix letters ("וה", "שב"), and keep 2+ letters of stem.
            for (var i = 0; i < 2 && stem.Length > 3 && IsHebrew(stem[0]) && Prefixes.Contains(stem[0]); i++)
            {
                stem = stem[1..];
                set.Add(stem);
            }
        }
        return set;
    }

    /// <summary>Stable content hash of the normalized text (mem0-style dedup).</summary>
    public static string Hash(string? text) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Normalize(text)))).ToLowerInvariant();

    /// <summary>How many query tokens prefix-match a token of the text.</summary>
    public static int Score(string? query, string? text)
    {
        var q = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 0) return 0;
        var tokens = Tokens(text);
        var score = 0;
        foreach (var raw in q)
        {
            var forms = Tokens(raw);
            if (forms.Any(f => f.Length >= 2 && tokens.Any(t => t.StartsWith(f, StringComparison.Ordinal)))) score++;
        }
        return score;
    }

    /// <summary>Two names refer to the same person: some token matches
    /// (prefix-stripped forms included), e.g. "דני" ~ "דני כהן" ~ "לדני".</summary>
    public static bool SameName(string? a, string? b)
    {
        var ta = Tokens(a).Where(t => t.Length >= 2).ToHashSet();
        if (ta.Count == 0) return false;
        return Tokens(b).Any(t => t.Length >= 2 && ta.Contains(t));
    }
}
