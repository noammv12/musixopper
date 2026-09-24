using Palon.Salesforce;
using Xunit;

namespace Palon.Tests;

public class RehearsalScheduleTests
{
    static RehearsalSettings Taught() => new() { TestRecordUrl = "https://acme.lightning.force.com/lightning/r/Account/001000000000001AAA/view" };

    // 2026-09-24 is a Thursday (work day); 2026-09-25 is a Friday.
    static readonly DateTime Thu = new(2026, 9, 24);

    [Fact]
    public void Off_until_a_skill_is_taught_and_a_test_record_is_known()
    {
        Assert.False(RehearsalSchedule.Active(new RehearsalSettings(), anySkillTaught: true));
        Assert.False(RehearsalSchedule.Active(Taught(), anySkillTaught: false));
        Assert.True(RehearsalSchedule.Active(Taught(), anySkillTaught: true));
        var off = Taught();
        off.Enabled = false;
        Assert.False(RehearsalSchedule.Active(off, true));
    }

    [Fact]
    public void Due_after_2130_on_work_days_once()
    {
        var s = Taught();
        Assert.False(RehearsalSchedule.IsDue(s, true, Thu.AddHours(21).AddMinutes(29)));
        Assert.True(RehearsalSchedule.IsDue(s, true, Thu.AddHours(21).AddMinutes(31)));
        s.LastRunLocal = Thu.AddHours(21).AddMinutes(31);
        Assert.False(RehearsalSchedule.IsDue(s, true, Thu.AddHours(21).AddMinutes(45)));
        s.LastRunLocal = Thu.AddDays(-1).AddHours(21).AddMinutes(31); // yesterday's run doesn't count
        Assert.True(RehearsalSchedule.IsDue(s, true, Thu.AddHours(22)));
    }

    [Fact]
    public void Not_due_on_weekend_or_long_after_the_slot()
    {
        var s = Taught();
        Assert.False(RehearsalSchedule.IsDue(s, true, Thu.AddDays(1).AddHours(21).AddMinutes(40))); // Friday
        Assert.False(RehearsalSchedule.IsDue(s, true, Thu.AddHours(23).AddMinutes(45)));             // missed by >2h
    }

    [Fact]
    public void Next_slot_skips_to_the_next_work_day_and_honors_custom_time()
    {
        var s = Taught();
        Assert.Equal(new DateTime(2026, 9, 27, 21, 30, 0), RehearsalSchedule.Next(s, true, Thu.AddHours(22))); // Sun
        s.Time = "19:05";
        Assert.Equal(Thu.AddHours(19).AddMinutes(5), RehearsalSchedule.Next(s, true, Thu.AddHours(8)));
        s.Time = "garbage";
        Assert.Equal(Thu.AddHours(21).AddMinutes(30), RehearsalSchedule.Next(s, true, Thu.AddHours(8)));
        Assert.Null(RehearsalSchedule.Next(s, false, Thu));
    }

    [Fact]
    public void Only_taught_composer_skills_are_rehearsed()
    {
        var skills = new[]
        {
            new SfSkill { Skill = "LogCall", TaughtAt = "2026-09-01", Steps = { new SfStep() } },
            new SfSkill { Skill = "NewTask", Steps = { new SfStep() } },
            new SfSkill { Skill = "FindRecordByPhone", TaughtAt = "2026-09-01", Steps = { new SfStep() } },
        };
        Assert.Equal(new[] { "LogCall" }, RehearsalSchedule.Taught(skills).Select(s => s.Skill));
    }

    [Fact]
    public void Summary_names_the_broken_step()
    {
        var st = new RehearsalStatus(Thu.AddHours(21.5), new[] { new RehearsalSkillStatus("LogCall", false, false, "Click Log a Call") });
        Assert.False(st.Ok);
        Assert.Contains("Click Log a Call", st.Summary());
    }
}

public class UndoGatingTests
{
    static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
    const string Record = "001000000000001AAA";
    const string RecordUrl = "https://acme.lightning.force.com/lightning/r/Account/001000000000001AAA/view";
    const string TaskUrl = "https://acme.lightning.force.com/lightning/r/Task/00T000000000009AAA/view";

