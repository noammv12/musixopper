using Palon.Agentic;
using Palon.Coaching;
using Palon.Memory;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;
using Xunit;

namespace Palon.Tests;

public class CommandParserTests
{
    // Tuesday 22 Sep 2026, 10:00 local.
    static readonly DateTime Now = new(2026, 9, 22, 10, 0, 0, DateTimeKind.Local);

    static readonly ParseContext Ctx = new(Now,
        new[] { "דני כהן", "שרית", "מתן", "Maya Levi", "לאה" },
        new[] { ("c1", "סיילספורס שלי"), ("c2", "Gmail") });

    static ParsedCommand? P(string text) => CommandParser.Parse(text, Ctx);

    [Theory]
    [InlineData("דני מחר ב-11", "דני כהן", 23, 11, 0)]
    [InlineData("דני כהן מחר ב-11:30", "דני כהן", 23, 11, 30)]
    [InlineData("תזכיר לי לחזור לשרית מחר בשלוש", "שרית", 23, 15, 0)]
    [InlineData("חזרה למתן ביום ראשון ב-12", "מתן", 27, 12, 0)]
    [InlineData("call Maya Levi tomorrow at 3pm", "Maya Levi", 23, 15, 0)]
    [InlineData("remind me to call Maya tomorrow at 9am", "Maya Levi", 23, 9, 0)]
    [InlineData("לחזור לדני היום ב-16", "דני כהן", 22, 16, 0)]
    [InlineData("יוסי מחר ב-10", "יוסי", 23, 10, 0)]
    [InlineData("לאה מחר ב-14", "לאה", 23, 14, 0)]
    public void Callback_name_and_time(string text, string name, int day, int hour, int minute)
    {
        var p = P(text);
        Assert.NotNull(p);
        Assert.Equal(CommandKind.Callback, p!.Kind);
        Assert.Equal(name, p.Name);
        Assert.Equal(new DateTime(2026, 9, day, hour, minute, 0), p.WhenLocal!.Value);
    }

    [Fact]
    public void Callback_keeps_the_note_after_על()
    {
        var p = P("שרית מחר ב-11 על החוזה");
        Assert.Equal(CommandKind.Callback, p!.Kind);
        Assert.Equal("שרית", p.Name);
        Assert.Equal("החוזה", p.Text);
    }

    [Fact]
    public void Callback_relative_time()
    {
        var p = P("תזכיר לי בעוד שעה לחזור לדני");
        Assert.Equal(CommandKind.Callback, p!.Kind);
        Assert.Equal("דני כהן", p.Name);
        Assert.Equal(Now.AddHours(1), p.WhenLocal);
    }

    [Fact]
    public void Callback_day_only_is_marked_not_explicit()
    {
        var p = P("מתן מחר");
        Assert.Equal(CommandKind.Callback, p!.Kind);
        Assert.False(p.TimeExplicit);
        Assert.Equal(new DateTime(2026, 9, 23, 10, 0, 0), p.WhenLocal);
    }

    [Fact]
    public void Reminder_without_person_needs_a_verb()
    {
        var p = P("תזכיר לי מחר ב-9 לשלוח חוזה");
        Assert.Equal(CommandKind.Callback, p!.Kind);
        Assert.Null(p.Name);
        Assert.Contains("לשלוח חוזה", p.Text);
    }

    [Theory]
    [InlineData("מחר ב-11")]
    [InlineData("מה קורה מחר ב-11?")]
    [InlineData("כמה הפקדות היו השבוע")]
    [InlineData("למה דני לא ענה")]
    [InlineData("hello there")]
    [InlineData("")]
    [InlineData("x")]
    public void Unclear_text_goes_to_the_agent(string text) => Assert.Null(P(text));

