using System.Text.Json;
using Palon;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class CallbackStoreTests
{
    static readonly DateTime NowUtc = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);

    const string V1File = """
        {
          "version": 1,
          "reminders": [
            { "id": "a1", "url": "", "label": "call Danny back", "dueAtUtc": "2026-09-24T12:00:00Z", "state": "Pending" },
            { "id": "b2", "url": "https://crm.example.com/lead/7", "label": "", "dueAtUtc": "2026-09-20T08:00:00Z", "state": "Done" },
            { "id": "c3", "url": "https://wa.me/972501234567", "label": "דני", "dueAtUtc": "2026-09-21T08:00:00Z", "state": "Dismissed" },
            { "id": "", "url": "", "label": "no id", "dueAtUtc": "2026-09-25T08:00:00Z", "state": "Pending" },
            { "id": "bad", "url": "not a url", "label": "x", "dueAtUtc": "2026-09-25T08:00:00Z", "state": "Pending" }
          ]
        }
        """;

    [Fact]
    public void V1_file_migrates_losslessly()
    {
        var list = CallbackStore.Parse(V1File, NowUtc, out var migrated);

        Assert.True(migrated);
        Assert.Equal(4, list.Count); // the malformed link is dropped, as v1 did
        Assert.All(list, c => Assert.Equal(CallbackSource.Migrated, c.Source));

        var a = list.Single(c => c.Id == "a1");
        Assert.Equal("call Danny back", a.Note);
        Assert.Equal(CallbackStatus.Open, a.Status);
        Assert.Equal(new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc), a.DueAtUtc);
        Assert.Equal(DateTimeKind.Utc, a.DueAtUtc.Kind);

        var b = list.Single(c => c.Id == "b2");
        Assert.Equal(CallbackStatus.Done, b.Status);
        Assert.Equal("https://crm.example.com/lead/7", b.Url);
        Assert.NotNull(b.CompletedUtc);

        var c = list.Single(c => c.Id == "c3");
        Assert.Equal(CallbackStatus.Cancelled, c.Status);
        Assert.Equal("https://wa.me/972501234567", c.Url); // link kept
        Assert.True(c.HasPhone);                             // and the number surfaced
        Assert.True(Palon.Agent.PhoneMatch.Same("0501234567", c.Phone));

        Assert.Contains(list, x => x.Note == "no id" && x.Id.Length > 0);
    }

    [Fact]
    public void V2_round_trips_without_migration()
    {
        var original = new List<Callback>
        {
            new("x", NowUtc.AddHours(2), "send contract", CallbackStatus.Snoozed, CallbackSource.AutoCall,
                "Dana", "050-123-4567", "", NowUtc, null, 2, "note1"),
            new("y", NowUtc.AddHours(-2), "done", CallbackStatus.Done, CallbackSource.Voice,
                CreatedUtc: NowUtc.AddDays(-1), CompletedUtc: NowUtc),
        };
        var parsed = CallbackStore.Parse(CallbackStore.Serialize(original), NowUtc, out var migrated);

        Assert.False(migrated);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Serialized_envelope_is_versioned()
    {
        using var doc = JsonDocument.Parse(CallbackStore.Serialize(new()));
        Assert.Equal(CallbackStore.CurrentVersion, doc.RootElement.GetProperty("version").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("callbacks", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Corrupt_file_throws_so_it_is_never_overwritten(string json) =>
        Assert.ThrowsAny<Exception>(() => CallbackStore.Parse(json, NowUtc, out _));

    [Theory]
    [InlineData("", "", null, "0501234567", true)]  // phone only
    [InlineData("", "", "Dana", null, true)]        // name only
    [InlineData("", "", null, "abc", false)]        // junk phone doesn't count
    [InlineData("", " ", " ", null, false)]
    public void Validity_accepts_name_or_phone(string url, string note, string? name, string? phone, bool valid) =>
        Assert.Equal(valid, CallbackStore.IsValid(url, note, name, phone));

    [Fact]
    public void Display_line_combines_who_and_what()
    {
        Assert.Equal("Dana · send contract", new Callback("i", NowUtc, "send contract", Name: "Dana").DisplayLine);
        Assert.Equal("050-1234567 · send contract", new Callback("i", NowUtc, "send contract", Phone: "050-1234567").DisplayLine);
        Assert.Equal("send contract", new Callback("i", NowUtc, "send contract").DisplayLine);
    }
}

public class CallbackPlannerTests
{
    // Thursday afternoon — the edge of the Israeli work week.
    static readonly DateTime Thu14 = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Local);

    static Callback At(string id, DateTime local, CallbackStatus status = CallbackStatus.Open) =>
        new(id, local.ToUniversalTime(), id, status);

    [Fact]
    public void Groups_in_display_order()
    {
        var list = new[]
        {
            At("later", new(2026, 9, 27, 10, 0, 0)),      // Sunday: next week
            At("week", new(2026, 9, 26, 10, 0, 0)),       // Saturday
            At("tomorrow", new(2026, 9, 25, 10, 0, 0)),   // Friday
            At("today", new(2026, 9, 24, 18, 0, 0)),
            At("overdue", new(2026, 9, 24, 13, 0, 0)),
            At("overdue2", new(2026, 9, 22, 9, 0, 0)),
            At("done", new(2026, 9, 24, 18, 0, 0), CallbackStatus.Done),
            At("snoozed", new(2026, 9, 24, 16, 0, 0), CallbackStatus.Snoozed),
        };
        var groups = CallbackPlanner.Group(list, Thu14);

        Assert.Equal(
            new[] { CallbackGroup.Overdue, CallbackGroup.Today, CallbackGroup.Tomorrow, CallbackGroup.LaterThisWeek, CallbackGroup.Later },
            groups.Select(g => g.Group));
        Assert.Equal(new[] { "overdue2", "overdue" }, groups[0].Items.Select(c => c.Id)); // oldest first
        Assert.Equal(new[] { "snoozed", "today" }, groups[1].Items.Select(c => c.Id));
        Assert.DoesNotContain(groups.SelectMany(g => g.Items), c => c.Id == "done");
    }

    [Fact]
    public void Counts_for_badges()
    {
        var list = new[]
        {
            At("o1", Thu14.AddHours(-1)), At("o2", Thu14.AddDays(-2)),
            At("t1", Thu14.AddHours(3)),
            At("tm", Thu14.AddDays(1)),
            At("d", Thu14.AddHours(-1), CallbackStatus.Done),
        };
        var counts = CallbackPlanner.Counts(list, Thu14);
        Assert.Equal(2, counts.Overdue);
        Assert.Equal(1, counts.DueToday);
        Assert.Equal(3, counts.Badge);
    }

    [Fact]
    public void Quick_picks_on_thursday_skip_the_weekend()
    {
        var picks = CallbackPlanner.QuickPicks(Thu14).ToDictionary(p => p.Key);
        Assert.Equal(Thu14.AddHours(1), picks["in1h"].DueLocal);
        Assert.True(picks["tonight"].Enabled);
        Assert.Equal(new DateTime(2026, 9, 24, 19, 0, 0), picks["tonight"].DueLocal);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), picks["tomorrow"].DueLocal); // Fri → Sun
        Assert.Equal("Sun 10:00", picks["tomorrow"].Label);
        Assert.False(picks["sunday"].Enabled); // same moment as "tomorrow"
    }

    [Fact]
    public void Quick_picks_after_seven_disable_tonight()
    {
        var tue20 = new DateTime(2026, 9, 22, 20, 15, 30);
        var picks = CallbackPlanner.QuickPicks(tue20).ToDictionary(p => p.Key);
        Assert.False(picks["tonight"].Enabled);
        Assert.Equal(new DateTime(2026, 9, 22, 21, 15, 0), picks["in1h"].DueLocal); // seconds trimmed
        Assert.Equal(new DateTime(2026, 9, 23, 10, 0, 0), picks["tomorrow"].DueLocal);
        Assert.Equal("Tomorrow 10:00", picks["tomorrow"].Label);
        Assert.True(picks["sunday"].Enabled);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), picks["sunday"].DueLocal);
    }

    [Theory]
    [InlineData(2026, 9, 25, 2026, 9, 27)] // Friday → Sunday
    [InlineData(2026, 9, 26, 2026, 9, 27)] // Saturday → Sunday
    [InlineData(2026, 9, 27, 2026, 9, 28)] // Sunday → Monday
    public void Next_work_morning(int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateTime(ey, em, ed, 10, 0, 0), CallbackPlanner.NextWorkMorning(new DateTime(y, m, d, 9, 0, 0)));

    [Fact]
    public void Sunday_pick_on_sunday_is_next_week() =>
        Assert.Equal(new DateTime(2026, 10, 4, 10, 0, 0), CallbackPlanner.NextSundayMorning(new DateTime(2026, 9, 27, 8, 0, 0)));

    [Fact]
    public void Snooze_moves_due_and_counts()
    {
        var c = At("x", Thu14.AddMinutes(-5));
        var once = CallbackPlanner.Snooze(c, SnoozeKind.TenMinutes, Thu14);
        Assert.Equal(CallbackStatus.Snoozed, once.Status);
        Assert.Equal(1, once.SnoozeCount);
        Assert.Equal(Thu14.AddMinutes(10).ToUniversalTime(), once.DueAtUtc);

        var twice = CallbackPlanner.Snooze(once, SnoozeKind.OneHour, Thu14);
        Assert.Equal(2, twice.SnoozeCount);
        Assert.Equal(Thu14.AddHours(1).ToUniversalTime(), twice.DueAtUtc);

        var tomorrow = CallbackPlanner.Snooze(c, SnoozeKind.Tomorrow, Thu14);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0).ToUniversalTime(), tomorrow.DueAtUtc);
    }

    [Fact]
    public void Done_and_undo()
    {
        var now = Thu14.ToUniversalTime();
        var c = CallbackPlanner.Snooze(At("x", Thu14), SnoozeKind.TenMinutes, Thu14);
        var done = CallbackPlanner.MarkDone(c, now);
        Assert.False(done.IsActive);
        Assert.Equal(now, done.CompletedUtc);

        var undone = CallbackPlanner.Reopen(done);
        Assert.True(undone.IsActive);
        Assert.Equal(CallbackStatus.Snoozed, undone.Status); // snooze history kept
        Assert.Null(undone.CompletedUtc);

        Assert.Equal(CallbackStatus.Open, CallbackPlanner.Reopen(CallbackPlanner.Cancel(At("y", Thu14), now)).Status);
    }

    [Fact]
    public void Missed_when_fired_late()
    {
        var c = At("x", Thu14);
        Assert.False(CallbackPlanner.IsMissed(c, Thu14.ToUniversalTime().AddMinutes(1)));
        Assert.True(CallbackPlanner.IsMissed(c, Thu14.ToUniversalTime().AddHours(10)));
    }
}

