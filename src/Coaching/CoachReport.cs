using Palon.Agent;
using Palon.Sales;

namespace Palon.Coaching;

/// <summary>
/// What coaching keeps per call, beyond the 50-note window: the local
/// numbers, the verified AI items, the call's distinct phrases (unigrams +
/// bigrams, for the deposit correlation) and the short note. No audio,
/// no transcript.
/// </summary>
sealed record CoachRecord(
    string Id,
    DateTime StartedUtc,
    int DurationSec,
    string? Number,
    CallMetrics? Metrics,
    CoachData? Coach,
    List<string> Phrases,
    string? Summary);

sealed record Kpis(
    int Calls,
    double? TalkPct,
    double? LongestMonologueSec,
    double? QuestionsPerCall,
    double? AvgResponseSec,
    double? InterruptionsPerCall,
    double? WordsPerMin,
    double? NextStepRate,  // 0..100 of calls with a verified next step
    double? AvgScore);

sealed record PhraseStat(string Phrase, double Z, int DepositCalls, int OtherCalls);

sealed record ScoredCall(CoachRecord Call, double Score, Deal? Deal);

sealed record WeekReport(
    DateTime WeekStart,
    Kpis This,
    Kpis Previous,
    List<(string Category, int Count)> Objections,
    List<PhraseStat> DepositPhrases,
    List<PhraseStat> OtherPhrases,
    int PhraseWindowCalls,
    ScoredCall? Best,
    List<ScoredCall> Calls);

/// <summary>Pure weekly coaching math: aggregation, trend, objections,
/// deposit-linked phrase log-odds and the best call. Everything takes its
/// inputs — no stores, no clock.</summary>
static class CoachReports
{
    public const int PhraseWindowWeeks = 8;
    public const int MinPhraseCalls = 8;
    public const double ReferenceTalkPct = 43; // Gong's (unverified) reference — shown as a reference only

    /// <summary>Weeks start on Sunday (the Israeli work week).</summary>
    public static DateTime WeekStart(DateTime local) => local.Date.AddDays(-(int)local.DayOfWeek);

    public static WeekReport Build(IReadOnlyList<CoachRecord> all, IReadOnlyList<Deal> deals, DateTime weekStart)
    {
        weekStart = weekStart.Date;
        List<CoachRecord> In(DateTime from, DateTime to) =>
            all.Where(r => r.StartedUtc.ToLocalTime() >= from && r.StartedUtc.ToLocalTime() < to).ToList();

        var week = In(weekStart, weekStart.AddDays(7));
        var prev = In(weekStart.AddDays(-7), weekStart);
        var scored = week.Select(r => new ScoredCall(r, Score(r), LinkDeal(r, deals)))
            .OrderByDescending(s => s.Call.StartedUtc).ToList();

        var objections = Objections.All
            .Select(cat => (cat, week.Sum(r => r.Coach?.Objections.Count(o => o.Category == cat) ?? 0)))
            .Where(x => x.Item2 > 0).OrderByDescending(x => x.Item2).ToList();

        var window = In(weekStart.AddDays(-7 * (PhraseWindowWeeks - 1)), weekStart.AddDays(7));
        var labeled = window.Select(r => (r.Phrases, LinkDeal(r, deals) is not null)).ToList();
        var (dep, other) = LogOdds(labeled);

        var best = scored.Where(s => s.Call.Metrics is not null || s.Call.Coach is not null)
            .OrderByDescending(s => s.Score).FirstOrDefault();

        return new WeekReport(weekStart, Aggregate(week), Aggregate(prev), objections, dep, other, window.Count, best, scored);
    }

    public static Kpis Aggregate(IReadOnlyList<CoachRecord> calls)
    {
        double? Avg(IEnumerable<double?> xs)
        {
            var v = xs.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            return v.Count > 0 ? v.Average() : null;
        }
        var withCoach = calls.Where(c => c.Coach is not null).ToList();
        return new Kpis(
            calls.Count,
            Avg(calls.Select(c => c.Metrics?.TalkPct)),
            Avg(calls.Select(c => c.Metrics?.LongestMonologueSec)),
            Avg(calls.Select(c => UserQuestions(c) is int q ? q : (double?)null)),
            Avg(calls.Select(c => c.Metrics?.AvgResponseSec)),
            Avg(calls.Select(c => c.Metrics?.Interruptions is int i ? i : (double?)null)),
            Avg(calls.Select(c => c.Metrics?.WordsPerMin)),
            withCoach.Count > 0 ? 100.0 * withCoach.Count(c => c.Coach!.NextStep?.Agreed == true) / withCoach.Count : null,
            calls.Count > 0 ? calls.Average(Score) : null);
    }

