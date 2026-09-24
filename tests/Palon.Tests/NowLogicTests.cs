using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;
using Xunit;

namespace Palon.Tests;

public class NowStackTests
{
    static readonly DateTime Now = new(2026, 9, 24, 15, 0, 0, DateTimeKind.Local);
    static readonly List<MessageTemplate> Templates = TemplateSeeds.Create();

    static Callback Cb(string id, DateTime dueLocal, string? name = "דני לוי", CallbackStatus status = CallbackStatus.Open, string note = "שאל על 10K") =>
        new(id, dueLocal.ToUniversalTime(), note, status, Name: name, Phone: "0505550186", CreatedUtc: Now.AddDays(-1).ToUniversalTime());

    static CallNote Note(string id, DateTime startLocal, string? summary, int seconds = 180, CallbackProposal? heard = null, string transcript = "") =>
        new(id, startLocal.ToUniversalTime(), seconds, summary, transcript, "ok", "0505550186", ProposedCallback: heard);

    [Fact]
    public void PlanCards_OnlyPreparedWork_NeverGenericCalls()
    {
        var plan = new[]
        {
            new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.Promise, Now, "דני", null, "חזרה", "", CallbackId: "c1", NoteId: "n0"),
            new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.UnbookedNextStep, null, "מאיה", null, "סוכם: לשלוח חוזה", "", NoteId: "n1"),
            new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.BuyingSignal, null, "רון", null, "שאל על מינימום", "", NoteId: "n2"),
            new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.SilentLead, null, "גל", null, "שקט 5 ימים", "", NoteId: "n3"),
            new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.SilentLead, null, "נועה", null, "שקט 6 ימים", "", NoteId: "n4"),
        };
        var tpl = Templates[0];
        var cards = NowStack.FromPlan(plan, Templates, new HashSet<string>(), Array.Empty<NowCard>(),
            i => i.NoteId == "n2" ? tpl.Id : null,
            i => i.NoteId == "n3" ? "היי גל, רציתי לבדוק" : null);
        Assert.Equal(new[] { "step:n1", "signal:n2", "draft:n3" }, cards.Select(c => c.Id));
        Assert.Equal(tpl.Id, cards[1].TemplateId);
    }

    [Fact]
    public void PlanCards_SkipHandledAndNotesAlreadyOnTheStack()
    {
        var plan = new[] { new Palon.Agentic.PlanItem(Palon.Agentic.PlanKind.UnbookedNextStep, null, "מאיה", null, "סוכם", "", NoteId: "n1") };
        var existing = new[] { new NowCard("tpl:n1", NowKind.FollowUp, "", "מאיה", "", Array.Empty<string>(), "", Array.Empty<string>(), NoteId: "n1") };
        Assert.Empty(NowStack.FromPlan(plan, Templates, new HashSet<string>(), existing, _ => null, _ => null));
        Assert.Empty(NowStack.FromPlan(plan, Templates, new HashSet<string> { "step:n1" }, Array.Empty<NowCard>(), _ => null, _ => null));
    }

    [Fact]
    public void CallsGoal_UserSettingWins()
    {
        Assert.Equal(25, DayRings.CallsGoal(Array.Empty<CallRecord>(), Now, 25));
    }

    [Fact]
    public void Stack_HoldsOnlyDueCallbacks_NotFutureOnes()
    {
        var cards = NowStack.Build(new[]
        {
            Cb("late", Now.AddHours(-2)),
            Cb("soon", Now.AddMinutes(3)),
            Cb("later", Now.AddHours(3)),
            Cb("done", Now.AddHours(-1), status: CallbackStatus.Done),
        }, Array.Empty<CallNote>(), Templates, new HashSet<string>(), Now);

        Assert.Equal(new[] { "cb:late", "cb:soon" }, cards.Select(c => c.Id));
        Assert.True(cards[0].Overdue);
        Assert.StartsWith("חזרה באיחור", cards[0].Kicker);
        Assert.False(cards[1].Overdue);
    }

    [Fact]
    public void Stack_IsEmpty_WithoutRemindersOrPreparedWork()
    {
        var cards = NowStack.Build(new[] { Cb("x", Now.AddDays(1)) }, Array.Empty<CallNote>(), Templates, new HashSet<string>(), Now);
        Assert.Empty(cards);
    }

    [Fact]
    public void Stack_AddsHeardSalesforceAndFollowUp_AndSkipsHandled()
    {
        var heard = new CallbackProposal(Now.AddDays(1).ToUniversalTime(), "תחזור אליי מחר ב-11");
        var notes = new[]
        {
            Note("n1", Now.AddHours(-1), "- רוצה לשמוע על פרטי הפקדה\n- שולח צילום", heard: heard),
        };
        var cards = NowStack.Build(Array.Empty<Callback>(), notes, Templates, new HashSet<string>(), Now);
        Assert.Equal(new[] { NowKind.Heard, NowKind.Salesforce, NowKind.FollowUp }, cards.Select(c => c.Kind));
        Assert.Equal("״תחזור אליי מחר ב-11״", cards[0].Quote);
        Assert.Equal("רוצה לשמוע על פרטי הפקדה", cards[1].Lines[0]);
        Assert.Equal("deposit-pro", cards[2].TemplateId);

        var handled = new HashSet<string> { "heard:n1", "sf:n1" };
        var left = NowStack.Build(Array.Empty<Callback>(), notes, Templates, handled, Now);
        Assert.Equal(new[] { "tpl:n1" }, left.Select(c => c.Id));
    }

    [Fact]
    public void Stack_IgnoresShortOrOldCallsForSalesforce()
    {
        var notes = new[]
        {
            Note("short", Now.AddHours(-1), "סיכום", seconds: 30),
            Note("old", Now.AddDays(-2), "סיכום"),
        };
        Assert.Empty(NowStack.Build(Array.Empty<Callback>(), notes, Templates, new HashSet<string>(), Now));
    }

    [Fact]
    public void Stack_IsCapped()
    {
        var cbs = Enumerable.Range(0, 20).Select(i => Cb("c" + i, Now.AddMinutes(-i - 10))).ToList();
        Assert.Equal(NowStack.MaxCards, NowStack.Build(cbs, Array.Empty<CallNote>(), Templates, new HashSet<string>(), Now).Count);
    }
}

