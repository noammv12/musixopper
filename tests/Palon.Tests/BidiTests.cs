using Xunit;

namespace Palon.Tests;

public class BidiTests
{
    [Theory]
    [InlineData("שלום", true)]
    [InlineData("مرحبا", true)]
    [InlineData("hello", false)]
    [InlineData("050-1234567", false)]
    [InlineData("דני - CRM", true)]
    [InlineData("", false)]
    public void Detects_rtl_runs(string text, bool expected) =>
        Assert.Equal(expected, Bidi.HasRtl(text));

    [Fact]
    public void Isolates_hebrew() =>
        Assert.Equal("\u2068שלום\u2069", Bidi.Isolate("שלום"));

    [Fact]
    public void Leaves_latin_untouched() =>
        Assert.Equal("hello", Bidi.Isolate("hello"));
}