    /// <summary>Questions the user asked: transcript questions minus the
    /// ones the AI attributed (with a verified quote) to the client.</summary>
    public static int? UserQuestions(CoachRecord r) =>
        r.Metrics is { } m ? Math.Max(0, m.Questions - (r.Coach?.ClientQuestions.Count ?? 0)) : null;

    /// <summary>
    /// Call score 0–100 from what's known: talk share near the reference,
    /// short monologues, questions asked, few interruptions, a next step.
    /// Missing parts are left out and the rest re-weighted.
    /// </summary>
    public static double Score(CoachRecord r)
    {
        double got = 0, weight = 0;
        void Part(double w, double? v)
        {
            if (v is not { } x) return;
            got += w * Math.Clamp(x, 0, 1);
            weight += w;
        }
        var m = r.Metrics;
        Part(25, m?.TalkPct is { } t ? 1 - Math.Abs(t - ReferenceTalkPct) / 40 : null);
        Part(20, m?.LongestMonologueSec is { } l ? l <= 60 ? 1 : 1 - (l - 60) / 120 : null);
        Part(20, UserQuestions(r) is int q ? q / 6.0 : null);
        Part(15, m?.Interruptions is int i ? 1 - i / 5.0 : null);
        Part(20, r.Coach is { } c ? c.NextStep?.Agreed == true ? 1 : 0 : null);
        return weight > 0 ? Math.Round(100 * got / weight, 1) : 0;
    }