    static SfAuditChange Created(DateTime at, string? createdUrl = TaskUrl, string status = "Verified") =>
        new("e1", at, SfChangeKind.LogCall, status, Record, RecordUrl, createdUrl, null, null, null, "log call");

    [Fact]
    public void Palon_created_task_within_24h_may_be_undone()
    {
        Assert.Null(UndoPolicy.Check(Created(Now.AddHours(-23)), Now, alreadyUndone: false));
    }

    [Fact]
    public void Refuses_old_missing_undone_or_unsaved()
    {
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-25)), Now, false));
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-1)), Now, alreadyUndone: true));
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-1), status: "DryRun"), Now, false));
        Assert.NotNull(UndoPolicy.Check(null, Now, false));
    }

    [Fact]
    public void Refuses_to_delete_anything_but_a_task()
    {
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-1), createdUrl: null), Now, false));
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-1), createdUrl: RecordUrl), Now, false)); // the account itself
        Assert.NotNull(UndoPolicy.Check(Created(Now.AddHours(-1),
            createdUrl: "https://acme.lightning.force.com/lightning/r/Contact/003000000000001AAA/view"), Now, false));
    }

    [Fact]
    public void Field_undo_needs_the_old_value()
    {
        var e = new SfAuditChange("e2", Now.AddHours(-1), SfChangeKind.UpdateField, "Verified", Record, RecordUrl, null, "Status", null, "Hot", "status");
        Assert.NotNull(UndoPolicy.Check(e, Now, false));
        Assert.Null(UndoPolicy.Check(e with { OldValue = "Cold" }, Now, false));
    }

    [Fact]
    public void Delete_only_on_the_created_items_page()
    {
        var e = Created(Now);
        Assert.True(UndoPolicy.OnCreatedItem(e, TaskUrl));
        Assert.False(UndoPolicy.OnCreatedItem(e, RecordUrl));
    }

    [Fact]
    public void Undo_token_is_bound_to_one_entry_single_use_and_expires()
    {
        var clock = Now;
        var gate = new UndoGate(() => clock);
        var t = gate.Issue("e1");
        Assert.False(gate.TryConsume("e2", t, out _));      // other entry: refused and burned
        Assert.False(gate.TryConsume("e1", t, out _));
        var t2 = gate.Issue("e1");
        Assert.True(gate.TryConsume("e1", t2, out _));
        Assert.False(gate.TryConsume("e1", t2, out _));     // single use
        var t3 = gate.Issue("e1");
        clock = clock.AddMinutes(6);
        Assert.False(gate.TryConsume("e1", t3, out _));     // expired
        Assert.False(gate.TryConsume("e1", null, out _));
    }

    [Fact]
    public void Plan_approval_tokens_cannot_undo()
    {
        var gate = new UndoGate(() => Now);
        Assert.False(gate.TryConsume("e1", "DEADBEEF", out _));
    }

    [Fact]
    public void Audit_log_round_trip_marks_undone_entries()
    {
        var lines = new[]
        {
            """{"at":"2026-09-24T10:00:00.0000000Z","evt":"change","id":"a1","kind":"NewTask","status":"Verified","record":"001000000000001AAA","createdUrl":"https://x.lightning.force.com/lightning/r/Task/00T000000000009AAA/view","description":"task"}""",
            """{"at":"2026-09-24T10:05:00.0000000Z","evt":"undo-done","ref":"a1"}""",
            """{"at":"2026-09-24T10:06:00.0000000Z","evt":"approve","plan":"x"}""",
            "not json",
        };
        var changes = AuditLog.Changes(lines, out var undone);
        Assert.Single(changes);
        Assert.Equal(SfChangeKind.NewTask, changes[0].Kind);
        Assert.Contains("a1", undone);
        Assert.NotNull(UndoPolicy.Check(changes[0], Now, undone.Contains("a1")));
    }

    [Fact]
    public void Global_delete_ban_still_holds()
    {
        Assert.True(SfSafety.IsForbidden("Delete"));
        Assert.True(SfSafety.IsForbidden("מחק"));
    }
}

public class ComposerScopeTests
{
    static AxNode N(string id, string? parent, string role, string name, int backend, bool modal = false, params string[] children) =>
        new(id, parent, role, name, backend, false, false, modal, children);

