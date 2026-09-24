using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Palon.Agent;
using Palon.Memory;
using Palon.Notes;

namespace Palon.Agentic;

/// <summary>One searchable call record — from the notes index or the daily Markdown archive.</summary>
sealed record SearchableNote(string Id, DateTime StartedLocal, string? Number, string Summary, string Transcript, int DurationSec, bool Archived);

/// <summary>A hit: the note, a relevance score and the best matching snippet.</summary>
sealed record NoteHit(SearchableNote Note, double Score, string Snippet);

/// <summary>
/// Better find-in-notes: Hebrew-aware token scoring (prefix letters, final
/// letters), summary weighted over transcript, a recency tie-break, a snippet
/// around the best match, a client filter by phone, and — beyond the 50-note
/// index — the append-only daily Markdown archive. Pure except <see cref="LoadAll"/>.
/// </summary>
static class NoteSearch
{
    public const int ArchiveDays = 120;

    public static List<NoteHit> Find(IEnumerable<SearchableNote> notes, string? query, string? phone, DateTime nowLocal, int limit = 5, int? sinceDays = null)
    {
        var q = (query ?? "").Trim();
        var qTokens = HebrewText.Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 2 && !Filler.Contains(t)).ToList();
        IEnumerable<SearchableNote> pool = notes;
        if (!string.IsNullOrWhiteSpace(phone)) pool = pool.Where(n => PhoneMatch.Same(n.Number, phone));
        if (sinceDays is int d) pool = pool.Where(n => n.StartedLocal >= nowLocal.Date.AddDays(-d));

        var hits = new List<NoteHit>();
        foreach (var n in pool)
        {
            double score;
            string snippet;
            if (qTokens.Count == 0)
            {
                score = 1;
                snippet = FirstLine(n.Summary.Length > 0 ? n.Summary : n.Transcript);
            }
            else
            {
                var qs = string.Join(' ', qTokens);
                var sumScore = HebrewText.Score(qs, n.Summary);
                var trScore = HebrewText.Score(qs, n.Transcript);
                if (sumScore == 0 && trScore == 0) continue;
                var coverage = (double)Math.Max(sumScore, trScore) / qTokens.Count;
                if (qTokens.Count >= 3 && coverage < 0.5) continue; // one common word out of many isn't a match
                score = sumScore * 2.0 + trScore + coverage * 3;
                if (n.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) || n.Transcript.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 4;
                snippet = Snippet(sumScore >= trScore && sumScore > 0 ? n.Summary : n.Transcript, qTokens);
            }
            var ageDays = Math.Max(0, (nowLocal - n.StartedLocal).TotalDays);
            score += 1.0 / (1 + ageDays / 7); // recent first on ties
            hits.Add(new NoteHit(n, score, snippet));
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Note.StartedLocal).Take(Math.Clamp(limit, 1, 20)).ToList();
    }

    static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "על", "את", "של", "מה", "עם", "לגבי", "זה", "the", "about", "of", "and", "what", "did", "say",
    };

    /// <summary>~160 chars around the densest match.</summary>
    internal static string Snippet(string text, IReadOnlyList<string> qTokens)
    {
        var flat = text.Replace('\n', ' ');
        var best = -1;
        foreach (var t in qTokens)
        {
            var i = IndexOfToken(flat, t);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        if (best < 0) return FirstLine(flat);
        var start = Math.Max(0, best - 60);
        var end = Math.Min(flat.Length, best + 100);
        // Snap to word boundaries.
        while (start > 0 && !char.IsWhiteSpace(flat[start - 1])) start--;
        while (end < flat.Length && !char.IsWhiteSpace(flat[end])) end++;
        return (start > 0 ? "…" : "") + flat[start..end].Trim() + (end < flat.Length ? "…" : "");
    }

    static int IndexOfToken(string text, string normalizedToken)
    {
        // Normalization folds final letters; search both forms cheaply.
        var i = text.IndexOf(normalizedToken, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return i;
        var words = text.Split(' ');
        var pos = 0;
        foreach (var w in words)
        {
            if (HebrewText.Tokens(w).Any(t => t.StartsWith(normalizedToken, StringComparison.Ordinal))) return pos;
            pos += w.Length + 1;
        }
        return -1;
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public static SearchableNote From(CallNote n) =>
        new(n.Id, n.StartedUtc.ToLocalTime(), n.Number, n.Summary ?? "", n.Transcript, n.DurationSec, false);

    /// <summary>The index notes plus older archive entries (not already in the index).</summary>
    public static List<SearchableNote> LoadAll(IReadOnlyList<CallNote> index, DateTime nowLocal)
    {
        var list = index.Select(From).ToList();
        try
        {
            if (!Directory.Exists(NotesStore.NotesDir)) return list;
            var oldest = index.Count > 0 ? index.Min(n => n.StartedUtc.ToLocalTime()) : DateTime.MaxValue;
            foreach (var file in Directory.EnumerateFiles(NotesStore.NotesDir, "????-??-??.md"))
            {
                if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                if (day < nowLocal.Date.AddDays(-ArchiveDays) || day > oldest.Date) continue;
                foreach (var entry in ParseDaily(File.ReadAllText(file), day))
                    if (entry.StartedLocal < oldest.AddMinutes(-1)) list.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Agentic: note archive read failed: {ex.Message}");
        }
        return list;
    }

    static readonly Regex Header = new(@"^## (?<h>\d{2}):(?<m>\d{2}) · (?<min>\d+) min(?: · (?<num>[^·\n]+?))?(?: · recovered)?\s*$", RegexOptions.Multiline);

    /// <summary>Splits one daily Markdown file (NotesStore.AppendDaily format) into entries.</summary>
    internal static List<SearchableNote> ParseDaily(string markdown, DateTime day)
    {
        var result = new List<SearchableNote>();
        var matches = Header.Matches(markdown.Replace("\r\n", "\n"));
        var text = markdown.Replace("\r\n", "\n");
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var bodyStart = m.Index + m.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = text[bodyStart..bodyEnd].Replace("\n---", "").Trim();
            var cut = body.IndexOf("Transcript:", StringComparison.Ordinal);
            var summary = cut >= 0 ? body[..cut].Trim() : "";
            var transcript = cut >= 0 ? body[(cut + "Transcript:".Length)..].Trim() : body;
            var at = day.Date.AddHours(int.Parse(m.Groups["h"].Value)).AddMinutes(int.Parse(m.Groups["m"].Value));
            var num = m.Groups["num"].Success ? m.Groups["num"].Value.Trim() : null;
            result.Add(new SearchableNote($"md:{day:yyyyMMdd}:{m.Groups["h"].Value}{m.Groups["m"].Value}:{i}", at, num, summary, transcript,
                int.Parse(m.Groups["min"].Value) * 60, true));
        }
        return result;
    }

    public static string Format(IReadOnlyList<NoteHit> hits, WorkSnapshot s)
    {
        if (hits.Count == 0) return "לא מצאתי בהערות.";
        return string.Join("\n", hits.Select(h =>
            $"• {Terminal.He.Ago(h.Note.StartedLocal, s.Now)} · {s.Who(h.Note.Number)}: {h.Snippet}"));
    }
}
