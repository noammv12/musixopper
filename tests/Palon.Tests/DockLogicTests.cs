using Palon;
using Palon.Notes;
using Palon.UI;
using Xunit;

namespace Palon.Tests;

public class DockCardQueueTests
{
    static CallNote Note(string id) => new(id, DateTime.UtcNow, 60, "summary", "", "ok", "0501234567");
    static Callback Cb(string id) => new(id, DateTime.UtcNow, "call back", Phone: "0501234567");

    [Fact]
    public void First_card_shows_immediately_later_ones_wait()
    {
        var q = new DockCardQueue();
        Assert.True(q.Enqueue(DockCard.ForNote(Note("n1"))));
        Assert.False(q.Enqueue(DockCard.ForNote(Note("n2"))));
        Assert.Equal("note:n1", q.Current!.Key);
        Assert.Equal(1, q.WaitingCount);
    }

    [Fact]
    public void Due_callbacks_jump_ahead_of_waiting_notes_but_never_replace_the_showing_card()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNote(Note("n1")));
        q.Enqueue(DockCard.ForNote(Note("n2")));
        q.Enqueue(DockCard.ForCallback(Cb("c1"), missed: false));
        Assert.Equal("note:n1", q.Current!.Key);
        Assert.Equal("cb:c1", q.Advance()!.Key);
        Assert.Equal("note:n2", q.Advance()!.Key);
        Assert.Null(q.Advance());
        Assert.Null(q.Current);
    }

    [Fact]
    public void Callbacks_keep_their_own_arrival_order()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNote(Note("n1")));
        q.Enqueue(DockCard.ForNote(Note("n2")));
        q.Enqueue(DockCard.ForCallback(Cb("c1"), false));
        q.Enqueue(DockCard.ForCallback(Cb("c2"), false));
        Assert.Equal(new[] { "cb:c1", "cb:c2", "note:n2" }, q.Waiting.Select(c => c.Key));
    }

    [Fact]
    public void The_same_item_twice_never_doubles()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNote(Note("n1")));
        q.Enqueue(DockCard.ForCallback(Cb("c1"), false));
        q.Enqueue(DockCard.ForCallback(Cb("c1"), true));
        q.Enqueue(DockCard.ForNote(Note("n1")));
        Assert.Equal(1, q.WaitingCount);
        Assert.True(q.Waiting[0].Missed);
    }

    [Fact]
    public void A_call_starting_drops_note_cards_and_parks_a_callback_card()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForCallback(Cb("c1"), false));
        q.Enqueue(DockCard.ForNote(Note("n1")));
        q.OnCallStarted();
        Assert.Null(q.Current);
        Assert.Equal(new[] { "cb:c1" }, q.Waiting.Select(c => c.Key));
        Assert.Equal("cb:c1", q.Resume()!.Key);
    }

    [Fact]
    public void A_call_starting_retires_a_showing_note_card()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNote(Note("n1")));
        q.OnCallStarted();
        Assert.Null(q.Current);
        Assert.Null(q.Resume());
    }

    [Fact]
    public void Forget_drops_a_card_handled_elsewhere()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForCallback(Cb("c1"), false));
        q.Enqueue(DockCard.ForCallback(Cb("c2"), false));
        q.Forget("cb:c2");
        Assert.Equal(0, q.WaitingCount);
        q.Forget("cb:c1");
        Assert.Null(q.Current);
    }
}

