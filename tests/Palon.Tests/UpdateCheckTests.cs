using Palon;
using Xunit;

namespace Palon.Tests;

public class UpdateCheckTests
{
    static readonly Version Current = new(8, 2, 0);

    [Theory]
    [InlineData("8.3.0", true)]
    [InlineData("9.0.0", true)]
    [InlineData("8.3", true)]     // two-part tag still compares
    [InlineData("8.2.0", false)]
    [InlineData("8.1.9", false)]
    [InlineData("garbage", false)]
    [InlineData("", false)]
    public void IsNewer_compares_semver_tags(string tag, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(tag, Current));

    [Fact]
    public void Stored_latest_round_trips()
    {
        var parsed = UpdateCheck.Parse("8.3.0 https://github.com/x/y/releases/tag/v8.3.0");
        Assert.NotNull(parsed);
        Assert.Equal("8.3.0", parsed!.Value.Version);
        Assert.Equal("https://github.com/x/y/releases/tag/v8.3.0", parsed.Value.Url);
        Assert.Null(UpdateCheck.Parse(null));
        Assert.Null(UpdateCheck.Parse("nospace"));
    }
}