    [Theory]
    [InlineData("מתן הפקיד 3000 PPC", "מתן", 3000, "PPC")]
    [InlineData("מתן הפקיד 3,000 דולר", "מתן", 3000, null)]
    [InlineData("שרית הפקידה 5k affiliate", "שרית", 5000, "Affiliate")]
    [InlineData("הפקדה של יוסי 10000 חבר מביא חבר", "יוסי", 10000, "Referral")]
    [InlineData("Maya deposited $2500 organic", "Maya Levi", 2500, "Organic")]
    [InlineData("רון הפקיד 4 אלף", "רון", 4000, null)]
    public void Deal(string text, string name, int amount, string? source)
    {
        var p = P(text);
        Assert.NotNull(p);
        Assert.Equal(CommandKind.Deal, p!.Kind);
        Assert.Equal(name, p.Name);
        Assert.Equal(amount, p.Amount);
        Assert.Equal(source, p.Source?.ToString());
        Assert.Equal(Now.Date, p.Date);
    }

    [Fact]
    public void Deal_region_and_yesterday()
    {
        var p = P("מתן הפקיד אתמול 3000 ישראל PPC");
        Assert.Equal(DealRegion.Israel, p!.Region);
        Assert.Equal(Now.Date.AddDays(-1), p.Date);
    }

    [Fact]
    public void Deal_without_amount_is_not_a_deal() => Assert.NotEqual(CommandKind.Deal, P("מתן הפקיד")?.Kind);

