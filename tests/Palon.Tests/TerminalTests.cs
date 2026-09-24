using System.IO;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;
using Xunit;

namespace Palon.Tests;

public class TemplateFillTests
{
    static MessageTemplate Replace() => new() { Id = "r", Mode = TemplateMode.Replace, Head = "היי ", Text = "היי !\nשמחתי לשוחח איתך" };
    static MessageTemplate Prepend() => new() { Id = "p", Mode = TemplateMode.Prepend, Text = "מענה על שאלון - https://x" };

    [Fact]
    public void NoName_LeavesTextUntouched()
    {
        var parts = TemplateFill.Parts(Replace(), "  ");
        Assert.Equal("", parts.Pre);
        Assert.Equal("", parts.Name);
        Assert.Equal("היי !\nשמחתי לשוחח איתך", parts.Full);
    }

    [Fact]
    public void Replace_PutsNameAfterHead()
    {
        var parts = TemplateFill.Parts(Replace(), "דני");
        Assert.Equal("היי ", parts.Pre);
        Assert.Equal("דני", parts.Name);
        Assert.Equal("היי דני!\nשמחתי לשוחח איתך", parts.Full);
    }

    [Fact]
    public void Prepend_AddsGreetingLine()
    {
        Assert.Equal("היי שירן!\nמענה על שאלון - https://x", TemplateFill.Fill(Prepend(), " שירן "));
    }

    [Fact]
    public void Replace_WithEditedHead_FallsBackToPrepend()
    {
        var t = Replace();
        t.Text = "שלום!\nטקסט";
        Assert.Equal("היי דני!\nשלום!\nטקסט", TemplateFill.Fill(t, "דני"));
    }

    [Theory]
    [InlineData("דני לוי", "דני")]
    [InlineData("  Sarit Klein ", "Sarit")]
    [InlineData("שירן", "שירן")]
    [InlineData(null, "")]
    public void FirstName_TakesFirstWord(string? full, string expected) =>
        Assert.Equal(expected, TemplateFill.FirstName(full));

    [Fact]
    public void Seeds_AreTheFiveTemplates_WithRepNameLiteral()
    {
        var seeds = TemplateSeeds.Create();
        Assert.Equal(5, seeds.Count);
        Assert.Equal(new[] { "welcome", "open-pro", "open-israel", "questionnaire", "deposit-pro" }, seeds.Select(s => s.Id));
        Assert.Contains("נועם", seeds[1].Text);
        Assert.StartsWith("ברוך הבא!", seeds[0].Text);
        Assert.Equal("ברוך הבא דני!", TemplateFill.Fill(seeds[0], "דני").Split('\n')[0]);
        Assert.Equal("היי דני!", TemplateFill.Fill(seeds[2], "דני").Split('\n')[0]);
    }

    [Theory]
    [InlineData("נרשמה אתמול, לא סיימה את השאלון", "questionnaire")]
    [InlineData("פתח חשבון פרו, מתכנן להפקיד 10K", "deposit-pro")]
    [InlineData("מתלבטת בין קולמקס ישראל לפרו", "open-israel")]
    [InlineData("שאל על שורטים ומניות OTC בפרו", "open-pro")]
    [InlineData("היה בנהיגה, ביקש שיחזרו אליו בערב", null)]
    public void Suggest_ByKeywords(string text, string? expectedId) =>
        Assert.Equal(expectedId, TemplateFill.Suggest(TemplateSeeds.Create(), text)?.Id);

    [Fact]
    public void Store_SeedsThenPersistsEditsAndRemovals()
    {
        var path = Path.Combine(Path.GetTempPath(), $"palon-templates-{Guid.NewGuid():n}.json");
        TemplatesStore.PathOverride = path;
        try
        {
            Assert.Equal(5, TemplatesStore.Load().Count);
            var added = new MessageTemplate { Title = "חדש", Text = "טקסט" };
            Assert.True(TemplatesStore.Save(added));
            Assert.Equal(6, TemplatesStore.Load().Count);
            var removed = TemplatesStore.Load()[1];
            Assert.True(TemplatesStore.Remove(removed.Id));
            Assert.DoesNotContain(TemplatesStore.Load(), t => t.Id == removed.Id);
            Assert.True(TemplatesStore.Restore(removed, 1));
            Assert.Equal(removed.Id, TemplatesStore.Load()[1].Id);
            Assert.Equal(TemplateMode.Replace, TemplatesStore.Load()[0].Mode);
        }
        finally
        {
            TemplatesStore.PathOverride = null;
            File.Delete(path);
        }
    }
}

