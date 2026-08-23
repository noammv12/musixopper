using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class NoteBriefTests
{
    static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Prefers_the_next_step_line()
    {
        var body = "• liked the price\n• asked about delivery\nNext step: send the quote by Friday";
        Assert.Equal("Next step: send the quote by Friday", NoteBrief.KeyLine(body));
    }

    [Fact]
    public void Prefers_the_hebrew_next_step_line()
    {
        var body = "• דיברנו על המחיר\nהצעד הבא: לשלוח הצעה עד חמישי";
        Assert.Equal("הצעד הבא: לשלוח הצעה עד חמישי", NoteBrief.KeyLine(body));
    }

    [Fact]
    public void Falls_back_to_the_first_line_without_bullet_dressing()
    {
        Assert.Equal("talked about renewal terms", NoteBrief.KeyLine("• talked about renewal terms\nmore stuff"));
    }

    [Fact]
    public void Compose_carries_number_age_and_truncates()
    {
        var note = new CallNote("id", Now.AddDays(-3), 300, null,
            new string('א', 300), "ok", "050-1234567");
        var brief = NoteBrief.Compose(note, Now);
        // The Hebrew key line is wrapped in bidi isolates (after truncation,
        // so the ellipsis travels inside the run).
        Assert.StartsWith("050-1234567 · 3d ago: \u2068", brief);
        Assert.True(brief.Length <= 95);
        Assert.EndsWith("…\u2069", brief);
    }

    [Fact]
    public void Compose_leaves_ltr_briefs_unwrapped()
    {
        var note = new CallNote("id", Now.AddDays(-1), 300, null,
            "Next step: send the quote", "ok", null);
        Assert.Equal("yesterday: Next step: send the quote", NoteBrief.Compose(note, Now));
    }

    [Theory]
    [InlineData(0, "earlier today")]
    [InlineData(1, "yesterday")]
    [InlineData(6, "6d ago")]
    public void Ago_reads_naturally(int daysBack, string expected) =>
        Assert.Equal(expected, NoteBrief.Ago(Now.AddDays(-daysBack), Now));
}