    [Theory]
    [InlineData("תבנית הפקדה לדני", "הפקדה", "דני כהן")]
    [InlineData("שלח לשרית תבנית פתיחה", "פתיחה", "שרית")]
    [InlineData("תבנית שאלון ליוסי", "שאלון", "יוסי")]
    [InlineData("template deposit for Maya", "deposit", "Maya Levi")]
    public void Template(string text, string query, string name)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Template, p!.Kind);
        Assert.Equal(query, p.TemplateQuery);
        Assert.Equal(name, p.Name);
    }

    [Theory]
    [InlineData("תזכור שדני אוהב שיחות קצרות", "דני אוהב שיחות קצרות")]
    [InlineData("תזכור ששרית מעדיפה וואטסאפ", "שרית מעדיפה וואטסאפ")]
    [InlineData("תזכור שלא להתקשר לדני לפני 12", "לא להתקשר לדני לפני 12")]
    [InlineData("remember that my manager is Avi", "my manager is Avi")]
    [InlineData("זכור: יום חמישי קצר", "יום חמישי קצר")]
    public void Remember(string text, string expected)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Remember, p!.Kind);
        Assert.Equal(expected, p.Text);
    }

    [Fact]
    public void Remember_keeps_a_known_name_starting_with_shin()
    {
        var p = P("תזכור שרית גרושה");
        Assert.Equal("שרית גרושה", p!.Text);
    }

    [Fact]
    public void Remember_is_not_a_callback_reminder() => Assert.Equal(CommandKind.Callback, P("תזכיר לי לחזור לדני מחר")!.Kind);

    [Fact]
    public void Client_note()
    {
        var p = P("הערה לדני: מעדיף אחרי 17:00");
        Assert.Equal(CommandKind.ClientNote, p!.Kind);
        Assert.Equal("דני כהן", p.Name);
        Assert.Equal("מעדיף אחרי 17:00", p.Text);
    }

    [Theory]
    [InlineData("פתח חודש", "month")]
    [InlineData("פתח את החודש", "month")]
    [InlineData("תפתח חזרות", "callbacks")]
    [InlineData("open templates", "templates")]
    [InlineData("פתח לקוחות", "clients")]
    [InlineData("פתח זיכרון", "memory")]
    [InlineData("פתח Gmail", "cmd:c2")]
    [InlineData("פתח שיחות", "calls")]
    [InlineData("פתח הגדרות", "settings")]
    [InlineData("פתח את דני", "client")]
    public void Open(string text, string target)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Open, p!.Kind);
        Assert.Equal(target, p.Target);
    }

    [Fact]
    public void Open_unknown_thing_goes_to_the_agent() => Assert.Null(P("פתח את הקובץ של הדוח השנתי"));

    [Theory]
    [InlineData("קרא מסך", false)]
    [InlineData("מה כתוב במסך", false)]
    [InlineData("what's on my screen", false)]
    [InlineData("קרא את הקבלה", true)]
    [InlineData("read receipt", true)]
    public void Screen(string text, bool receipt)
    {
        var p = P(text);
        Assert.Equal(CommandKind.ReadScreen, p!.Kind);
        Assert.Equal(receipt, p.Receipt);
    }

    [Theory]
    [InlineData("תעד בסיילספורס", null)]
    [InlineData("תעד את דני בסיילספורס", "דני כהן")]
    [InlineData("log to salesforce", null)]
    [InlineData("סיילספורס", null)]
    public void Salesforce(string text, string? name)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Salesforce, p!.Kind);
        Assert.Equal(name, p.Name);
    }

    [Theory]
    [InlineData("נסח הודעה לשרית", "שרית", null)]
    [InlineData("תנסח פולואפ לדני על המינימום", "דני כהן", "המינימום")]
    [InlineData("draft a follow-up to Maya", "Maya Levi", null)]
    [InlineData("נסח הודעה ליוסי", "יוסי", null)]
    [InlineData("follow up Maya", "Maya Levi", null)]
    public void Draft(string text, string name, string? instruction)
    {
        var p = P(text.Replace("draft a follow-up", "draft follow-up"));
        Assert.Equal(CommandKind.DraftFollowUp, p!.Kind);
        Assert.Equal(name, p.Name);
        Assert.Equal(instruction, p.Text);
    }

    [Theory]
    [InlineData("מה אמר דני על המחיר", "דני כהן", "המחיר")]
    [InlineData("מה שרית אמרה על הבעל", "שרית", "הבעל")]
    [InlineData("what did Maya say about fees", "Maya Levi", "fees")]
    [InlineData("חפש עמלות", null, "עמלות")]
    [InlineData("מי שאל על מינימום הפקדה", null, "מינימום הפקדה")]
    public void Search(string text, string? name, string query)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Search, p!.Kind);
        Assert.Equal(name, p.Name);
        Assert.Equal(query, p.Text);
    }

    [Theory]
    [InlineData("סכם את דני")]
    [InlineData("מי זה מתן")]
    [InlineData("tell me about Maya")]
    public void Summarize(string text) => Assert.Equal(CommandKind.SummarizeClient, P(text)!.Kind);

    [Fact]
    public void Summarize_unknown_client_goes_to_the_agent() => Assert.NotEqual(CommandKind.SummarizeClient, P("סכם את המייל הזה")?.Kind);

    [Theory]
    [InlineData("מה עכשיו", "NextSteps")]
    [InlineData("מה התוכנית להיום", "NextSteps")]
    [InlineData("plan my day", "NextSteps")]
    [InlineData("בריף", "Brief")]
    [InlineData("סיכום יום", "Recap")]
    [InlineData("סכם את היום", "Recap")]
    public void Rituals(string text, string kind) => Assert.Equal(kind, P(text)!.Kind.ToString());

    [Theory]
    [InlineData("פוקוס", 60)]
    [InlineData("פוקוס שעתיים", 120)]
    [InlineData("פוקוס 30 דקות", 30)]
    [InlineData("focus 45m", 45)]
    [InlineData("מצב פוקוס לחצי שעה", 30)]
    public void Focus_duration(string text, int minutes)
    {
        var p = P(text);
        Assert.Equal(CommandKind.Focus, p!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(minutes), p.Duration);
    }

    [Fact]
    public void Focus_off() => Assert.True(P("בטל פוקוס")!.Off);

    [Fact]
    public void Known_name_matching_handles_prefixes()
    {
        Assert.Equal("דני כהן", CommandParser.FindKnownName("לדני", Ctx.ClientNames));
        Assert.Equal("שרית", CommandParser.FindKnownName("ושרית אמרה", Ctx.ClientNames));
        Assert.Null(CommandParser.FindKnownName("מישהו אחר", Ctx.ClientNames));
    }
}

public class CommandSuggestTests
{
    static readonly DateTime Now = new(2026, 9, 22, 10, 0, 0);

    static SuggestContext Ctx() => new(Now, new[]
    {
        new SuggestClient("שרית", Now.AddDays(-1)),
        new SuggestClient("שמעון", Now.AddDays(-10)),
        new SuggestClient("דני כהן", Now.AddHours(-2), DueToday: true),
        new SuggestClient("050-1234567", Now),
    }, new[] { "הפקדה", "פתיחה פרו" });