public class CallSummaryTests
{
    [Fact]
    public void Format_IsSalesforceReady()
    {
        var started = new DateTime(2026, 9, 24, 14, 12, 0, DateTimeKind.Local);
        var note = new CallNote("n1", started.ToUniversalTime(), 372,
            "פתח חשבון פרו אתמול, מתכנן להפקיד 10K עד סוף השבוע.\nCALLBACK: {\"when_iso\":\"x\"}", "תמלול", "ok", "0501234567");
        var text = CallSummary.Format(note, "דני לוי", started);
        Assert.Equal("סיכום שיחה · דני לוי · 24.9 14:12 (6:12)\nפתח חשבון פרו אתמול, מתכנן להפקיד 10K עד סוף השבוע.", text);
    }

    [Fact]
    public void Format_AddsAgreedCallback_AndFallsBackToNumberAndTranscript()
    {
        var started = new DateTime(2026, 9, 24, 12, 31, 0, DateTimeKind.Local);
        var when = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Local);
        var note = new CallNote("n2", started.ToUniversalTime(), 48, null, "היה בנהיגה", "transcript-only", "0581234567",
            ProposedCallback: new CallbackProposal(when.ToUniversalTime(), "ביום ראשון בצהריים"));
        var text = CallSummary.Format(note, null, started);
        Assert.Equal("סיכום שיחה · 0581234567 · 24.9 12:31 (0:48)\nהיה בנהיגה\nחזרה: ראשון 27.9 12:00", text);
    }

    [Fact]
    public void When_IsShortAndRelative()
    {
        var now = new DateTime(2026, 9, 24, 14, 20, 0); // Thursday
        Assert.Equal("16:00", He.When(new DateTime(2026, 9, 24, 16, 0, 0), now));
        Assert.Equal("מחר 10:00", He.When(new DateTime(2026, 9, 25, 10, 0, 0), now));
        Assert.Equal("ראשון 12:00", He.When(new DateTime(2026, 9, 27, 12, 0, 0), now));
        Assert.Equal("אתמול 11:30", He.When(new DateTime(2026, 9, 23, 11, 30, 0), now));
        Assert.Equal("5.10 · 10:00", He.When(new DateTime(2026, 10, 5, 10, 0, 0), now));
        Assert.Equal("1,234", He.N(1234.4));
        Assert.Equal("2.5", He.R1(2.5));
        Assert.Equal("3", He.R1(3.0));
    }
}

public class MonthViewTests
{
    static Deal D(int day, decimal amount, DealSource source = DealSource.Affiliate, string? aff = null, DealRegion region = DealRegion.Pro) =>
        new() { ClientName = $"C{day}-{amount}", Date = new DateTime(2026, 9, day), Amount = amount, Source = source, Affiliate = aff, Region = region };

    static MonthBook Book() => new()
    {
        Year = 2026,
        Month = 9,
        Target = 20,
        Deals =
        {
            D(1, 1000, aff: "OMRI"), D(1, 3000, aff: "OMRI"), D(2, 3000, DealSource.PPC),
            D(6, 1000, aff: "OMRI"), D(6, 1000), D(6, 10000, aff: "DAVID"), D(6, 3000, DealSource.PPC), D(6, 1000),
            D(7, 2000, aff: "DAVID"),
        },
    };

    [Fact]
    public void Bars_CoverWorkDays_AndMatchTheCount()
    {
        var book = Book();
        var asOf = new DateTime(2026, 9, 8, 18, 0, 0);
        var stats = SalesStats.Compute(book, BonusRules.Default(), asOf);
        var bars = MonthView.Bars(book, stats, asOf);
        Assert.Equal(book.WorkDays, bars.Count);
        Assert.Equal(book.Deals.Count, bars.Where(b => !b.IsFuture).Sum(b => b.Count));
        Assert.Equal(book.Deals.Count, bars.Last(b => !b.IsFuture).Cumulative);
        Assert.True(bars.Single(b => b.IsToday).Date == asOf.Date);
        Assert.All(bars.Where(b => b.IsFuture), b => Assert.Equal(stats.RequiredPerDay ?? 0, b.Needed));
    }