public class DayRingsTests
{
    static readonly DateTime Now = new(2026, 9, 24, 15, 0, 0, DateTimeKind.Local);

    static CallRecord Call(DateTime local) => new(local.ToUniversalTime(), 60);

    [Fact]
    public void CallsGoal_Defaults_WithoutHistory()
    {
        Assert.Equal(DayRings.DefaultCallsGoal, DayRings.CallsGoal(Array.Empty<CallRecord>(), Now));
    }

    [Fact]
    public void CallsGoal_StretchesTenPercent_RoundedUpToFive()
    {
        var calls = Enumerable.Range(0, 40).Select(i => Call(Now.Date.AddDays(-1).AddHours(9).AddMinutes(i))).ToList();
        Assert.Equal(45, DayRings.CallsGoal(calls, Now)); // 40 * 1.1 = 44 → 45
    }

    [Fact]
    public void Rings_CountTodaysWork()
    {
        var calls = new[] { Call(Now.AddHours(-1)), Call(Now.AddHours(-2)), Call(Now.AddDays(-1)) };
        var cbs = new[]
        {
            new Callback("a", Now.AddHours(-3).ToUniversalTime(), "x", CallbackStatus.Done, CompletedUtc: Now.AddHours(-1).ToUniversalTime()),
            new Callback("b", Now.AddHours(-1).ToUniversalTime(), "y"),
            new Callback("c", Now.AddDays(2).ToUniversalTime(), "z"),
        };
        var book = new MonthBook { Year = 2026, Month = 9, Target = 55, Deals = { new Deal { ClientName = "Ella", Date = Now.Date, Amount = 10000 } } };
        var rings = DayRings.Build(calls, cbs, book, null, Now);

        Assert.Equal(2, rings[0].Value);
        Assert.Equal(1, rings[1].Value);
        Assert.Equal(2, rings[1].Goal);
        Assert.Equal("1 באיחור", rings[1].Hint);
        Assert.Equal(1, rings[2].Value);
        Assert.True(rings[2].Closed); // no stats → pace goal of 1
    }

    [Fact]
    public void Head_And_Score()
    {
        var rings = new[] { new DayRing("a", "a", 10, 10, ""), new DayRing("b", "b", 1, 2, ""), new DayRing("c", "c", 0, 0, "") };
        Assert.Equal("2 מתוך 3 נסגרו", DayRings.Head(rings));
        Assert.Equal(83, DayRings.Score(rings));
        Assert.Equal("כל הטבעות סגורות", DayRings.Head(new[] { rings[0], rings[2] }));
    }
}