    [Fact]
    public void Completes_a_half_typed_name_after_a_verb()
    {
        var s = CommandSuggest.Rank("נסח הודעה לש", Ctx());
        Assert.Equal("נסח הודעה לשרית", s[0]); // more recent than שמעון
        Assert.Contains("נסח הודעה לשמעון", s);
    }

    [Fact]
    public void Straight_prefix_beats_partial_matches()
    {
        var s = CommandSuggest.Rank("פתח ח", Ctx());
        Assert.Contains("פתח חודש", s);
        Assert.Contains("פתח חזרות", s);
    }

    [Fact]
    public void Client_due_today_ranks_first()
    {
        var s = CommandSuggest.Rank("סכם", Ctx());
        Assert.Equal("סכם את דני כהן", s[0]);
    }

    [Fact]
    public void Phone_only_clients_are_not_suggested()
    {
        var s = CommandSuggest.Rank("נסח", Ctx());
        Assert.DoesNotContain(s, x => x.Contains("050"));
    }

    [Fact]
    public void Empty_bar_offers_contextual_starters()
    {
        var s = CommandSuggest.Rank("", Ctx());
        Assert.Equal("מה עכשיו", s[0]);
        Assert.Contains("בריף", s);
        Assert.Contains("סכם את דני כהן", s);
        Assert.True(s.Count <= CommandSuggest.Max);
    }

    [Fact]
    public void Templates_are_offered()
    {
        var s = CommandSuggest.Rank("תבנית פ", Ctx());
        Assert.Contains("תבנית פתיחה פרו ל", s);
    }

    [Fact]
    public void English_prefix_gets_english_phrases()
    {
        var s = CommandSuggest.Rank("open", Ctx());
        Assert.Contains("open month", s);
    }

    [Fact]
    public void Never_echoes_exactly_what_was_typed() =>
        Assert.DoesNotContain("פתח חודש", CommandSuggest.Rank("פתח חודש", Ctx()));

    [Fact]
    public void Score_orders_prefix_over_word_prefix_over_contains()
    {
        var prefix = CommandSuggest.Score("נסח", "נסח הודעה לשרית");
        var words = CommandSuggest.Score("הודעה שרית", "נסח הודעה לשרית");
        var none = CommandSuggest.Score("xyz", "נסח הודעה לשרית");
        Assert.True(prefix > words);
        Assert.True(words > 0);
        Assert.Equal(0, none);
    }
}

public class NudgeTests
{
    // Tuesday 22 Sep 2026.
    static DateTime At(int h, int m = 0) => new(2026, 9, 22, h, m, 0, DateTimeKind.Local);

    static CallNote Note(string id, DateTime startedLocal, string number, string summary, int sec = 400, CoachData? coach = null) =>
        new(id, startedLocal.ToUniversalTime(), sec, summary, summary, "ok", number, Coach: coach);

    static Callback Cb(string id, DateTime dueLocal, string name, string phone, string note = "") =>
        new(id, dueLocal.ToUniversalTime(), note, Name: name, Phone: phone, CreatedUtc: dueLocal.ToUniversalTime().AddDays(-1));

    static WorkSnapshot Snap(DateTime now, List<Callback>? cbs = null, List<CallNote>? notes = null, MonthBook? month = null, List<MessageTemplate>? templates = null) =>
        WorkSnapshot.Build(new WorkSnapshot
        {
            Now = now,
            Callbacks = cbs ?? new(),
            Notes = notes ?? new(),
            Month = month,
            Templates = templates ?? new() { new MessageTemplate { Id = "deposit-pro", Title = "הפקדה", Text = "פרטי הפקדה" } },
        });

    static GovernorInput G(DateTime now, bool onCall = false, bool focus = false, DateTime? callEnded = null,
        Dictionary<string, DateTime>? shown = null, int ambientToday = 0, DateTime? lastAmbient = null, IReadOnlyList<TimeRule>? rules = null) =>
        new(now, onCall, callEnded, focus, true, shown ?? new(), new Dictionary<string, DateTime>(), ambientToday, lastAmbient, rules ?? Array.Empty<TimeRule>());