public class DockTextTests
{
    // Thursday 24 Sep 2026, 14:12 local — Friday/Saturday aren't work days.
    static readonly DateTime Now = new(2026, 9, 24, 14, 12, 0, DateTimeKind.Local);

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(372, "6:12")]
    [InlineData(3725, "1:02:05")]
    public void Duration_reads_like_a_clock(int seconds, string expected) =>
        Assert.Equal(expected, DockText.Duration(seconds));

    [Fact]
    public void Timer_pads_minutes() => Assert.Equal("02:14", DockText.Timer(TimeSpan.FromSeconds(134)));

    [Fact]
    public void When_names_today_tomorrow_weekday_then_date()
    {
        Assert.Equal("היום 15:00", DockText.When(Now.Date.AddHours(15), Now));
        Assert.Equal("מחר 10:00", DockText.When(Now.Date.AddDays(1).AddHours(10), Now));
        Assert.Equal("ראשון 12:00", DockText.When(Now.Date.AddDays(3).AddHours(12), Now));
        Assert.Equal("5.10 10:00", DockText.When(new DateTime(2026, 10, 5, 10, 0, 0), Now));
    }

    [Fact]
    public void Progress_hides_without_a_target()
    {
        Assert.Equal("40/55", DockText.Progress(40, 55));
        Assert.Null(DockText.Progress(40, null));
        Assert.Null(DockText.Progress(40, 0));
    }

    [Fact]
    public void Overdue_hides_at_zero()
    {
        Assert.Null(DockText.Overdue(0));
        Assert.Equal("2 באיחור", DockText.Overdue(2));
    }

    [Fact]
    public void Remaining_today_has_hebrew_number_agreement()
    {
        Assert.Contains("סיימת", DockText.RemainingToday(0));
        Assert.Contains("חזרה אחת", DockText.RemainingToday(1));
        Assert.Contains("נשארו לך 4 היום", DockText.RemainingToday(4));
    }

    [Fact]
    public void Time_to_call_prefixes_hebrew_names_directly_and_isolates_numbers()
    {
        Assert.Equal("הגיע הזמן לחזור לדני", DockText.TimeToCall("דני"));
        Assert.Equal("הגיע הזמן לחזור ל-⁨050-1234567⁩", DockText.TimeToCall("050-1234567"));
    }

    [Fact]
    public void Due_line_marks_missed_callbacks()
    {
        Assert.Equal("קבעת ל-10:00", DockText.DueLine(Now.Date.AddHours(10), Now, missed: false));
        Assert.Equal("באיחור · קבעת ל-10:00", DockText.DueLine(Now.Date.AddHours(10), Now, missed: true));
    }

    static CallbackProposal Proposal(DateTime local, string phrase = "דבר איתי ביום ראשון בצהריים") =>
        new(local.ToUniversalTime(), phrase);

    [Fact]
    public void Picks_are_hebrew_and_a_heard_time_off_the_grid_leads_as_its_own_chip()
    {
        var picks = DockText.Picks(CallbackPlanner.QuickPicks(Now), Proposal(Now.Date.AddDays(3).AddHours(12)), Now);
        Assert.Equal("heard", picks[0].Key);
        Assert.True(picks[0].Heard);
        Assert.Equal("ראשון 12:00", picks[0].Label);
        Assert.Equal(new[] { "בעוד שעה", "הערב 19:00", "ראשון 10:00" }, picks.Skip(1).Select(p => p.Label));
        Assert.Single(picks, p => p.Heard);
    }

    [Fact]
    public void A_heard_time_on_a_standard_chip_highlights_that_chip()
    {
        var picks = DockText.Picks(CallbackPlanner.QuickPicks(Now), Proposal(Now.Date.AddHours(19)), Now);
        Assert.Equal(3, picks.Count);
        Assert.True(picks.Single(p => p.Key == "tonight").Heard);
    }

    [Fact]
    public void Settled_proposals_highlight_nothing()
    {
        var accepted = Proposal(Now.Date.AddHours(19)) with { State = "accepted" };
        Assert.DoesNotContain(DockText.Picks(CallbackPlanner.QuickPicks(Now), accepted, Now), p => p.Heard);
        Assert.Null(DockText.Heard(accepted));
    }

    [Fact]
    public void Heard_line_quotes_the_phrase() =>
        Assert.Equal("Palon שמע בשיחה: ״דבר איתי מחר״", DockText.Heard(Proposal(Now.AddDays(1), "\"דבר איתי מחר\"")));

    static CallNote SampleNote(string? summary = "• פתח חשבון פרו אתמול\n• מתכנן להפקיד 10K\nהצעד הבא: לשלוח פרטי הפקדה") =>
        new("n1", new DateTime(2026, 9, 24, 14, 5, 48, DateTimeKind.Local).ToUniversalTime(), 372, summary, "transcript", "ok", "052-555-0148");

    [Fact]
    public void Salesforce_summary_has_who_when_duration_bullets_and_next_step()
    {
        var text = DockText.SalesforceSummary(SampleNote(), null, Now);
        var lines = text.Split('\n');
        Assert.Equal("סיכום שיחה · 052-555-0148 · 24.9.2026 14:05 (6:12)", lines[0]);
        Assert.Equal("• פתח חשבון פרו אתמול", lines[1]);
        Assert.Equal("• מתכנן להפקיד 10K", lines[2]);
        Assert.Equal("צעד הבא: לשלוח פרטי הפקדה", lines[3]);
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public void Salesforce_summary_folds_in_the_booked_callback()
    {
        var text = DockText.SalesforceSummary(SampleNote(), Now.Date.AddDays(3).AddHours(12), Now);
        Assert.EndsWith("צעד הבא: לשלוח פרטי הפקדה (חזרה ראשון 12:00)", text);

        var plain = DockText.SalesforceSummary(SampleNote("דיברנו על המחיר."), Now.Date.AddHours(19), Now);
        Assert.EndsWith("• דיברנו על המחיר.\nצעד הבא: חזרה היום 19:00", plain);
    }

    [Fact]
    public void Salesforce_summary_falls_back_to_the_transcript()
    {
        var text = DockText.SalesforceSummary(SampleNote(null), null, Now);
        Assert.EndsWith("• transcript", text);
    }

    [Fact]
    public void One_line_strips_bullets_and_explains_a_missing_summary()
    {
        Assert.Equal("פתח חשבון פרו אתמול", DockText.OneLine(SampleNote()));
        Assert.StartsWith("אין סיכום", DockText.OneLine(SampleNote(null)));
    }
}