public class PayMathTests
{
    [Fact]
    public void Streak_CountsWorkDaysWithDeposits_SkippingWeekends()
    {
        // Thu 24.9.2026. Deals Sun 20, Mon 21, Tue 22, Wed 23 — Fri/Sat aren't work days.
        var now = new DateTime(2026, 9, 24, 9, 0, 0);
        var book = new MonthBook { Year = 2026, Month = 9 };
        foreach (var day in new[] { 17, 20, 21, 22, 23 }) book.Deals.Add(new Deal { ClientName = "x", Date = new DateTime(2026, 9, day) });
        Assert.Equal(5, PayMath.Streak(book, now)); // today has none yet — doesn't break it; Thu 17 bridges the weekend
        book.Deals.Add(new Deal { ClientName = "y", Date = now.Date });
        Assert.Equal(6, PayMath.Streak(book, now));
    }

    [Fact]
    public void Streak_ZeroWithoutDeals() => Assert.Equal(0, PayMath.Streak(null, DateTime.Now));

    [Fact]
    public void DepositDelta_OnlyWhenADealWasAdded()
    {
        Assert.Equal(400, PayMath.DepositDelta(40, 18200, 41, 18600));
        Assert.Null(PayMath.DepositDelta(40, 18200, 40, 18600)); // rules edit, not a deposit
        Assert.Null(PayMath.DepositDelta(40, 18200, 41, 18200));
    }
}

public class NowRitualsTests
{
    [Fact]
    public void Morning_FirstOpenBeforeTwo()
    {
        var morning = new DateTime(2026, 9, 24, 8, 52, 0);
        Assert.True(NowRituals.ShowMorning(null, morning));
        Assert.True(NowRituals.ShowMorning(morning.AddDays(-1), morning));
        Assert.False(NowRituals.ShowMorning(morning.AddHours(-1), morning));
        Assert.False(NowRituals.ShowMorning(null, morning.Date.AddHours(15)));
    }

    [Fact]
    public void Recap_AfterFiveWithWork_OncePerDay()
    {
        var evening = new DateTime(2026, 9, 24, 18, 0, 0);
        Assert.True(NowRituals.OfferRecap(null, evening, 12));
        Assert.False(NowRituals.OfferRecap(null, evening, 0));
        Assert.False(NowRituals.OfferRecap(evening.AddMinutes(-5), evening, 12));
        Assert.False(NowRituals.OfferRecap(null, evening.AddHours(-2), 12));
    }

    [Fact]
    public void WelcomeBack_After45Minutes_SameDay()
    {
        var now = new DateTime(2026, 9, 24, 15, 0, 0);
        Assert.True(NowRituals.WelcomeBack(now.AddMinutes(-45), now));
        Assert.False(NowRituals.WelcomeBack(now.AddMinutes(-44), now));
        Assert.False(NowRituals.WelcomeBack(now.AddDays(-1), now));
        Assert.False(NowRituals.WelcomeBack(null, now));
    }

    [Fact]
    public void Shiny_OneRollInOdds()
    {
        Assert.True(NowRituals.IsShiny(0));
        Assert.Equal(1, Enumerable.Range(0, NowRituals.ShinyOdds).Count(NowRituals.IsShiny));
    }

    [Theory]
    [InlineData(2026, 9, 24, "שנה טובה")]
    [InlineData(2026, 9, 5, "שנה טובה")]
    [InlineData(2026, 12, 8, "חנוכה שמח")]
    [InlineData(2027, 4, 23, "חג שמח")]
    [InlineData(2026, 7, 1, null)]
    public void Holiday_Accents(int y, int m, int d, string? greeting) =>
        Assert.Equal(greeting, NowRituals.Holiday(new DateTime(y, m, d))?.Greeting);

    [Fact]
    public void LongDate_IsHebrew() =>
        Assert.Equal("יום חמישי · 24 בספטמבר · 08:52", NowRituals.LongDate(new DateTime(2026, 9, 24, 8, 52, 0)));

    [Fact]
    public void MorningItems_NeverEmpty_AtMostThree()
    {
        Assert.Single(NowRituals.MorningItems(new CallbackCounts(0, 0), null, Array.Empty<NowCard>()));
        var items = NowRituals.MorningItems(new CallbackCounts(2, 3), null, Array.Empty<NowCard>());
        Assert.Equal("3 חזרות באיחור", items[0]);
    }

