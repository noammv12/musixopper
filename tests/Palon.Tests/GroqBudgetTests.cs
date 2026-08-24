using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class GroqBudgetTests
{
    [Fact]
    public void Tiny_clip_still_gets_the_20s_floor()
    {
        Assert.Equal(20, GroqTranscriber.RequestBudget(TimeSpan.Zero).TotalSeconds, 3);
    }

    [Fact]
    public void Short_question_gets_a_snappy_budget()
    {
        Assert.Equal(21, GroqTranscriber.RequestBudget(TimeSpan.FromSeconds(3)).TotalSeconds, 3);
    }

    [Fact]
    public void Dictation_scales_with_the_clip()
    {
        Assert.Equal(15 + 120, GroqTranscriber.RequestBudget(TimeSpan.FromSeconds(60)).TotalSeconds, 3);
    }

    [Fact]
    public void Call_chunk_is_capped_at_the_http_client_bound()
    {
        Assert.Equal(360, GroqTranscriber.RequestBudget(TimeSpan.FromMinutes(10)).TotalSeconds, 3);
    }
}
