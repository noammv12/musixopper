using Palon;
using Palon.UI;
using Xunit;

namespace Palon.Tests;

public class StoreLogicTests
{
    [Theory]
    [InlineData("https://crm.example.com/lead/1", "", true)]   // link only
    [InlineData("", "call Danny back", true)]                  // text only
    [InlineData("https://x.co", "label too", true)]            // both
    [InlineData("", "", false)]                                // neither
    [InlineData("", "   ", false)]                             // whitespace label
    [InlineData("not a url", "label", false)]                  // malformed link never passes
    [InlineData("ftp://x.co/file", "", false)]                 // wrong scheme
    public void Reminder_validity_accepts_link_or_label(string url, string label, bool valid) =>
        Assert.Equal(valid, ReminderStore.IsValidReminder(url, label));

    [Fact]
    public void Text_only_reminder_has_a_display_label()
    {
        var reminder = new Reminder("id", "", "call Danny", DateTime.UtcNow, ReminderState.Pending);
        Assert.False(reminder.HasUrl);
        Assert.Equal("call Danny", reminder.DisplayLabel);
    }

    [Theory]
    [InlineData("050-123-4567", "050-123-4567")]
    [InlineData("+972 50 123 4567", "+972 50 123 4567")]
    [InlineData("\"0501234567\"", "0501234567")]  // dialers pass quotes through
    public void Caller_number_sanitizer_keeps_real_numbers(string raw, string expected) =>
        Assert.Equal(expected, CurrentCall.Sanitize(raw));

    [Theory]
    [InlineData("%NUMBER%")]   // dialer passed the placeholder through
    [InlineData("not a phone")]
    [InlineData("12")]         // too few digits
    [InlineData(null)]
    public void Caller_number_sanitizer_rejects_junk(string? raw) =>
        Assert.Null(CurrentCall.Sanitize(raw));

    [Theory]
    [InlineData("3,32", 3u, 32u)]     // Ctrl+Alt+Space
    [InlineData("3,80", 3u, 80u)]     // Ctrl+Alt+P
    public void Hotkey_parse_round_trips(string stored, uint mods, uint vk)
    {
        var combo = Hotkey.Parse(stored);
        Assert.Equal(mods, combo.Modifiers);
        Assert.Equal(vk, combo.Vk);
        Assert.Equal(stored, combo.Serialize());
    }

    [Fact]
    public void Hotkey_off_and_garbage_behave()
    {
        Assert.True(Hotkey.Parse("off").IsOff);
        Assert.Equal(Hotkey.Default, Hotkey.Parse("garbage"));
        Assert.Equal(Hotkey.AssistantDefault, Hotkey.Parse(null, Hotkey.AssistantDefault));
    }
}