    [Fact]
    public void Insights_NameSourceAffiliateBonusAndBestDay()
    {
        var book = Book();
        var rules = BonusRules.Default();
        var stats = SalesStats.Compute(book, rules, new DateTime(2026, 9, 8));
        var lines = MonthView.Insights(book, stats, rules);
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("78% מההפקדות שלך מגיעות מ-Affiliate. OMRI לבד הביא 3.", lines[0]);
        Assert.Contains("PPC הכי משתלם לבונוס: ₪600", lines[1]);
        Assert.StartsWith("השיא שלך החודש: 5 הפקדות ב-6.9.", lines[2]);
        Assert.Contains("עשית 5 ומעלה ביום פעם אחת", lines[2]);
    }

    [Fact]
    public void Insights_EmptyMonth_SaysNothing()
    {
        var book = new MonthBook { Year = 2026, Month = 9, Target = 55 };
        var rules = BonusRules.Default();
        Assert.Empty(MonthView.Insights(book, SalesStats.Compute(book, rules, new DateTime(2026, 9, 8)), rules));
    }

    [Fact]
    public void Brief_CombinesCallbacksAndTarget()
    {
        var book = Book();
        var stats = SalesStats.Compute(book, BonusRules.Default(), new DateTime(2026, 9, 8));
        var brief = MonthView.Brief(new CallbackCounts(3, 2), stats);
        Assert.StartsWith("יש לך 5 חזרות פתוחות להיום, 2 מהן באיחור. חסרים 11 חשבונות ליעד:", brief);
        Assert.Equal("כל החזרות של היום בוצעו.", MonthView.Brief(new CallbackCounts(0, 0), null));
    }

    [Fact]
    public void Answers_PaceAndPay()
    {
        var rules = BonusRules.Default();
        var stats = SalesStats.Compute(Book(), rules, new DateTime(2026, 9, 8));
        Assert.StartsWith("נשארו ", MonthView.AnswerPace(stats));
        Assert.Contains("מאושר עד עכשיו: ₪10,000.", MonthView.AnswerPay(stats, rules));
    }
}

public class ClientIndexTests
{
    static Callback Cb(string? name, string? phone, DateTime dueUtc, CallbackStatus status = CallbackStatus.Open) =>
        new(Guid.NewGuid().ToString("n"), dueUtc, "note", status, Name: name, Phone: phone, CreatedUtc: dueUtc.AddHours(-1));

    [Fact]
    public void MergesCallsByPhone_AndDealsByName()
    {
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        var callbacks = new[] { Cb("דני לוי", "050-555-0186", now.AddHours(2)) };
        var notes = new[]
        {
            new CallNote("a", now.AddHours(-3), 372, "שיחה ראשונה", "", "ok", "+972 50 555 0186"),
            new CallNote("b", now.AddHours(-1), 120, "שיחה אחרת", "", "ok", "0541112222"),
        };
        var deals = new[] { new Deal { ClientName = "  דני  לוי ", Date = new DateTime(2026, 9, 24), Amount = 10000 } };

        var cards = ClientIndex.Build(notes, callbacks, deals);
        Assert.Equal(2, cards.Count);
        var dani = cards.Single(c => c.Name == "דני לוי");
        Assert.Single(dani.Calls);
        Assert.Single(dani.Deals);
        Assert.Single(dani.Callbacks);
        Assert.Equal("דל", dani.Initials);
        Assert.Equal(ClientTone.Deposited, dani.Tone(now.ToLocalTime()));
        var tl = dani.Timeline(BonusRules.Default());
        Assert.Equal(3, tl.Count);
        Assert.Contains(tl, e => e.Kind == TimelineKind.Deal && e.Text.Contains("$10,000"));
        var unknown = cards.Single(c => c.Name == "0541112222");
        Assert.Equal("#", unknown.Initials);
    }

    [Fact]
    public void Search_ByNameOrDigits()
    {
        var now = DateTime.UtcNow;
        var cards = ClientIndex.Build(Array.Empty<CallNote>(),
            new[] { Cb("Maya Goldberg", "054-555-0172", now), Cb("אבי חדד", "058-555-0127", now) }, Array.Empty<Deal>());
        Assert.Equal("Maya Goldberg", Assert.Single(ClientIndex.Search(cards, "maya")).Name);
        Assert.Equal("אבי חדד", Assert.Single(ClientIndex.Search(cards, "0127")).Name);
        Assert.Equal(2, ClientIndex.Search(cards, "").Count);
    }

    [Fact]
    public void NameForPhone_FindsTheCallbackName()
    {
        var callbacks = new[] { Cb("Sarit Klein", "054-555-0157", DateTime.UtcNow) };
        Assert.Equal("Sarit Klein", ClientIndex.NameForPhone(callbacks, "+972545550157"));
        Assert.Null(ClientIndex.NameForPhone(callbacks, "0500000000"));
    }
}