    [Fact]
    public void Promise_keeper_fires_five_minutes_before_a_callback()
    {
        var s = Snap(At(10, 55), new() { Cb("a", At(11), "דני", "0501234567", "החוזה") });
        var c = NudgeRules.Evaluate(s, new NudgeExtras()).Single(x => x.Key.StartsWith("promise:"));
        Assert.True(c.UserReminder);
        Assert.Contains("בעוד 5 דק׳", c.Text);
        Assert.Contains("דני", c.Text);
    }

    [Fact]
    public void Promise_keeper_is_silent_earlier()
    {
        var s = Snap(At(10, 40), new() { Cb("a", At(11), "דני", "0501234567") });
        Assert.DoesNotContain(NudgeRules.Evaluate(s, new NudgeExtras()), x => x.Key.StartsWith("promise:"));
    }

    [Fact]
    public void Nothing_is_raised_during_a_call()
    {
        var s = Snap(At(10, 55), new() { Cb("a", At(11), "דני", "0501234567") });
        var picked = NudgeGovernor.Select(NudgeRules.Evaluate(s, new NudgeExtras()), G(At(10, 55), onCall: true));
        Assert.Empty(picked);
    }

    [Fact]
    public void Reminders_pass_focus_but_ambient_does_not()
    {
        var cands = new[]
        {
            new NudgeCandidate("promise:x", NudgeKind.Reminder, 100, "r", UserReminder: true),
            new NudgeCandidate("tip:x", NudgeKind.Insight, 10, "t"),
        };
        var picked = NudgeGovernor.Select(cands, G(At(11), focus: true));
        Assert.Equal(new[] { "promise:x" }, picked.Select(p => p.Key));
    }