    /// <summary>The deposit this call led to: same phone (in the deal's
    /// note) or the client's full name heard on the call, deposited from
    /// the day before the call up to 30 days after.</summary>
    public static Deal? LinkDeal(CoachRecord r, IReadOnlyList<Deal> deals)
    {
        var day = r.StartedUtc.ToLocalTime().Date;
        HashSet<string>? phrases = null;
        foreach (var d in deals.OrderBy(d => d.Date))
        {
            if (d.Date.Date < day.AddDays(-1) || d.Date.Date > day.AddDays(30)) continue;
            if (r.Number is { } n && d.Note is { } note && PhoneRuns(note).Any(p => PhoneMatch.Same(p, n))) return d;
            var name = CoachExtract.Normalize(d.ClientName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (name.Length == 0 || name.Sum(x => x.Length) < 4) continue;
            phrases ??= r.Phrases.ToHashSet();
            var hit = name.Length == 1 ? name[0].Length >= 3 && phrases.Contains(name[0])
                : Enumerable.Range(0, name.Length - 1).All(i => phrases.Contains(name[i] + " " + name[i + 1]));
            if (hit || (r.Summary is { } s && name.Length > 1 && CoachExtract.Normalize(s).Contains(string.Join(' ', name)))) return d;
        }
        return null;
    }

    static IEnumerable<string> PhoneRuns(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\+?[\d][\d\- ]{6,}\d").Select(m => m.Value);

    /// <summary>
    /// Informative-Dirichlet log-odds (Monroe, Colaresi &amp; Quinn 2008)
    /// over per-call phrase presence, deposit calls vs the rest. The prior
    /// is the pooled frequency scaled to α0 = 100; z = δ / σ. Phrases seen
    /// in fewer than 8 calls are ignored. Returns the top positive (deposit)
    /// and top negative phrases.
    /// </summary>
    public static (List<PhraseStat> Deposit, List<PhraseStat> Other) LogOdds(
        IReadOnlyList<(List<string> Phrases, bool Deposit)> calls, int top = 6, double alpha0 = 100)
    {
        var dep = new Dictionary<string, int>();
        var oth = new Dictionary<string, int>();
        foreach (var (phrases, deposit) in calls)
        {
            var target = deposit ? dep : oth;
            foreach (var p in phrases.Distinct()) target[p] = target.GetValueOrDefault(p) + 1;
        }
        if (dep.Count == 0 || oth.Count == 0) return (new(), new());
        double nD = dep.Values.Sum(), nO = oth.Values.Sum(), n = nD + nO;
        var stats = new List<PhraseStat>();
        foreach (var w in dep.Keys.Union(oth.Keys))
        {
            var yD = dep.GetValueOrDefault(w);
            var yO = oth.GetValueOrDefault(w);
            if (yD + yO < MinPhraseCalls) continue;
            var a = alpha0 * (yD + yO) / n;
            var delta = Math.Log((yD + a) / (nD + alpha0 - yD - a)) - Math.Log((yO + a) / (nO + alpha0 - yO - a));
            var z = delta / Math.Sqrt(1 / (yD + a) + 1 / (yO + a));
            stats.Add(new PhraseStat(w, Math.Round(z, 2), yD, yO));
        }
        return (stats.Where(s => s.Z > 0).OrderByDescending(s => s.Z).Take(top).ToList(),
                stats.Where(s => s.Z < 0).OrderBy(s => s.Z).Take(top).ToList());
    }

    static readonly HashSet<string> Stop = new()
    {
        "של", "את", "זה", "זאת", "זו", "לא", "כן", "אני", "אתה", "את", "הוא", "היא", "אנחנו", "אתם", "הם", "על", "עם", "יש", "אין",
        "גם", "או", "אבל", "אז", "רק", "כל", "עוד", "אם", "כי", "שלי", "שלך", "לי", "לך", "לו", "לה", "לנו", "היה", "הייתה", "יהיה",
        "אה", "אהה", "אממ", "טוב", "בסדר", "אוקיי", "אוקי", "נו", "פה", "שם", "כאן", "מה", "איך", "ש", "ו", "ה", "ב", "ל", "מ",
        "the", "a", "an", "and", "or", "to", "of", "is", "it", "i", "you", "yes", "no", "ok", "okay", "so", "that", "this", "in", "on",
    };

    /// <summary>Distinct unigrams + bigrams (stop words and 1-letter tokens
    /// skipped), capped per call. Only these are kept — not the transcript.</summary>
    public static List<string> Phrases(string transcript, int cap = 600)
    {
        var tokens = CoachExtract.Normalize(transcript).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>();
        for (var i = 0; i < tokens.Length && set.Count < cap; i++)
        {
            var t = tokens[i];
            var ok = t.Length >= 2 && !Stop.Contains(t) && !t.All(char.IsDigit);
            if (ok) set.Add(t);
            if (ok && i + 1 < tokens.Length && tokens[i + 1].Length >= 2 && !Stop.Contains(tokens[i + 1]) && !tokens[i + 1].All(char.IsDigit))
                set.Add(t + " " + tokens[i + 1]);
        }
        return set.ToList();
    }
}

/// <summary>Palon's 2–3 coaching lines for the week, in his voice (butler,
/// first person, male forms). Local templates — nothing is sent anywhere.</summary>
static class CoachTips
{
    public static List<string> For(WeekReport r)
    {
        var tips = new List<(int Weight, string Text)>();
        var k = r.This;
        if (k.Calls == 0)
            return new() { "השבוע עוד לא שמעתי שיחות. כשתתחיל לדבר — אני אקשיב, ואביא מספרים." };

        if (k.TalkPct is { } t)
        {
            if (t > 60) tips.Add((90, $"דיברת {t:0}% מהזמן. הרשה לי להציע: שאלה אחת, ואז שקט — תן ללקוח למלא אותו."));
            else if (t < 30) tips.Add((60, $"דיברת רק {t:0}% מהזמן. הקשבה זו מעלה, אבל מישהו צריך להוביל לסגירה — ואני ממליץ שזה יהיה אתה."));
            else tips.Add((20, $"יחס הדיבור שלך מאוזן, {t:0}%. זה בדיוק המקום."));
        }
        if (k.LongestMonologueSec is { } m && m > 90)
            tips.Add((80, $"המונולוג הארוך הממוצע נמשך {Math.Round(m)} שניות. אחרי דקה, עצור ושאל \"זה מסתדר לך?\" — זה עושה פלאים."));
        if (k.QuestionsPerCall is { } q && q < 4)
            tips.Add((70, $"בממוצע {q:0.#} שאלות לשיחה. שאלה על הניסיון שלו, ושאלה על המטרה — ושיחה נפתחת."));
        if (k.NextStepRate is { } ns && ns < 50)
            tips.Add((75, $"רק ב־{ns:0}% מהשיחות נקבע צעד הבא. אל תנתק בלי יום ושעה — אני כבר ארשום את החזרה."));
        if (k.InterruptionsPerCall is { } i && i >= 2)
            tips.Add((50, $"נכנסת לדברי הלקוח כ־{i:0.#} פעמים בשיחה. שנייה של סבלנות — והוא יגיד לך בדיוק מה הוא צריך."));
        if (r.Objections.FirstOrDefault() is { Count: >= 2 } top)
            tips.Add((55, $"ההתנגדות הנפוצה השבוע: \"{Objections.Label(top.Category)}\" ({top.Count} פעמים). כדאי שתהיה לך תשובה מוכנה עוד לפני שהיא עולה."));
        if (r.DepositPhrases.FirstOrDefault() is { } ph)
            tips.Add((40, $"בשיחות שהסתיימו בהפקדה חוזר הביטוי \"{ph.Phrase}\". אולי שווה להכניס אותו לשגרה."));
        if (k.AvgScore is { } s && r.Previous.AvgScore is { } ps && s - ps >= 5)
            tips.Add((45, $"הציון הממוצע עלה ב־{s - ps:0} נקודות משבוע שעבר. יפה מאוד — אני רושם לעצמי."));

        return tips.OrderByDescending(x => x.Weight).Take(3).Select(x => x.Text).ToList();
    }
}
