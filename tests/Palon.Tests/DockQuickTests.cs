using Palon.Agentic;
using Palon.Interop;
using Palon.Notes;
using Palon.Terminal;
using Palon.UI;
using Xunit;

namespace Palon.Tests;

public class DockQuickTests
{
    // Thursday 24 Sep 2026, 09:20 — Friday/Saturday are off, so "next work morning" is Sunday.
    static readonly DateTime Now = new(2026, 9, 24, 9, 20, 0);

    static readonly List<QuickPerson> People = new()
    {
        new("דני לוי", "052-555-0148"),
        new("מאיה כהן", "054-221-9087"),
        new("תומר לוי", "050-712-3398"),
        new("Dani Levi", null),
    };

    static QuickParse P(string text, QuickPerson? context = null) => DockQuick.Parse(text, Now, People, context);

    [Fact]
    public void Name_and_bare_hour_books_today_when_still_ahead()
    {
        var p = P("דני 11");
        Assert.Equal("דני לוי", p.Who!.Name);
        Assert.Equal(new DateTime(2026, 9, 24, 11, 0, 0), p.WhenLocal);
        Assert.True(p.CanBook);
        Assert.False(p.IsNew);
    }

    [Fact]
    public void A_bare_hour_already_past_rolls_to_tomorrow()
    {
        Assert.Equal(new DateTime(2026, 9, 25, 8, 0, 0), P("דני 8:00").WhenLocal);
    }

    [Fact]
    public void Tomorrow_evening_is_19()
    {
        var p = P("מאיה מחר בערב");
        Assert.Equal("מאיה כהן", p.Who!.Name);
        Assert.Equal(new DateTime(2026, 9, 25, 19, 0, 0), p.WhenLocal);
    }

    [Fact]
    public void Full_name_and_clock_time_use_both_words()
    {
        var p = P("תומר לוי מחר 16:30");
        Assert.Equal("תומר לוי", p.Who!.Name);
        Assert.Equal(new DateTime(2026, 9, 25, 16, 30, 0), p.WhenLocal);
    }

