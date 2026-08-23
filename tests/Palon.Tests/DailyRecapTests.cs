using Palon;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class DailyRecapTests
{
    static readonly DateTime NowLocal = DateTime.Now.Date.AddHours(18);

    [Fact]
    public void Quiet_day_still_produces_a_sane_block()
    {
        var data = DailyRecap.Build(new(), new(), new(), NowLocal);
        Assert.Contains("Calls today: none.", data);
    }

    [Fact]
    public void Includes_todays_activity_and_pending_reminders()
    {
        var todayUtc = NowLocal.ToUniversalTime().AddHours(-2);
        var notes = new List<CallNote>
        {
            new("a", todayUtc, 240, "• agreed on terms\nNext step: send contract", "long transcript", "ok", "0501234567"),
            new("old", todayUtc.AddDays(-2), 100, "old note", "old", "ok"),
        };
        var calls = new List<CallRecord> { new(todayUtc, 240, "0501234567"), new(todayUtc.AddDays(-1), 60) };
        var reminders = new List<Reminder>
        {
            new("r1", "", "call Danny back", DateTime.UtcNow.AddHours(3), ReminderState.Pending),
            new("r2", "", "done thing", DateTime.UtcNow.AddHours(-1), ReminderState.Done),
        };

        var data = DailyRecap.Build(notes, calls, reminders, NowLocal);
        Assert.Contains("Calls today: 1", data);
        Assert.Contains("Next step: send contract", data);
        Assert.DoesNotContain("old note", data);
        Assert.Contains("call Danny back", data);
        Assert.DoesNotContain("done thing", data);
    }
}