    [Fact]
    public void Dedup_by_key()
    {
        var cands = new[] { new NudgeCandidate("tip:1", NudgeKind.Insight, 10, "t") };
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11), shown: new() { ["tip:1"] = DateTime.UtcNow })));
        Assert.Single(NudgeGovernor.Select(cands, G(At(11))));
    }

    [Fact]
    public void Rate_limited_to_one_ambient_per_gap()
    {
        var cands = new[]
        {
            new NudgeCandidate("a", NudgeKind.Insight, 50, "a"),
            new NudgeCandidate("b", NudgeKind.Suggestion, 40, "b"),
        };
        var first = NudgeGovernor.Select(cands, G(At(11)));
        Assert.Equal(new[] { "a" }, first.Select(p => p.Key));
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11, 5), lastAmbient: At(11))));
        Assert.Single(NudgeGovernor.Select(cands, G(At(11, 25), lastAmbient: At(11))));
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11, 25), ambientToday: NudgeGovernor.MaxAmbientPerDay)));
    }

    [Fact]
    public void Celebrations_skip_the_gap()
    {
        var cands = new[] { new NudgeCandidate("deal:1", NudgeKind.Celebration, 90, "!") };
        Assert.Single(NudgeGovernor.Select(cands, G(At(11, 5), lastAmbient: At(11))));
    }

    [Fact]
    public void Quiet_minute_after_a_call_and_outside_work_hours()
    {
        var cands = new[] { new NudgeCandidate("a", NudgeKind.Insight, 50, "a") };
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11), callEnded: At(11).AddSeconds(-30))));
        Assert.Single(NudgeGovernor.Select(cands, G(At(11), callEnded: At(11).AddMinutes(-3))));
        Assert.Empty(NudgeGovernor.Select(cands, G(At(21))));
        Assert.Empty(NudgeGovernor.Select(cands, G(new DateTime(2026, 9, 25, 11, 0, 0)))); // Friday
    }

    [Fact]
    public void Contact_suggestions_respect_time_rules()
    {
        var cands = new[] { new NudgeCandidate("revive:x", NudgeKind.Suggestion, 50, "a", ClientName: "דני", Contact: true) };
        var rules = new[] { new TimeRule("דני", NotBefore: new TimeSpan(12, 0, 0)) };
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11), rules: rules)));
        Assert.Single(NudgeGovernor.Select(cands, G(At(13), rules: rules)));
    }

    [Fact]
    public void Expired_candidates_are_dropped()
    {
        var cands = new[] { new NudgeCandidate("a", NudgeKind.Insight, 50, "a", ExpiresLocal: At(10)) };
        Assert.Empty(NudgeGovernor.Select(cands, G(At(11))));
    }

    [Fact]
    public void Buying_signal_from_the_latest_note_offers_the_deposit_template()
    {
        var s = Snap(At(11), notes: new() { Note("n1", At(10, 30), "0501234567", "הלקוח שאל מה המינימום להפקדה ומתי אפשר להתחיל") });
        var c = NudgeRules.Evaluate(s, new NudgeExtras()).Single(x => x.Key == "signal:n1");
        Assert.Equal(NudgeActionKind.CopyTemplate, c.Action!.Kind);
        Assert.Equal("deposit-pro", c.Action.Arg);
    }

    [Fact]
    public void Silent_warm_lead_is_revived_with_a_draft_when_ready()
    {
        var cbDone = Cb("c", At(10).AddDays(-5), "שרית", "0527654321") with { Status = CallbackStatus.Done, CompletedUtc = At(10).AddDays(-5).ToUniversalTime() };
        var notes = new List<CallNote>
        {
            Note("n1", At(10).AddDays(-5), "0527654321", "שאלה כמה צריך להפקיד. רוצה לחשוב.", 500,
                new CoachData(new(), new(), new CoachNextStep(true, "לחזור אחרי שתדבר עם הבעל", null))),
        };
        var s = Snap(At(11), new() { cbDone }, notes);
        var lead = Leads.Silent(s).Single();
        Assert.Equal("שרית", lead.Client.Name);
        Assert.Equal(5, lead.DaysQuiet);

        var withDraft = NudgeRules.Evaluate(s, new NudgeExtras(DraftsReady: new HashSet<string> { lead.Client.Key }))
            .Single(x => x.Key.StartsWith("revive:"));
        Assert.Contains("ניסחתי", withDraft.Text);
        Assert.Equal(NudgeActionKind.DraftFollowUp, withDraft.Action!.Kind);
        Assert.True(withDraft.Contact);
    }

    [Fact]
    public void Lead_with_a_booked_callback_is_not_silent()
    {
        var notes = new List<CallNote> { Note("n1", At(10).AddDays(-5), "0527654321", "שאלה כמה צריך להפקיד", 500) };
        var s = Snap(At(11), new() { Cb("c", At(15).AddDays(1), "שרית", "0527654321") }, notes);
        Assert.Empty(Leads.Silent(s));
    }

    [Fact]
    public void Pace_nudge_at_midday_when_behind()
    {
        var month = new MonthBook { Year = 2026, Month = 9, Target = 60 };
        for (var i = 0; i < 20; i++) month.Deals.Add(new Deal { ClientName = "x" + i, Date = new DateTime(2026, 9, 1 + i % 20), Amount = 3000 });
        var s = Snap(At(13), month: month);
        var c = NudgeRules.Evaluate(s, new NudgeExtras()).SingleOrDefault(x => x.Key.StartsWith("pace:"));
        Assert.NotNull(c);
        Assert.Contains("לקצב של היום", c!.Text);
        Assert.Equal(NudgeActionKind.NextSteps, c.Action!.Kind);
    }

    [Fact]
    public void Deposit_today_is_celebrated_imports_are_not()
    {
        var month = new MonthBook { Year = 2026, Month = 9, Target = 1 };
        month.Deals.Add(new Deal { Id = "d1", ClientName = "מתן", Date = At(9).Date, Amount = 3000, Source = DealSource.PPC });
        month.Deals.Add(new Deal { Id = "d2", ClientName = "רון", Date = At(9).Date, Amount = 3000, CreatedFrom = DealOrigin.Import });
        var c = NudgeRules.Evaluate(Snap(At(11), month: month), new NudgeExtras());
        Assert.Contains(c, x => x.Key == "deal:d1" && x.Kind == NudgeKind.Celebration && x.Text.Contains("₪600"));
        Assert.DoesNotContain(c, x => x.Key == "deal:d2");
        Assert.Contains(c, x => x.Key == "target-hit:2026-09");
    }

    [Fact]
    public void Month_start_target_reminder()
    {
        var s = Snap(new DateTime(2026, 10, 1, 10, 0, 0)); // Thursday 1 Oct, no book
        Assert.Contains(NudgeRules.Evaluate(s, new NudgeExtras()), x => x.Key.StartsWith("target:2026-10"));
    }

    [Fact]
    public void Tip_template_and_salesforce_nudges()
    {
        var rehearsal = new Palon.Salesforce.RehearsalStatus(At(10).AddHours(-12), new[] { new Palon.Salesforce.RehearsalSkillStatus("LogCall", false, false, "Save") });
        var c = NudgeRules.Evaluate(Snap(At(11)), new NudgeExtras(2, "k", rehearsal, new[] { "tip A", "tip B" }));
        Assert.Contains(c, x => x.Key == "tpl:k" && x.Text.Contains("2"));
        Assert.Contains(c, x => x.Key.StartsWith("sf:"));
        Assert.Contains(c, x => x.Key == "tip:20260922");
    }
}

