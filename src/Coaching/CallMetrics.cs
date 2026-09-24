using System.Text.RegularExpressions;

namespace Palon.Coaching;

/// <summary>
/// Local per-call numbers, computed from the separate mic (user) and
/// loopback (client) tracks before the audio is deleted. Only these
/// numbers are kept — never audio. Talk fields are null when one channel
/// was missing (a ratio from one side would be meaningless).
/// </summary>
sealed record CallMetrics(
    double? UserTalkSec,
    double? ClientTalkSec,
    double? TalkPct,              // user share of voiced time, 0..100
    double? LongestMonologueSec,  // user's longest uninterrupted stretch
    int? Interruptions,           // user started talking over the client
    double? AvgResponseSec,       // client stops → user starts
    int Questions,                // question sentences in the transcript (both sides)
    double? WordsPerMin,          // transcript words / voiced minutes (overall)
    int Words);

static class CallMetricsCalc
{
    const double MonologueBreakSec = 1.0;   // client speech this long ends a user monologue
    const double MonologueMaxGapSec = 3.0;  // silence this long ends one too
    const double ClientTurnMergeSec = 0.7;
    const double MaxLatencySec = 5.0;
    const double InterruptLeadSec = 0.3;    // client must already be talking this long
    const double InterruptOverlapSec = 0.3; // and keep talking over the user this long

    public static CallMetrics Compute(IReadOnlyList<Segment>? user, IReadOnlyList<Segment>? client,
        string transcript, double durationSec)
    {
        var words = CountWords(transcript);
        var questions = CountQuestions(transcript);
        if (user is null || client is null)
        {
            var wpmOnly = durationSec > 0 && words > 0 ? words / (durationSec / 60) : (double?)null;
            return new CallMetrics(null, null, null, null, null, null, questions, wpmOnly, words);
        }

        var u = Sorted(user);
        var c = Sorted(client);
        var userSec = u.Sum(s => s.Length);
        var clientSec = c.Sum(s => s.Length);
        var voiced = userSec + clientSec;
        double? talkPct = voiced > 0 ? 100 * userSec / voiced : null;
        double? wpm = words > 0 ? voiced > 5 ? words / (voiced / 60) : durationSec > 0 ? words / (durationSec / 60) : null : null;

        return new CallMetrics(
            Math.Round(userSec, 1), Math.Round(clientSec, 1),
            talkPct is { } p ? Math.Round(p, 1) : null,
            Math.Round(LongestMonologue(u, c), 1),
            Interruptions(u, c),
            AvgLatency(u, c) is { } l ? Math.Round(l, 2) : null,
            questions,
            wpm is { } w ? Math.Round(w) : null,
            words);
    }

    static List<Segment> Sorted(IReadOnlyList<Segment> s) => s.Where(x => x.Length > 0).OrderBy(x => x.Start).ToList();

    static double ClientSpeechIn(List<Segment> c, double from, double to) =>
        c.Sum(s => Math.Max(0, Math.Min(s.End, to) - Math.Max(s.Start, from)));

    /// <summary>User segments joined across short pauses and brief client
    /// back-channels ("כן", "אהה"); the longest joined span wins.</summary>
    internal static double LongestMonologue(List<Segment> u, List<Segment> c)
    {
        if (u.Count == 0) return 0;
        double best = 0, start = u[0].Start, end = u[0].End;
        for (var i = 1; i < u.Count; i++)
        {
            var gap = u[i].Start - end;
            if (gap <= MonologueMaxGapSec && ClientSpeechIn(c, end, u[i].Start) < MonologueBreakSec &&
                ClientSpeechIn(c, end, u[i].Start) + ClientSpeechIn(c, u[i].Start, u[i].End) < MonologueBreakSec * 2)
            {
                end = Math.Max(end, u[i].End);
                continue;
            }
            best = Math.Max(best, end - start);
            start = u[i].Start;
            end = u[i].End;
        }
        return Math.Max(best, end - start);
    }

    internal static int Interruptions(List<Segment> u, List<Segment> c) =>
        u.Count(us => c.Any(cs => cs.Start <= us.Start - InterruptLeadSec && cs.End >= us.Start + InterruptOverlapSec));

    /// <summary>Mean gap from a client turn's end to the user's next onset,
    /// only when the user answers within 5 s and nobody spoke in between.</summary>
    internal static double? AvgLatency(List<Segment> u, List<Segment> c)
    {
        var turns = new List<Segment>();
        foreach (var s in c)
        {
            if (turns.Count > 0 && s.Start - turns[^1].End < ClientTurnMergeSec)
                turns[^1] = new Segment(turns[^1].Start, Math.Max(turns[^1].End, s.End));
            else turns.Add(s);
        }
        var gaps = new List<double>();
        for (var i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            var next = u.FirstOrDefault(x => x.Start >= t.End);
            if (next.Length <= 0) continue;
            var lat = next.Start - t.End;
            if (lat > MaxLatencySec) continue;
            if (i + 1 < turns.Count && turns[i + 1].Start < next.Start) continue; // client spoke again first
            if (u.Any(x => x.Start < t.End && x.End > t.End)) continue;           // user was already talking
            gaps.Add(lat);
        }
        return gaps.Count > 0 ? gaps.Average() : null;
    }

    static readonly Regex WordRx = new(@"[\p{L}\p{N}]+(?:['׳""״][\p{L}]+)?", RegexOptions.Compiled);

    public static int CountWords(string text) => string.IsNullOrWhiteSpace(text) ? 0 : WordRx.Matches(text).Count;

    static readonly HashSet<string> QuestionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "מה", "למה", "מדוע", "איך", "כיצד", "מתי", "איפה", "היכן", "לאן", "מאיפה", "כמה", "האם", "מי", "איזה",
        "איזו", "אילו", "מהו", "מהי", "מהם", "ממה", "במה", "עם מי", "נכון",
        "what", "why", "how", "when", "where", "who", "which", "do", "does", "did", "is", "are", "can", "could", "would", "will",
    };

    static readonly Regex SentenceRx = new(@"[^.!?\n…]+[.!?…]*", RegexOptions.Compiled);

    /// <summary>Sentences ending in '?' or opening with a question word
    /// (Hebrew prefix letters ו/ש/ה stripped). "האם" anywhere counts too.</summary>
    static bool HasRtl(string s) => s.Any(ch => ch is >= '\u0590' and <= '\u05FF');

    public static int CountQuestions(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var n = 0;
        foreach (Match m in SentenceRx.Matches(text))
        {
            var s = m.Value.Trim();
            if (s.Length == 0) continue;
            if (s.EndsWith('?')) { n++; continue; }
            var tokens = WordRx.Matches(s).Select(x => x.Value).ToList();
            if (tokens.Count == 0) continue;
            if (tokens.Contains("האם")) { n++; continue; }
            var first = tokens[0];
            // English auxiliaries only open a question when the sentence is short-ish
            // and never on their own — keep them, but Hebrew words are the main signal.
            if (QuestionWords.Contains(first) && (HasRtl(first) || tokens.Count > 2)) { n++; continue; }
            if (first.Length > 2 && first[0] is 'ו' or 'ש' && QuestionWords.Contains(first[1..])) n++;
        }
        return n;
    }
}

