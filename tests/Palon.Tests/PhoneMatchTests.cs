using Palon.Agent;
using Xunit;

namespace Palon.Tests;

public class PhoneMatchTests
{
    [Theory]
    [InlineData("+972 50-123-4567", "0501234567")]
    [InlineData("0501234567", "050-123-4567")]
    [InlineData("501234567", "+972501234567")]
    public void Same_matches_loose_formats(string a, string b)
    {
        Assert.True(PhoneMatch.Same(a, b));
        Assert.True(PhoneMatch.Same(b, a));
    }

    [Theory]
    [InlineData("0501234567", "0501234568")]  // different number
    [InlineData("12345", "12345")]            // too short to be meaningful
    [InlineData(null, "0501234567")]
    [InlineData("", "")]
    public void Same_rejects_mismatch_and_junk(string? a, string? b) =>
        Assert.False(PhoneMatch.Same(a, b));

    [Fact]
    public void Digits_strips_everything_else() =>
        Assert.Equal("972501234567", PhoneMatch.Digits("+972 (50) 123-45.67"));
}