    /// <summary>A page with a docked "Log a Call" composer and, optionally, a "New Task" modal.</summary>
    static List<AxNode> Page(bool withTaskModal, bool twoCallComposers = false)
    {
        var rootKids = new List<string> { "call" };
        if (twoCallComposers) rootKids.Add("call2");
        if (withTaskModal) rootKids.Add("task");
        var list = new List<AxNode>
        {
            N("root", null, "RootWebArea", "Account", 1, false, rootKids.ToArray()),
            N("call", "root", "region", "Log a Call", 10, false, "callSubject", "callSave"),
            N("callSubject", "call", "textbox", "Subject", 11),
            N("callSave", "call", "button", "Save", 12),
        };
        if (twoCallComposers)
        {
            list.Add(N("call2", "root", "region", "Log a Call", 20, false, "call2Subject"));
            list.Add(N("call2Subject", "call2", "textbox", "Subject", 21));
        }
        if (withTaskModal)
        {
            list.Add(N("task", "root", "dialog", "New Task", 30, true, "taskSubject", "taskSave"));
            list.Add(N("taskSubject", "task", "textbox", "Subject", 31));
            list.Add(N("taskSave", "task", "button", "Save", 32));
        }
        return list;
    }

    [Fact]
    public void Scopes_to_the_composer_matching_the_planned_action()
    {
        var ax = Page(withTaskModal: true);
        var call = ComposerScope.Pick(ax, ComposerScope.HeaderFor("LogCall"));
        Assert.Equal("call", call.Root!.Id);
        Assert.Contains(call.Scope, n => n.Id == "callSubject");
        Assert.DoesNotContain(call.Scope, n => n.Id == "taskSubject");

        var task = ComposerScope.Pick(ax, ComposerScope.HeaderFor("NewTask"));
        Assert.Equal("task", task.Root!.Id);
    }

    [Fact]
    public void Refuses_when_two_composers_match()
    {
        var pick = ComposerScope.Pick(Page(withTaskModal: false, twoCallComposers: true), ComposerScope.HeaderFor("LogCall"));
        Assert.True(pick.Refused);
        Assert.Empty(pick.Scope);
    }

    [Fact]
    public void Invisible_composer_does_not_count()
    {
        var pick = ComposerScope.Pick(Page(withTaskModal: false, twoCallComposers: true), ComposerScope.HeaderFor("LogCall"), n => n.Id != "call2");
        Assert.Equal("call", pick.Root!.Id);
    }

    [Fact]
    public void Refuses_when_several_are_open_and_none_matches()
    {
        var ax = Page(withTaskModal: true);
        var pick = ComposerScope.Pick(ax, "new event");
        Assert.True(pick.Refused);
    }

    [Fact]
    public void Single_unnamed_match_or_no_composer_falls_back()
    {
        var one = Page(withTaskModal: false);
        Assert.Equal("call", ComposerScope.Pick(one, "new event").Root!.Id); // the only composer open
        var none = new List<AxNode> { N("root", null, "RootWebArea", "Account", 1, false, "b"), N("b", "root", "button", "Log a Call", 2) };
        var pick = ComposerScope.Pick(none, ComposerScope.HeaderFor("LogCall"));
        Assert.False(pick.Refused);
        Assert.Null(pick.Root);
        Assert.Equal(2, pick.Scope.Count);
    }

    [Fact]
    public void Several_matches_prefer_the_single_modal_on_top()
    {
        var ax = Page(withTaskModal: true);
        // A header pattern that matches both: the modal is topmost and wins.
        var pick = ComposerScope.Pick(ax, "log a call|new task");
        Assert.Equal("task", pick.Root!.Id);
    }

    [Fact]
    public void Only_typing_and_saving_steps_are_scoped()
    {
        var logCall = new SfSkill { Skill = "LogCall" };
        Assert.False(SkillRunner.InComposer(logCall, new SfStep { Method = "click", Locators = { new SfLocator { Role = "button", Name = "Log a Call" } } }));
        Assert.True(SkillRunner.InComposer(logCall, new SfStep { Method = "fill" }));
        Assert.True(SkillRunner.InComposer(logCall, new SfStep { Method = "click", Locators = { new SfLocator { Role = "button", Name = "Save" } } }));
        Assert.False(SkillRunner.InComposer(new SfSkill { Skill = "FindRecordByPhone" }, new SfStep { Method = "fill" }));
    }
}
