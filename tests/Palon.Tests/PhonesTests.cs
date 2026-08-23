using Palon;
using Xunit;

namespace Palon.Tests;

public class PhonesTests
{
    [Theory]
    [InlineData("050-123-4567", "972501234567")]      // trunk zero → country code
    [InlineData("0501234567", "972501234567")]
    [InlineData("+972 50-123-4567", "972501234567")]  // explicit international
    [InlineData("00972501234567", "972501234567")]    // 00 dialing prefix
    [InlineData("501234567", "972501234567")]         // local without the trunk zero
    [InlineData("+1 (415) 555-2671", "14155552671")]  // non-Israeli passes through
    [InlineData("14155552671", "14155552671")]        // ≥11 digits = already international
    public void Normalizes_to_international_digits(string raw, string expected) =>
        Assert.Equal(expected, Phones.ToInternationalDigits(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]        // way too short
    [InlineData("not a phone")]
    public void Rejects_unlinkable_input(string? raw) =>
        Assert.Null(Phones.ToInternationalDigits(raw));

    [Fact]
    public void WaMe_url_encodes_the_prefilled_message()
    {
        var url = Phones.WaMeUrl("050-123-4567", "היי דני, בהמשך לשיחה");
        Assert.NotNull(url);
        Assert.StartsWith("https://wa.me/972501234567?text=", url);
        Assert.DoesNotContain(" ", url);
    }

    [Fact]
    public void WaMe_url_without_text_is_a_plain_chat_link() =>
        Assert.Equal("https://wa.me/972501234567", Phones.WaMeUrl("0501234567"));
}
