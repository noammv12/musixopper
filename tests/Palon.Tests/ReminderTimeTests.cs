using Palon.Agent;
using Xunit;

namespace Palon.Tests;

public class ReminderTimeTests
{
    static readonly DateTime Now = new(2026, 8, 23, 14, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("2026-08-23 15:30")]
    [InlineData("2026-08-23T15:30")]
    [InlineData("2026-08-24 09:00:00")]
    public void Accepts_future_full_timestamps(string raw)
    {
        Assert.True(CreateReminderTool.TryParseDueLocal(raw, Now, out var due));
        Assert.True(due > Now);
    }

    [Fact]
    public void Bare_time_still_ahead_lands_today()
    {
        Assert.True(CreateReminderTool.TryParseDueLocal("15:30", Now, out var due));
        Assert.Equal(new DateTime(2026, 8, 23, 15, 30, 0), due);
    }

    [Fact]
    public void Bare_time_already_past_rolls_to_tomorrow()
    {
        Assert.True(CreateReminderTool.TryParseDueLocal("09:00", Now, out var due));
        Assert.Equal(new DateTime(2026, 8, 24, 9, 0, 0), due);
    }

    [Theory]
    [InlineData("2026-08-23 13:00")]  // in the past
    [InlineData("2030-01-01 10:00")]  // absurdly far out
    [InlineData("tomorrow at noon")]  // prose is the model's job, not ours
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_past_far_future_and_garbage(string? raw) =>
        Assert.False(CreateReminderTool.TryParseDueLocal(raw, Now, out _));
}
