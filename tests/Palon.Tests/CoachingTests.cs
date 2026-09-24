using Palon.Coaching;
using Palon.Sales;
using Xunit;

namespace Palon.Tests;

public class CoachingVadTests
{
    const int Sr = Vad.SampleRate;

    static float[] Noise(double seconds, double amp, int seed = 1)
    {
        var rng = new Random(seed);
        var x = new float[(int)(seconds * Sr)];
        for (var i = 0; i < x.Length; i++) x[i] = (float)((rng.NextDouble() * 2 - 1) * amp);
        return x;
    }

    /// <summary>Voice-like: 140 Hz fundamental + decaying harmonics, 4 Hz syllable envelope.</summary>
    static void AddVoice(float[] x, double from, double to, double amp)
    {
        for (var i = (int)(from * Sr); i < Math.Min(x.Length, (int)(to * Sr)); i++)
        {
            var t = (double)i / Sr;
            double v = 0;
            for (var h = 1; h <= 20; h++) v += Math.Sin(2 * Math.PI * 140 * h * t) / h;
            var env = 0.6 + 0.4 * Math.Abs(Math.Sin(Math.PI * 4 * t));
            x[i] += (float)(amp * env * v * 0.4);
        }
    }

    [Fact]
    public void Silence_has_no_speech() => Assert.Empty(Vad.Detect(new float[Sr * 5]));

    [Fact]
    public void Steady_white_noise_is_not_speech() => Assert.Empty(Vad.Detect(Noise(6, 0.05)));

    [Fact]
    public void Loud_white_noise_burst_is_not_speech()
    {
        var x = Noise(6, 0.002);
        var burst = Noise(1, 0.3, 7);
        Array.Copy(burst, 0, x, 2 * Sr, burst.Length);
        Assert.Empty(Vad.Detect(x));
    }

    [Fact]
    public void Voice_bursts_over_noise_are_found_at_the_right_times()
    {
        var x = Noise(10, 0.003);
        AddVoice(x, 1.0, 3.0, 0.2);
        AddVoice(x, 6.0, 7.5, 0.2);
        var segs = Vad.Detect(x);
        Assert.Equal(2, segs.Count);
        Assert.InRange(segs[0].Start, 0.85, 1.15);
        Assert.InRange(segs[0].End, 2.9, 3.5);   // hangover ≈ 0.3 s
        Assert.InRange(segs[1].Start, 5.85, 6.15);
        Assert.InRange(segs[1].End, 7.4, 8.0);
    }

    [Fact]
    public void Adaptive_floor_follows_a_louder_room()
    {
        var x = Noise(14, 0.001);
        var loud = Noise(10, 0.02, 3);
        Array.Copy(loud, 0, x, 4 * Sr, loud.Length); // room gets 26 dB noisier
        AddVoice(x, 10, 12, 0.3);
        var segs = Vad.Detect(x);
        var seg = Assert.Single(segs);
        Assert.InRange(seg.Start, 9.8, 10.2);
    }

    [Fact]
    public void Short_pauses_do_not_split_a_turn()
    {
        var x = Noise(6, 0.002);
        AddVoice(x, 1, 2, 0.2);
        AddVoice(x, 2.15, 3, 0.2);
        Assert.Single(Vad.Detect(x));
    }

    [Fact]
    public void Streaming_matches_one_shot()
    {
        var x = Noise(8, 0.003);
        AddVoice(x, 2, 4, 0.2);
        var vad = new Vad();
        for (var i = 0; i < x.Length; i += 777) vad.Push(x.AsSpan(i, Math.Min(777, x.Length - i)));
        Assert.Equal(Vad.Detect(x), vad.Finish());
    }
}

public class CoachingMetricsTests
{
    static Segment S(double a, double b) => new(a, b);