public class AgenticInsightTests
{
    static DateTime At(int h, int m = 0) => new(2026, 9, 22, h, m, 0, DateTimeKind.Local);

    static WorkSnapshot Snap(List<Callback>? cbs = null, List<CallNote>? notes = null, MonthBook? month = null, List<CallRecord>? calls = null) =>
        WorkSnapshot.Build(new WorkSnapshot { Now = At(11), Callbacks = cbs ?? new(), Notes = notes ?? new(), Month = month, Calls = calls ?? new() });

    [Fact]
    public void Buying_signals_are_detected_in_hebrew_and_english()
    {
        CallNote N(string t) => new("n", DateTime.UtcNow, 60, t, t, "ok");
        Assert.Equal("minimum", BuyingSignals.Detect(N("שאל מה ההפקדה המינימלית")).First().Kind);
        Assert.Equal("how-to-deposit", BuyingSignals.Detect(N("איך מעבירים את הכסף? מה פרטי ההעברה")).First().Kind);
        Assert.Equal("minimum", BuyingSignals.Detect(N("what is the minimum deposit?")).First().Kind);
        Assert.Empty(BuyingSignals.Detect(N("לא מעוניין, תודה")));
    }

    [Fact]
    public void Next_steps_come_from_promises_and_proposals_not_a_call_list()
    {
        var cbs = new List<Callback>
        {
            new("a", At(15).ToUniversalTime(), "החוזה", Name: "דני", Phone: "0501111111"),
            new("b", At(9).ToUniversalTime(), "", Name: "רון", Phone: "0502222222"),
            new("c", At(10).AddDays(3).ToUniversalTime(), "", Name: "גל", Phone: "0503333333"),
        };
        var notes = new List<CallNote>
        {
            new("n1", At(10).ToUniversalTime(), 300, "סיכום", "t", "ok", "0504444444",
                ProposedCallback: new CallbackProposal(At(10).AddDays(1).ToUniversalTime(), "מחר בעשר")),
        };
        var plan = NextSteps.Plan(Snap(cbs, notes));
        Assert.Equal(PlanKind.Overdue, plan[0].Kind);
        Assert.Equal("רון", plan[0].Who);
        Assert.Contains(plan, p => p.Kind == PlanKind.Promise && p.Who == "דני" && p.Why == "החוזה");
        Assert.Contains(plan, p => p.Kind == PlanKind.ProposedCallback);
        Assert.DoesNotContain(plan, p => p.Who == "גל"); // not today
    }

    [Fact]
    public void Pace_goal_counts_today()
    {
        var month = new MonthBook { Year = 2026, Month = 9, Target = 44 };
        var s = Snap(month: month);
        var st = s.Stats!;
        var goal = Pace.TodayGoal(st, 0)!.Value;
        Assert.True(goal >= 2);
        Assert.Equal(0, Pace.DayFraction(At(8)));
        Assert.Equal(1, Pace.DayFraction(At(20)));
    }