    [Fact]
    public void A_bare_weekday_reads_as_that_day()
    {
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), P("מאיה ראשון 10").WhenLocal);
    }

    [Fact]
    public void Text_that_opens_with_a_time_books_the_current_client()
    {
        var ctx = new QuickPerson("רון אברהם", "053-640-1172");
        var p = P("בעוד שעה", ctx);
        Assert.Same(ctx, p.Who);
        Assert.Equal(new DateTime(2026, 9, 24, 10, 20, 0), p.WhenLocal);
    }

    [Fact]
    public void Time_first_without_a_client_asks_who()
    {
        var p = P("בעוד שעה");
        Assert.Null(p.Who);
        Assert.NotNull(p.Miss);
        Assert.False(p.CanBook);
    }

    [Fact]
    public void An_unknown_name_still_books_as_new_with_the_next_work_morning()
    {
        var p = P("רון");
        Assert.Equal("רון", p.Who!.Name);
        Assert.True(p.IsNew);
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), p.WhenLocal); // Thu → Sun (Fri/Sat off)
    }

    [Fact]
    public void A_client_named_like_a_weekday_wins_over_the_weekday()
    {
        var people = new List<QuickPerson> { new("שני אברהם", null) };
        var p = DockQuick.Parse("שני 11", Now, people);
        Assert.Equal("שני אברהם", p.Who!.Name);
    }

    [Fact]
    public void English_names_and_times_work()
    {
        var p = P("dani tomorrow 3pm");
        Assert.Equal("Dani Levi", p.Who!.Name);
        Assert.Equal(new DateTime(2026, 9, 25, 15, 0, 0), p.WhenLocal);
    }

    [Fact]
    public void A_phone_number_finds_its_client()
    {
        var p = P("0545550148 מחר"); // no match → the number itself
        Assert.Equal("0545550148", p.Who!.Name);
        var q = P("0522219087 מחר");
        Assert.Equal("0522219087", q.Who!.Phone);
        var known = P("054-221-9087 מחר");
        Assert.Equal("מאיה כהן", known.Who!.Name);
        Assert.Equal(new DateTime(2026, 9, 25, 10, 0, 0), known.WhenLocal);
    }

    [Fact]
    public void Empty_text_is_empty_and_prefix_matches_a_name()
    {
        Assert.True(P("  ").Empty);
        Assert.Equal("מאיה כהן", P("מאי 12").Who!.Name);
    }

    [Fact]
    public void Preview_reads_name_dot_when()
    {
        Assert.Equal("דני לוי · היום 11:00", DockQuick.Preview(P("דני 11"), Now));
        Assert.Equal("רון (חדש) · ראשון 10:00", DockQuick.Preview(P("רון"), Now));
    }

    [Fact]
    public void Smart_default_prefers_a_heard_future_time_else_the_next_work_morning()
    {
        var heard = new DateTime(2026, 9, 24, 15, 0, 0);
        Assert.Equal(heard, DockQuick.SmartDefault(heard, Now));
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), DockQuick.SmartDefault(null, Now));
        Assert.Equal(new DateTime(2026, 9, 27, 10, 0, 0), DockQuick.SmartDefault(Now.AddMinutes(-30), Now));
        var monday = new DateTime(2026, 9, 28, 14, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 29, 10, 0, 0), DockQuick.SmartDefault(null, monday));
    }

    [Fact]
    public void Rules_move_a_time_to_the_first_allowed_one()
    {
        var rules = new List<Palon.Memory.TimeRule> { new("דני", NotBefore: TimeSpan.FromHours(12)) };
        Assert.Equal(new DateTime(2026, 9, 25, 12, 0, 0), DockQuick.ApplyRules(new DateTime(2026, 9, 25, 10, 0, 0), rules));
        Assert.Equal(new DateTime(2026, 9, 25, 10, 0, 0), DockQuick.ApplyRules(new DateTime(2026, 9, 25, 10, 0, 0), new List<Palon.Memory.TimeRule>()));
    }

    [Fact]
    public void Chips_put_the_heard_time_first_and_cap_at_four()
    {
        var picks = CallbackPlanner.QuickPicks(Now);
        var proposal = new CallbackProposal(new DateTime(2026, 9, 25, 9, 30, 0).ToUniversalTime(), "תתקשר מחר בבוקר");
        var chips = DockQuick.Chips(picks, proposal, Now);
        Assert.Equal(4, chips.Count);
        Assert.True(chips[0].Heard);
        Assert.Equal(new DateTime(2026, 9, 25, 9, 30, 0), chips[0].DueLocal);
        Assert.All(chips, c => Assert.True(c.Enabled));
    }

    [Fact]
    public void A_heard_time_on_a_standard_chip_moves_that_chip_first()
    {
        var picks = CallbackPlanner.QuickPicks(Now);
        var proposal = new CallbackProposal(new DateTime(2026, 9, 24, 19, 0, 0).ToUniversalTime(), "הערב");
        var chips = DockQuick.Chips(picks, proposal, Now);
        Assert.True(chips[0].Heard);
        Assert.Equal("tonight", chips[0].Key);
        Assert.Single(chips, c => c.Heard);
    }

    [Fact]
    public void Chips_drop_disabled_picks()
    {
        var late = new DateTime(2026, 9, 24, 20, 0, 0);
        var chips = DockQuick.Chips(CallbackPlanner.QuickPicks(late), null, late);
        Assert.DoesNotContain(chips, c => c.Key == "tonight");
    }

    [Fact]
    public void Chip_lines_name_the_day()
    {
        var pick = new DockPick("in1h", "", Now.AddHours(1), true, false);
        Assert.Equal(("בעוד שעה", "10:20"), DockQuick.ChipLines(pick, pick.DueLocal, Now));
        var sun = new DockPick("sunday", "", new DateTime(2026, 9, 27, 10, 0, 0), true, false);
        Assert.Equal(("ראשון", "10:00"), DockQuick.ChipLines(sun, sun.DueLocal, Now));
        var heard = new DockPick("heard", "", new DateTime(2026, 9, 25, 9, 30, 0), true, true);
        Assert.Equal("Palon שמע", DockQuick.ChipLines(heard, heard.DueLocal, Now).Top);
    }

    [Fact]
    public void Nudge_moves_by_a_quarter_hour_but_never_into_the_past()
    {
        var t = new DateTime(2026, 9, 24, 9, 30, 0);
        Assert.Equal(t.AddMinutes(15), DockQuick.Nudge(t, 15, Now));
        Assert.Equal(t, DockQuick.Nudge(t, -15, Now));
    }

    [Fact]
    public void Pins_default_then_follow_the_setting()
    {
        var templates = TemplateSeeds.Create();
        Assert.Equal(new[] { "open-pro", "deposit-pro" }, DockPins.Pick(templates, null).Select(t => t.Id));
        Assert.Equal(new[] { "welcome", "questionnaire" }, DockPins.Pick(templates, "welcome, nope ,questionnaire").Select(t => t.Id));
        Assert.Equal(3, DockPins.Pick(templates, "welcome,open-pro,deposit-pro,questionnaire").Count);
        Assert.Equal("הפקדת כספים", DockPins.ShortLabel(templates.First(t => t.Id == "deposit-pro")));
    }

    [Fact]
    public void Rings_count_today_against_small_goals()
    {
        var utc = Now.ToUniversalTime();
        var callbacks = new List<Callback>
        {
            new("a", utc.AddHours(-3), "x", CallbackStatus.Done, CompletedUtc: utc.AddMinutes(-5)),
            new("b", utc.AddHours(3), "y"),
            new("c", utc.AddDays(-1), "z"), // overdue
        };
        var rings = DockRings.Compute(Now, new[] { Now.AddHours(-1), Now.AddDays(-1) }, new[] { Now.Date }, 40, callbacks);
        Assert.Equal(1, rings.Calls);
        Assert.Equal(1, rings.Deposits);
        Assert.Equal(1, rings.CallbacksDone);
        Assert.Equal(2, rings.CallbacksGoal);
        Assert.Equal(1, rings.Overdue);
        Assert.Equal(0.5, rings.CallbacksP);
        Assert.Equal(2, rings.DepositsGoal); // 40 over 22 work days → 2 a day
    }

    // ---- hotkey config ----

    [Fact]
    public void Quick_hotkey_defaults_to_ctrl_alt_r_and_can_be_off()
    {
        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, Hotkey.QuickCallbackDefault.Modifiers);
        Assert.Equal(0x52u, Hotkey.QuickCallbackDefault.Vk); // R
        Assert.Equal(Hotkey.QuickCallbackDefault, Hotkey.Parse(null, Hotkey.QuickCallbackDefault));
        Assert.True(Hotkey.Parse("off", Hotkey.QuickCallbackDefault).IsOff);
        Assert.Equal(Hotkey.QuickCallbackDefault, Hotkey.Parse(Hotkey.QuickCallbackDefault.Serialize(), Hotkey.Default));
    }

    [Fact]
    public void Conflicts_are_found_against_palons_other_hotkeys()
    {
        var others = new List<(string, Hotkey)> { ("dictation", Hotkey.Default), ("Ask Palon", Hotkey.AssistantDefault) };
        Assert.Null(Hotkey.ConflictWith(Hotkey.QuickCallbackDefault, others));
        Assert.Equal("Ask Palon", Hotkey.ConflictWith(Hotkey.AssistantDefault, others));
        Assert.Null(Hotkey.ConflictWith(Hotkey.Off, new[] { ("x", Hotkey.Off) }));

        var ctrlAlt1 = new Hotkey(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x31);
        Assert.Equal("Snippet 1", Hotkey.ConflictWith(ctrlAlt1, Hotkey.Fixed(screenRead: false, snippets: true)));
        Assert.Null(Hotkey.ConflictWith(ctrlAlt1, Hotkey.Fixed(screenRead: true, snippets: false)));
        var screen = new Hotkey(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x53);
        Assert.Equal("Read screen", Hotkey.ConflictWith(screen, Hotkey.Fixed(true, false)));
    }

    // ---- card queue: one nudge at a time ----

    static Nudge N(string id, NudgeKind kind = NudgeKind.Reminder) => new(id, kind, "t", null, null, Now);
    static CallNote Note(string id) => new(id, DateTime.UtcNow, 60, "s", "t", "ok");
    static Callback Cb(string id) => new(id, DateTime.UtcNow, "n");

    [Fact]
    public void Only_high_priority_nudges_reach_the_dock()
    {
        Assert.True(DockCard.DockWorthy(N("a", NudgeKind.Reminder)));
        Assert.False(DockCard.DockWorthy(N("b", NudgeKind.Insight)));
        Assert.False(DockCard.DockWorthy(N("c", NudgeKind.Suggestion)));
        Assert.True(DockCard.DockWorthy(new Nudge("d", NudgeKind.Suggestion, "t", "כן", () => Task.CompletedTask, Now)));
        // The dock's own callback-due card covers a missed callback — never both.
        Assert.False(DockCard.DockWorthy(N("missed:cb1:202609241000", NudgeKind.Reminder)));
    }

    [Fact]
    public void A_fresher_nudge_replaces_the_waiting_one_and_waits_behind_notes()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNote(Note("1")));
        q.Enqueue(DockCard.ForNudge(N("a")));
        q.Enqueue(DockCard.ForNudge(N("b")));
        q.Enqueue(DockCard.ForNote(Note("2")));
        q.Enqueue(DockCard.ForCallback(Cb("x"), false));
        Assert.Equal(new[] { "cb:x", "note:2", "nudge:b" }, q.Waiting.Select(c => c.Key));
    }

    [Fact]
    public void A_showing_nudge_is_never_swapped_and_a_call_drops_nudges()
    {
        var q = new DockCardQueue();
        q.Enqueue(DockCard.ForNudge(N("a")));
        q.Enqueue(DockCard.ForNudge(N("b")));
        Assert.Equal("nudge:a", q.Current!.Key);
        Assert.Empty(q.Waiting);
        q.OnCallStarted();
        Assert.Null(q.Current);
        q.Enqueue(DockCard.ForCallback(Cb("x"), false));
        q.Enqueue(DockCard.ForNudge(N("c")));
        q.OnCallStarted();
        Assert.Equal(new[] { "cb:x" }, q.Waiting.Select(c => c.Key));
    }

    [Fact]
    public void Done_line_counts_whats_left() =>
        Assert.Equal(new[] { "בוצע · סיימת להיום", "בוצע · נשארה אחת היום", "בוצע · נשארו 3 היום" },
            new[] { DockText.DoneLeft(0), DockText.DoneLeft(1), DockText.DoneLeft(3) });
}