    [Fact]
    public void Talk_ratio_monologue_interruptions_latency()
    {
        var user = new List<Segment> { S(0, 10), S(10.5, 20), S(22, 25), S(40, 45) };
        var client = new List<Segment> { S(25.5, 38), S(43, 50) };
        var m = CallMetricsCalc.Compute(user, client, "שלום מה שלומך? טוב תודה.", 60);
        Assert.Equal(27.5, m.UserTalkSec);
        Assert.Equal(19.5, m.ClientTalkSec);
        Assert.Equal(Math.Round(100 * 27.5 / 47, 1), m.TalkPct);
        Assert.Equal(25, m.LongestMonologueSec);   // 0–25 across short pauses, no client speech
        Assert.Equal(0, m.Interruptions);          // 40 starts after client ended at 38
        Assert.Equal(2, m.AvgResponseSec);         // client ends 38 → user 40; 50 has no reply
    }

    [Fact]
    public void Interruption_counts_user_onsets_over_client_speech()
    {
        var user = new List<Segment> { S(5, 8), S(20, 21) };
        var client = new List<Segment> { S(0, 10), S(19.95, 22) };
        var m = CallMetricsCalc.Compute(user, client, "", 30);
        Assert.Equal(1, m.Interruptions); // the second overlap started together, not an interruption
    }

    [Fact]
    public void Client_backchannel_does_not_break_a_monologue_but_a_turn_does()
    {
        var user = new List<Segment> { S(0, 30), S(30.6, 60), S(64, 70) };
        var client = new List<Segment> { S(30.1, 30.5), S(60.5, 63.5) };
        var m = CallMetricsCalc.Compute(user, client, "", 70);
        Assert.Equal(60, m.LongestMonologueSec);
    }

    [Fact]
    public void Missing_channel_leaves_talk_fields_null()
    {
        var m = CallMetricsCalc.Compute(null, new List<Segment> { S(0, 5) }, "אחת שתיים שלוש ארבע", 120);
        Assert.Null(m.TalkPct);
        Assert.Null(m.LongestMonologueSec);
        Assert.Equal(2, m.WordsPerMin);
        Assert.Equal(4, m.Words);
    }

    [Theory]
    [InlineData("מה שלומך? אני בסדר.", 1)]
    [InlineData("איך אתה משקיע היום. כמה כסף יש לך בצד", 2)]
    [InlineData("ולמה לא עכשיו. בסדר גמור.", 1)]
    [InlineData("רציתי לשאול האם יש לך חשבון מסחר.", 1)]
    [InlineData("שלום, מדבר דני מהחברה. היה נעים.", 0)]
    [InlineData("What do you trade? OK.", 1)]
    public void Questions_from_marks_and_hebrew_words(string text, int expected) =>
        Assert.Equal(expected, CallMetricsCalc.CountQuestions(text));

    [Fact]
    public void Wpm_uses_voiced_minutes()
    {
        var words = string.Join(' ', Enumerable.Repeat("מילה", 120));
        var m = CallMetricsCalc.Compute(new List<Segment> { S(0, 30) }, new List<Segment> { S(30, 60) }, words, 300);
        Assert.Equal(120, m.WordsPerMin);
    }
}

public class CoachingQuoteTests
{
    const string Transcript = "שלום, מדבר דני. \"יש לי כבר ברוקר\" – אני עובד עם אינטראקטיב. מה העמלות אצלכם? " +
                              "אני צריך לדבר עם אשתי. טוב, אז נדבר מחר ב־11.";

    [Fact]
    public void Normalize_strips_hebrew_punctuation_niqqud_and_spaces()
    {
        Assert.Equal("יש לי כבר ברוקר", CoachExtract.Normalize("  יֵשׁ   לִי \"כבר\" ברוקר!!"));
        Assert.Equal("ב 11", CoachExtract.Normalize("ב־11"));
        Assert.Equal("צהל", CoachExtract.Normalize("צה״ל"));
    }

