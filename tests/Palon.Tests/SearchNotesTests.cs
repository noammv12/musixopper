using Palon.Agent;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class SearchNotesTests
{
    static CallNote Note(string id, DateTime startedUtc, string? number, string transcript, string? summary = null) =>
        new(id, startedUtc, 120, summary, transcript, "ok", number);

    static readonly List<CallNote> Notes = new()
    {
        Note("a", new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc), "050-123-4567", "talked about the quote"),
        Note("b", new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc), "+972501234567", "agreed on delivery Tuesday"),
        Note("c", new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc), "0529876543", "price objection", "• wants a discount"),
        Note("d", new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc), null, "cold intro call"),
    };

    [Fact]
    public void Number_filter_matches_loose_formats_newest_first()
    {
        var result = SearchNotesTool.Filter(Notes, query: null, number: "0501234567", limit: 5);
        Assert.Equal(new[] { "b", "a" }, result.Select(n => n.Id));
    }

    [Fact]
    public void Query_searches_summary_and_transcript_case_insensitively()
    {
        Assert.Equal(new[] { "c" }, SearchNotesTool.Filter(Notes, "DISCOUNT", null, 5).Select(n => n.Id));
        Assert.Equal(new[] { "b" }, SearchNotesTool.Filter(Notes, "tuesday", null, 5).Select(n => n.Id));
    }

    [Fact]
    public void No_filters_returns_most_recent_capped_by_limit()
    {
        var result = SearchNotesTool.Filter(Notes, null, null, 2);
        Assert.Equal(new[] { "d", "c" }, result.Select(n => n.Id));
    }
}