    [Fact]
    public void Brief_and_recap_render()
    {
        var cbs = new List<Callback> { new("a", At(15).ToUniversalTime(), "החוזה", Name: "דני", Phone: "0501111111") };
        var month = new MonthBook { Year = 2026, Month = 9, Target = 40 };
        month.Deals.Add(new Deal { ClientName = "מתן", Date = At(9).Date, Amount = 3000, Source = DealSource.PPC });
        var s = Snap(cbs, month: month, calls: new() { new CallRecord(At(10).ToUniversalTime(), 600) });
        var brief = Rituals.Brief(s, "איתי", "טיפ");
        Assert.Contains("איתי", brief.Greeting);
        Assert.Single(brief.Promises);
        Assert.NotNull(brief.Pace);
        Assert.Contains("טיפ", brief.ToText(s.Now));

        var recap = Rituals.Recap(s);
        Assert.Equal(1, recap.Calls);
        Assert.Equal(1, recap.Deals);
        Assert.Equal(600, recap.BonusIls);
        Assert.Contains("הפקדה אחת", recap.Headline);
    }

    [Fact]
    public void Client_summary_suggests_deposit_details_after_a_signal()
    {
        var notes = new List<CallNote> { new("n1", At(9).ToUniversalTime(), 300, "שאל כמה צריך להפקיד", "t", "ok", "0501111111") };
        var cbs = new List<Callback> { new("a", At(9).AddDays(-2).ToUniversalTime(), "x", CallbackStatus.Done, Name: "דני", Phone: "0501111111") };
        var s = Snap(cbs, notes);
        var card = s.FindClient("דני")!;
        var summary = ClientSummaries.Build(s, card);
        Assert.Contains("פרטי ההפקדה", summary.Suggestion);
        Assert.Contains("דני", summary.ToText());
    }

    [Fact]
    public void Deal_from_note_reads_the_amount_and_source()
    {
        var note = new CallNote("n", DateTime.UtcNow, 300, "הלקוח הגיע מגוגל. הוא הפקיד 3,000 דולר היום.", "", "ok");
        var d = DealFromNote.Extract(note, "מתן");
        Assert.Equal(3000, d.Amount);
        Assert.Equal(DealSource.PPC, d.Source);
        Assert.True(d.Complete);
        Assert.Null(DealFromNote.Extract(new CallNote("m", DateTime.UtcNow, 60, "דיברנו 20 דקות על הפקדה", "", "ok"), "x").Amount);
    }

    [Fact]
    public void Note_search_ranks_by_tokens_and_snippets()
    {
        var now = At(11);
        var notes = new List<SearchableNote>
        {
            new("a", now.AddDays(-1), "0501111111", "דיברנו על עמלות והמינימום", "", 60, false),
            new("b", now.AddDays(-2), "0522222222", "שאל על המחיר", "הוא שאל כמה העמלות עולות לו בחודש", 60, false),
            new("c", now.AddDays(-3), "0503333333", "שיחה קצרה", "", 60, false),
        };
        var hits = NoteSearch.Find(notes, "עמלות", null, now);
        Assert.Equal(new[] { "a", "b" }, hits.Select(h => h.Note.Id));
        Assert.Contains("עמלות", hits[1].Snippet);
        Assert.Equal(new[] { "b" }, NoteSearch.Find(notes, "עמלות", "052-2222222", now).Select(h => h.Note.Id));
    }

    [Fact]
    public void Daily_markdown_archive_parses()
    {
        var md = "## 10:05 · 4 min · 050-1234567\nשאל על עמלות\n\nTranscript:\nבלה בלה\n\n---\n## 11:30 · 2 min\nTranscript:\nרק תמלול\n\n---\n";
        var entries = NoteSearch.ParseDaily(md, new DateTime(2026, 5, 1));
        Assert.Equal(2, entries.Count);
        Assert.Equal(new DateTime(2026, 5, 1, 10, 5, 0), entries[0].StartedLocal);
        Assert.Equal("050-1234567", entries[0].Number);
        Assert.Equal("שאל על עמלות", entries[0].Summary);
        Assert.Equal("רק תמלול", entries[1].Transcript);
    }

    [Fact]
    public void Template_matching_by_title_and_alias()
    {
        var templates = TemplateSeeds.Create();
        Assert.Equal("deposit-pro", AgenticRouter.MatchTemplate(templates, "הפקדה")?.Id);
        Assert.Equal("questionnaire", AgenticRouter.MatchTemplate(templates, "שאלון")?.Id);
        Assert.Null(AgenticRouter.MatchTemplate(templates, "זברה"));
    }
}