    [Fact]
    public void Items_with_unfound_quotes_are_dropped()
    {
        var json = "COACH: {\"objections\":[{\"category\":\"already_has_broker\",\"quote\":\"יש לי כבר ברוקר\"}," +
                   "{\"category\":\"spouse\",\"quote\":\"צריך לדבר עם אשתי\"},{\"category\":\"fees\",\"quote\":\"יקר לי מדי\"}]," +
                   "\"client_questions\":[{\"quote\":\"מה העמלות אצלכם?\"},{\"quote\":\"איפה אתם יושבים?\"}]," +
                   "\"next_step\":{\"agreed\":true,\"text\":\"שיחה מחר 11\",\"quote\":\"נדבר מחר ב-11\"}}";
        var (data, error) = CoachExtract.Parse(json, Transcript);
        Assert.Null(error);
        Assert.NotNull(data);
        Assert.Equal(new[] { Objections.HasBroker, Objections.Spouse }, data!.Objections.Select(o => o.Category));
        Assert.Single(data.ClientQuestions);
        Assert.True(data.NextStep!.Agreed);   // hyphen vs maqaf normalized
        Assert.Equal(2, data.Dropped);
    }

    [Theory]
    [InlineData("COACH: not json")]
    [InlineData("COACH: {\"objections\":[{\"category\":\"weather\",\"quote\":\"שלום\"}]}")]
    [InlineData("COACH: {\"objections\":{}}")]
    [InlineData("COACH: {\"next_step\":{\"agreed\":\"yes\"}}")]
    [InlineData("COACH: {\"objections\":[{\"category\":\"fees\"}]}")]
    public void Invalid_shapes_return_an_error_for_the_retry(string line)
    {
        var (data, error) = CoachExtract.Parse(line, Transcript);
        Assert.Null(data);
        Assert.NotNull(error);
    }

    [Fact]
    public void Split_removes_the_coach_line_and_keeps_the_note()
    {
        var reply = "סחר בעבר - אחזור אליו מחר\nCALLBACK: {\"when_iso\":\"2026-09-25T11:00\"}\nCOACH: {\"objections\":[]}";
        var (text, line) = CoachExtract.Split(reply);
        Assert.StartsWith("COACH:", line);
        Assert.DoesNotContain("COACH", text);
        Assert.Contains("CALLBACK", text);
    }
}