    [Fact]
    public void AwayItems_SummarisesWhatHappened()
    {
        var since = new DateTime(2026, 9, 24, 13, 0, 0);
        var now = since.AddHours(1);
        var notes = new[]
        {
            new CallNote("a", since.AddMinutes(10).ToUniversalTime(), 120, "s", "", "ok", "0501"),
            new CallNote("b", since.AddMinutes(20).ToUniversalTime(), 120, "s", "", "ok", "0502",
                ProposedCallback: new CallbackProposal(now.AddDays(1).ToUniversalTime(), "מחר")),
            new CallNote("old", since.AddMinutes(-20).ToUniversalTime(), 120, "s", "", "ok", "0503"),
        };
        var cbs = new[] { new Callback("x", since.AddMinutes(30).ToUniversalTime(), "n") };
        var items = NowRituals.AwayItems(notes, cbs, since, now);
        Assert.Equal(new[] { "סיכמתי 2 שיחות", "שמעתי הבטחה לחזור — מחכה לאישור שלך בתור", "חזרה אחת שקבעת הגיעה לזמנה" }, items);
        Assert.Empty(NowRituals.AwayItems(Array.Empty<CallNote>(), Array.Empty<Callback>(), since, now));
    }
}

public class CommandTextTests
{
    [Theory]
    [InlineData("תב", true)]
    [InlineData("  תבנית הפקדה", true)]
    [InlineData("דני מחר ב-11", false)]
    public void Palette_Detects(string text, bool palette) => Assert.Equal(palette, CommandText.IsPalette(text));

    [Theory]
    [InlineData("תב", "")]
    [InlineData("תבנית הפקדה", "הפקדה")]
    [InlineData("תב שאלון ", "שאלון")]
    public void Palette_Query(string text, string query) => Assert.Equal(query, CommandText.PaletteQuery(text));

    [Fact]
    public void Palette_KeysPinnedTemplates_ThenSnippets_AndFilters()
    {
        var templates = TemplateSeeds.Create();
        var snippets = new List<Snippet> { new("יומן", "הנה היומן שלי") };
        var all = CommandText.Palette(templates, snippets, "", "דני");
        Assert.Equal("1", all[0].Key);
        Assert.Equal("“", all[^1].Key);
        Assert.True(all[^1].IsSnippet);
        Assert.All(all.Take(Math.Min(5, templates.Count)), p => Assert.Matches("^[1-5]$", p.Key));

        var filtered = CommandText.Palette(templates, snippets, "שאלון", null);
        Assert.Contains(filtered, p => p.TemplateId == "questionnaire");
        Assert.DoesNotContain(filtered, p => p.IsSnippet);
    }

    [Fact]
    public void PinnedIndex_OnlyOnEmptyBar()
    {
        Assert.Equal(0, CommandText.PinnedIndex("", 1, 5));
        Assert.Equal(4, CommandText.PinnedIndex("", 5, 5));
        Assert.Null(CommandText.PinnedIndex("", 6, 9));
        Assert.Null(CommandText.PinnedIndex("", 3, 2));
        Assert.Null(CommandText.PinnedIndex("x", 1, 5));
    }

    [Fact]
    public void Move_Wraps()
    {
        Assert.Equal(0, CommandText.Move(3, 1, 4));
        Assert.Equal(3, CommandText.Move(0, -1, 4));
        Assert.Equal(0, CommandText.Move(0, 1, 0));
    }

    [Fact]
    public void Hero_PrefersTheTopCard_ThenRings()
    {
        var now = new DateTime(2026, 9, 24, 15, 0, 0);
        var card = new NowCard("sf:1", NowKind.Salesforce, "", "Sarit Klein", "", Array.Empty<string>(), "", Array.Empty<string>());
        Assert.Equal("סיכמתי את השיחה עם Sarit. לרשום ב-Salesforce?", NowLines.Hero(now, null, card, Array.Empty<DayRing>(), false, default, null));
        Assert.StartsWith("בשיחה", NowLines.Hero(now, null, card, Array.Empty<DayRing>(), true, default, null));
        var closed = new[] { new DayRing("a", "a", 1, 1, "") };
        Assert.Equal("כל הטבעות סגורות. יום מושלם.", NowLines.Hero(now, null, null, closed, false, default, null));
    }
}

public class NowStateTests
{
    [Fact]
    public void Prune_DropsOldHandledIds()
    {
        var now = DateTime.UtcNow;
        var s = new NowState { Handled = { ["old"] = now.AddDays(-8), ["new"] = now.AddDays(-1) } };
        s.Prune(now);
        Assert.Equal(new[] { "new" }, s.Handled.Keys);
    }
}