public class CallbackProposalTests
{
    static readonly DateTime Thu14 = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("מחר ב-11", 2026, 9, 25, 11, 0)]                     // Thu → Fri: literal day kept
    [InlineData("אחזור אליך מחר ב-11", 2026, 9, 25, 11, 0)]
    [InlineData("תתקשר אליי ביום ראשון בצהריים", 2026, 9, 27, 12, 0)]
    [InlineData("call me back in an hour", 2026, 9, 24, 15, 0)]
    [InlineData("in 20 minutes", 2026, 9, 24, 14, 20)]
    [InlineData("בעוד שעתיים", 2026, 9, 24, 16, 0)]
    [InlineData("בעוד חצי שעה", 2026, 9, 24, 14, 30)]
    [InlineData("תחזור אליי בשלוש", 2026, 9, 24, 15, 0)]
    [InlineData("tomorrow at 3pm", 2026, 9, 25, 15, 0)]
    [InlineData("Sunday at 10:30", 2026, 9, 27, 10, 30)]
    [InlineData("הערב בשמונה", 2026, 9, 24, 20, 0)]
    [InlineData("ביום חמישי ב-5", 2026, 10, 1, 17, 0)]               // "Thursday" said on Thursday → next week; 5 → 17:00
    [InlineData("at 11", 2026, 9, 25, 11, 0)]                         // past today → tomorrow
    public void Resolves_common_phrases(string phrase, int y, int mo, int d, int h, int mi)
    {
        Assert.True(TimePhrase.TryResolve(phrase, Thu14, out var when, out var complete));
        Assert.True(complete);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), when);
    }

    [Fact]
    public void Day_only_is_incomplete_with_morning_default()
    {
        Assert.True(TimePhrase.TryResolve("שבוע הבא", Thu14, out var when, out var complete));
        Assert.False(complete);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), when);
    }

    [Theory]
    [InlineData("בימים הקרובים")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("we'll talk")]
    public void Vague_phrases_resolve_to_nothing(string? phrase) =>
        Assert.False(TimePhrase.TryResolve(phrase, Thu14, out _, out _));

    [Fact]
    public void Extracts_and_strips_the_callback_line()
    {
        const string reply = "יש ניסיון - רוצה לשמוע עוד, אחזור אליו מחר ב-11.\n" +
            "CALLBACK: {\"when_iso\":\"2026-09-25T11:00\",\"phrase\":\"אחזור אליו מחר ב-11\",\"reason\":\"לחזור עם הצעה\"}";
        var (summary, proposal) = CallbackProposals.Extract(reply, Thu14);

        Assert.Equal("יש ניסיון - רוצה לשמוע עוד, אחזור אליו מחר ב-11.", summary);
        Assert.NotNull(proposal);
        Assert.Equal(new DateTime(2026, 9, 25, 11, 0, 0), proposal!.WhenUtc.ToLocalTime());
        Assert.Equal("לחזור עם הצעה", proposal.Reason);
        Assert.True(proposal.IsPending);
    }

    [Fact]
    public void No_line_no_proposal()
    {
        var (summary, proposal) = CallbackProposals.Extract("  just a note  ", Thu14);
        Assert.Equal("just a note", summary);
        Assert.Null(proposal);
    }

    [Theory]
    [InlineData("CALLBACK: {broken")]
    [InlineData("CALLBACK: {\"phrase\":\"soon\"}")]                                  // no time anywhere
    [InlineData("CALLBACK: {\"when_iso\":\"2026-09-20T10:00\",\"phrase\":\"x\"}")]  // before the call
    [InlineData("CALLBACK: {\"when_iso\":\"banana\"}")]
    [InlineData("CALLBACK: []")]
    public void Malformed_line_is_dropped_but_note_kept(string line)
    {
        var (summary, proposal) = CallbackProposals.Extract("note text\n" + line, Thu14);
        Assert.Equal("note text", summary);
        Assert.Null(proposal);
    }

    [Fact]
    public void Missing_when_iso_falls_back_to_the_phrase()
    {
        var proposal = CallbackProposals.ParseLine("CALLBACK: {\"phrase\":\"call me back in an hour\"}", Thu14);
        Assert.Equal(Thu14.AddHours(1), proposal!.WhenUtc.ToLocalTime());
        Assert.Null(proposal.Reason);
    }

    [Fact]
    public void Model_time_wins_when_phrase_only_names_a_day()
    {
        var proposal = CallbackProposals.ParseLine(
            "CALLBACK: {\"when_iso\":\"2026-09-27T13:00\",\"phrase\":\"ביום ראשון\"}", Thu14);
        Assert.Equal(new DateTime(2026, 9, 27, 13, 0, 0), proposal!.WhenUtc.ToLocalTime());
    }

    [Fact]
    public void Exact_phrase_overrides_a_miscomputed_model_time()
    {
        // Model said Thursday; the words say tomorrow at 11.
        var proposal = CallbackProposals.ParseLine(
            "CALLBACK: {\"when_iso\":\"2026-09-24T23:00\",\"phrase\":\"מחר ב-11\"}", Thu14);
        Assert.Equal(new DateTime(2026, 9, 25, 11, 0, 0), proposal!.WhenUtc.ToLocalTime());
    }

    [Fact]
    public void Proposal_survives_note_serialization()
    {
        var note = new CallNote("n", DateTime.UtcNow, 60, "s", "t", "ok", "050",
            ProposedCallback: new CallbackProposal(DateTime.UtcNow, "מחר", "r"));
        var back = JsonSerializer.Deserialize<CallNote>(JsonSerializer.Serialize(note));
        Assert.Equal(note.ProposedCallback, back!.ProposedCallback);
    }
}