public class CoachingReportTests
{
    static readonly DateTime Sunday = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Local);

    static CoachRecord Rec(string id, DateTime local, double talk, double mono, int questions, bool next,
        string[]? phrases = null, string? number = null, params string[] objections) =>
        new(id, local.ToUniversalTime(), 300, number,
            new CallMetrics(100, 100, talk, mono, 1, 1.2, questions, 150, 700),
            new CoachData(objections.Select(o => new CoachItem("q", o)).ToList(), new(), new CoachNextStep(next, null, null)),
            (phrases ?? Array.Empty<string>()).ToList(), null);

    [Fact]
    public void Week_starts_on_sunday() =>
        Assert.Equal(Sunday, CoachReports.WeekStart(new DateTime(2026, 9, 24, 15, 0, 0)));

    [Fact]
    public void Aggregates_trend_objections_and_best_call()
    {
        var calls = new List<CoachRecord>
        {
            Rec("a", Sunday.AddDays(1).AddHours(10), 40, 50, 6, true, objections: new[] { Objections.Fees, Objections.Fees }),
            Rec("b", Sunday.AddDays(2).AddHours(10), 70, 200, 1, false, objections: Objections.Spouse),
            Rec("old", Sunday.AddDays(-3), 80, 120, 0, false),
            Rec("future", Sunday.AddDays(8), 50, 50, 5, true),
        };
        var r = CoachReports.Build(calls, new List<Deal>(), Sunday);
        Assert.Equal(2, r.This.Calls);
        Assert.Equal(55, r.This.TalkPct);
        Assert.Equal(125, r.This.LongestMonologueSec);
        Assert.Equal(3.5, r.This.QuestionsPerCall);
        Assert.Equal(50, r.This.NextStepRate);
        Assert.Equal(1, r.Previous.Calls);
        Assert.Equal(80, r.Previous.TalkPct);
        Assert.Equal((Objections.Fees, 2), r.Objections[0]);
        Assert.Equal((Objections.Spouse, 1), r.Objections[1]);
        Assert.Equal("a", r.Best!.Call.Id);
        Assert.True(r.Best.Score > 90);
        Assert.NotEmpty(CoachTips.For(r));
        Assert.True(CoachTips.For(r).Count <= 3);
    }

    [Fact]
    public void User_questions_subtract_client_questions()
    {
        var rec = Rec("x", Sunday, 40, 30, 5, true) with
        {
            Coach = new CoachData(new(), new() { new CoachItem("מה?"), new CoachItem("איך?") }, null),
        };
        Assert.Equal(3, CoachReports.UserQuestions(rec));
    }

    [Fact]
    public void Log_odds_ranks_deposit_phrases_and_needs_eight_calls()
    {
        var calls = new List<(List<string>, bool)>();
        for (var i = 0; i < 12; i++) calls.Add((new() { "חשבון דמו", "שלום", i < 10 ? "הפקדה ראשונה" : "x" }, true));
        for (var i = 0; i < 30; i++) calls.Add((new() { "שלום", "אין זמן", i < 2 ? "הפקדה ראשונה" : "y", i < 5 ? "rare" : "z" }, false));
        var (dep, other) = CoachReports.LogOdds(calls);
        Assert.Equal("חשבון דמו", dep[0].Phrase);          // 12/12 vs 0/30
        Assert.Contains(dep, p => p.Phrase == "הפקדה ראשונה");
        Assert.Equal("אין זמן", other[0].Phrase);
        Assert.DoesNotContain(dep.Concat(other), p => p.Phrase == "rare" || p.Phrase == "x"); // < 8 calls
        var hello = dep.Concat(other).FirstOrDefault(p => p.Phrase == "שלום");
        Assert.True(hello is null || Math.Abs(hello.Z) < 1);
    }

    [Fact]
    public void Log_odds_empty_when_one_side_missing() =>
        Assert.Empty(CoachReports.LogOdds(new List<(List<string>, bool)> { (new() { "a" }, true) }).Deposit);

    [Fact]
    public void Deals_link_by_phone_in_note_or_full_name_within_30_days()
    {
        var call = Rec("c", Sunday.AddDays(1), 40, 30, 3, true, phrases: new[] { "יוסי", "יוסי כהן", "כהן" }, number: "+972 50-123-4567");
        var byPhone = new Deal { ClientName = "Someone", Date = Sunday.AddDays(5), Note = "tel 050-1234567" };
        var byName = new Deal { ClientName = "יוסי כהן", Date = Sunday.AddDays(3) };
        var tooLate = new Deal { ClientName = "יוסי כהן", Date = Sunday.AddDays(60) };
        var otherName = new Deal { ClientName = "דנה לוי", Date = Sunday.AddDays(2) };
        Assert.Same(byPhone, CoachReports.LinkDeal(call, new List<Deal> { byPhone, otherName }));
        Assert.Same(byName, CoachReports.LinkDeal(call, new List<Deal> { byName }));
        Assert.Null(CoachReports.LinkDeal(call, new List<Deal> { tooLate, otherName }));
    }

    [Fact]
    public void Phrases_skip_stop_words_and_keep_bigrams()
    {
        var p = CoachReports.Phrases("אני רוצה לפתוח חשבון, זה בסדר?");
        Assert.Contains("לפתוח חשבון", p);
        Assert.Contains("רוצה", p);
        Assert.DoesNotContain("אני", p);
        Assert.DoesNotContain("בסדר", p);
    }
}
