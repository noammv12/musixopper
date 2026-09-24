using System.Text.Json;
using Palon.Notes;
using Palon.Salesforce;
using Xunit;

namespace Palon.Tests;

public class SalesforcePhoneTests
{
    [Theory]
    [InlineData("050-1234567")]
    [InlineData("+972 50 123 4567")]
    [InlineData("00972501234567")]
    public void SearchTerms_LocalThenInternationalThenLast7(string raw) =>
        Assert.Equal(new[] { "0501234567", "+972501234567", "1234567" }, SfPhones.SearchTerms(raw));

    [Fact]
    public void SearchTerms_ForeignNumberHasNoLocalForm() =>
        Assert.Equal(new[] { "+14155550123", "5550123" }, SfPhones.SearchTerms("+1 415 555 0123"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    public void SearchTerms_UnusableIsEmpty(string? raw) => Assert.Empty(SfPhones.SearchTerms(raw));

    [Fact]
    public void ParseRecordUrl_ReadsIdAndObject()
    {
        var r = SfPhones.ParseRecordUrl("https://acme.lightning.force.com/lightning/r/Contact/0035g00000AbCdEAAV/view?x=1");
        Assert.Equal(("0035g00000AbCdEAAV", "Contact"), (r!.Value.Id, r.Value.ObjectType));
        Assert.Equal("Lead", SfPhones.ParseRecordUrl("/lightning/r/00Q5g00000AbCdE/view")!.Value.ObjectType);
        Assert.Null(SfPhones.ParseRecordUrl("https://acme.lightning.force.com/lightning/page/home"));
    }

    [Fact]
    public void SameId_Compares15CharPrefix()
    {
        Assert.True(SfPhones.SameId("0035g00000AbCdEAAV", "0035g00000AbCdE"));
        Assert.False(SfPhones.SameId("0035g00000AbCdE", "0035g00000AbCdF"));
        Assert.False(SfPhones.SameId(null, "0035g00000AbCdE"));
    }

    [Fact]
    public void ParseSearchResults_KeepsContactsAndLeads_Dedupes()
    {
        var links = new[]
        {
            ("/lightning/r/Contact/0035g00000AbCdEAAV/view", ""),
            ("/lightning/r/0035g00000AbCdEAAV/view", "Dana Cohen"),
            ("/lightning/r/Account/0015g00000AbCdEAAV/view", "Acme"),
            ("/lightning/r/00Q5g00000ZzZzZAAV/view", "  Avi   Levi "),
        };
        var r = SfPhones.ParseSearchResults(links, "https://acme.lightning.force.com/");
        Assert.Equal(2, r.Count);
        Assert.Equal("Dana Cohen", r[0].Name);
        Assert.Equal("Contact", r[0].ObjectType);
        Assert.Equal("Avi Levi", r[1].Name);
        Assert.Equal("https://acme.lightning.force.com/lightning/r/Lead/00Q5g00000ZzZzZAAV/view", r[1].Url);
    }

    [Fact]
    public void SearchUrl_IsOneAppWithBase64Term()
    {
        var url = SfPhones.SearchUrl("https://acme.lightning.force.com", "0501234567");
        var b64 = url[(url.IndexOf('#') + 1)..];
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("0501234567", json);
        Assert.StartsWith("https://acme.lightning.force.com/one/one.app#", url);
    }
}

public class SalesforcePlanTests
{
    static readonly DateTime Now = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);
    static readonly SfRecordRef Dana = new("0035g00000AbCdEAAV", "Contact", "Dana", "https://x.lightning.force.com/lightning/r/Contact/0035g00000AbCdEAAV/view");
    static readonly SfRecordRef Avi = new("00Q5g00000ZzZzZAAV", "Lead", "Avi", "u");

    static CallNote Note(string? summary = "Wants the gold plan", CallbackProposal? cb = null) =>
        new("n1", Now.AddHours(-1), 300, summary, "transcript text", "ok", "0501234567", null, cb);

    [Fact]
    public void Build_LogCallOnly_WhenNoCallback()
    {
        var p = SfPlanner.Build(Note(), null, null, new[] { Dana }, Now);
        Assert.Same(Dana, p.Record);
        var c = Assert.Single(p.Changes);
        Assert.Equal(SfChangeKind.LogCall, c.Kind);
        Assert.Equal("Wants the gold plan", c.Comments);
        Assert.True(p.Ready);
    }

    [Fact]
    public void Build_SeveralCandidates_LeavesChoiceToUser()
    {
        var p = SfPlanner.Build(Note(), null, null, new[] { Dana, Avi }, Now);
        Assert.Null(p.Record);
        Assert.False(p.Ready);
        Assert.True(p.WithRecord(Avi).Ready);
    }

    [Fact]
    public void Build_ProposedCallback_AddsFollowUpTask()
    {
        var cb = new CallbackProposal(Now.AddDays(1), "מחר", "send the contract");
        var p = SfPlanner.Build(Note(cb: cb), null, Dana, Array.Empty<SfRecordRef>(), Now);
        var task = p.Changes.Single(c => c.Kind == SfChangeKind.NewTask);
        Assert.Contains("send the contract", task.Subject);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(1).ToLocalTime()), task.Date);
    }

    [Fact]
    public void Build_DismissedOrPastCallback_NoTask()
    {
        var dismissed = new CallbackProposal(Now.AddDays(1), "מחר", State: "dismissed");
        Assert.Single(SfPlanner.Build(Note(cb: dismissed), null, Dana, Array.Empty<SfRecordRef>(), Now).Changes);
        Assert.Single(SfPlanner.Build(Note(), Now.AddDays(-2).ToLocalTime(), Dana, Array.Empty<SfRecordRef>(), Now).Changes);
    }

    [Fact]
    public void Build_ExplicitCallbackWins()
    {
        var when = Now.AddDays(3).ToLocalTime();
        var cb = new CallbackProposal(Now.AddDays(1), "מחר");
        var p = SfPlanner.Build(Note(cb: cb), when, Dana, Array.Empty<SfRecordRef>(), Now);
        Assert.Equal(DateOnly.FromDateTime(when), p.Changes[1].Date);
    }

    [Fact]
    public void Build_StatusOption_AddsFieldChange()
    {
        var p = SfPlanner.Build(Note(), null, Dana, Array.Empty<SfRecordRef>(), Now, new SfPlanOptions(StatusField: "Status", StatusValue: "Working"));
        var f = p.Changes.Single(c => c.Kind == SfChangeKind.UpdateField);
        Assert.Equal(("Status", "Working"), (f.Field, f.NewValue));
    }

    [Fact]
    public void Comments_FallsBackToTranscript_AndCaps()
    {
        Assert.StartsWith("תמליל", SfPlanner.Comments(Note(summary: null), 1000));
        var capped = SfPlanner.Comments(Note(summary: new string('x', 50)), 10);
        Assert.Equal(10, capped.Length);
        Assert.EndsWith("…", capped);
    }

    [Fact]
    public void Hash_BindsRecordAndContent_NotOldValue()
    {
        var p = SfPlanner.Build(Note(), null, Dana, Array.Empty<SfRecordRef>(), Now);
        Assert.Equal(p.Hash, (p with { CreatedUtc = Now.AddHours(1) }).Hash);
        Assert.NotEqual(p.Hash, p.WithRecord(Avi).Hash);
        Assert.NotEqual(p.Hash, p.WithChanges(new[] { p.Changes[0] with { Comments = "other" } }).Hash);
        Assert.Equal(p.Hash, p.WithChanges(new[] { p.Changes[0] with { OldValue = "was" } }).Hash);
    }
}

public class ApprovalGateTests
{
    static SfPlan Plan(string comments = "c") => new("n", "050", new SfRecordRef("0035g00000AbCdE", "Contact", "D", "u"),
        Array.Empty<SfRecordRef>(), new[] { new SfChange(SfChangeKind.LogCall, "s", comments, new DateOnly(2026, 9, 24)) }, DateTime.UtcNow);

    [Fact]
    public void Token_IsSingleUse()
    {
        var gate = new ApprovalGate();
        var p = Plan();
        var t = gate.Issue(p);
        Assert.True(gate.TryConsume(p, t, out _));
        Assert.False(gate.TryConsume(p, t, out _));
    }

    [Fact]
    public void Token_BoundToPlanHash_AndBurnedOnMismatch()
    {
        var gate = new ApprovalGate();
        var p = Plan();
        var t = gate.Issue(p);
        Assert.False(gate.TryConsume(Plan("edited after approval"), t, out var why));
        Assert.NotEmpty(why);
        Assert.False(gate.TryConsume(p, t, out _));
    }

    [Fact]
    public void Token_Expires()
    {
        var now = DateTime.UtcNow;
        var gate = new ApprovalGate(() => now);
        var p = Plan();
        var t = gate.Issue(p);
        now += ApprovalGate.Lifetime + TimeSpan.FromSeconds(1);
        Assert.False(gate.TryConsume(p, t, out _));
    }

    [Fact]
    public void NoToken_NoRecord_Refused()
    {
        var gate = new ApprovalGate();
        Assert.False(gate.TryConsume(Plan(), null, out _));
        Assert.False(gate.TryConsume(Plan(), "made-up", out _));
        Assert.Throws<InvalidOperationException>(() => gate.Issue(Plan() with { Record = null }));
    }

    [Theory]
    [InlineData("Delete", true)]
    [InlineData("Mass Update", true)]
    [InlineData("Convert", true)]
    [InlineData("מחק", true)]
    [InlineData("Save", false)]
    [InlineData("Log a Call", false)]
    public void Forbidden(string name, bool forbidden) => Assert.Equal(forbidden, SfSafety.IsForbidden(name));

    [Theory]
    [InlineData("Save", true)]
    [InlineData("שמור", true)]
    [InlineData("Save & New", true)]
    [InlineData("Save as Draft Template", false)]
    [InlineData("Log a Call", false)]
    public void Commit(string name, bool commit) => Assert.Equal(commit, SfSafety.LooksLikeCommit(name));

    [Fact]
    public void LoginUrls()
    {
        Assert.True(SfSafety.IsLoginUrl("https://login.salesforce.com/?ec=302"));
        Assert.True(SfSafety.IsLoginUrl("https://acme.my.salesforce.com/_ui/identity/verification/method/x"));
        Assert.False(SfSafety.IsLoginUrl("https://acme.lightning.force.com/lightning/r/Contact/003/view"));
    }
}

public class SkillTests
{
    [Fact]
    public void Defaults_RoundTripThroughJson()
    {
        foreach (var name in DefaultSkills.Names)
        {
            var s = DefaultSkills.Get(name);
            var back = SfSkill.FromJson(s.ToJson());
            Assert.NotNull(back);
            Assert.Equal(s.Steps.Count, back!.Steps.Count);
            Assert.Equal(s.Steps.Select(x => x.Locators.Count), back.Steps.Select(x => x.Locators.Count));
            Assert.Contains(back.Steps, x => x.Commit || name == "FindRecordByPhone");
        }
    }

    [Fact]
    public void Defaults_OneCommitStep_LastStep()
    {
        foreach (var name in new[] { "LogCall", "NewTask", "UpdateField" })
        {
            var s = DefaultSkills.Get(name);
            Assert.Single(s.Steps, x => x.Commit);
            Assert.True(s.Steps[^1].Commit);
        }
    }

    [Fact]
    public void FromJson_RejectsUnknownMethod_AndDropsUnsafeCss()
    {
        Assert.Null(SfSkill.FromJson("""{"skill":"X","steps":[{"method":"eval","locators":[{"role":"button","name":"a"}]}]}"""));
        Assert.Null(SfSkill.FromJson("not json"));
        Assert.Null(SfSkill.FromJson("""{"skill":"X","steps":[{"method":"click"}]}"""));
        var s = SfSkill.FromJson("""{"skill":"X","steps":[{"method":"click","locators":[{"css":"#input-123"},{"role":"button","name":"Save"}]}]}""");
        Assert.Single(s!.Steps[0].Locators);
    }

    [Fact]
    public void JsonShape_MatchesDocFormat()
    {
        var json = DefaultSkills.Get("LogCall").ToJson();
        using var doc = JsonDocument.Parse(json);
        var step = doc.RootElement.GetProperty("steps")[1];
        Assert.Equal("click", step.GetProperty("method").GetString());
        Assert.Equal("button", step.GetProperty("locators")[0].GetProperty("role").GetString());
        Assert.False(step.GetProperty("locators")[0].TryGetProperty("key", out _));
        Assert.Contains("שיחה", json); // Hebrew stays readable in the file
    }

    [Fact]
    public void Vars_Substitute_AndDetectMissing()
    {
        var vars = new Dictionary<string, string> { ["field"] = "Status" };
        Assert.Equal("Edit Status", SfVars.Apply("Edit %field%", vars));
        Assert.True(SfVars.HasUnresolved(SfVars.Apply("%value%", vars)));
        Assert.Equal("Edit Status", SfVars.Apply(new SfLocator { Role = "button", Name = "Edit %field%" }, vars).Name);
    }

    [Theory]
    [InlineData("24/09/2026", "dd/MM/yyyy")]
    [InlineData("24.9.2026", "d.M.yyyy")]
    [InlineData("2026-09-24", "yyyy-MM-dd")]
    public void InferDateFormat(string sample, string format) =>
        Assert.Equal(format, SfVars.InferDateFormat(sample, new DateOnly(2026, 9, 24)));

    [Fact]
    public void InferDateFormat_KnownDateDisambiguatesMonthFirst() =>
        Assert.Equal("MM/dd/yyyy", SfVars.InferDateFormat("09/10/2026", new DateOnly(2026, 9, 10)));

    [Fact]
    public void FormatDate() => Assert.Equal("05/03/2026", SfVars.FormatDate(new DateOnly(2026, 3, 5), "dd/MM/yyyy"));
}

public class LocatorRankingTests
{
    [Fact]
    public void Derive_FieldForInputs_RoleNameForButtons()
    {
        Assert.Equal("Task.Subject", LocatorRanking.Derive(new ElementFacts("combobox", "Subject", null, "Task.Subject"))!.FieldApiName);
        var b = LocatorRanking.Derive(new ElementFacts("button", "Log  a Call", null, "Task.X"))!;
        Assert.Equal(("button", "Log a Call"), (b.Role, b.Name));
        Assert.Equal("Close", LocatorRanking.Derive(new ElementFacts("generic", "", "Close", null))!.AriaLabel);
        Assert.Null(LocatorRanking.Derive(new ElementFacts("generic", "", null, null)));
    }

    [Fact]
    public void Promote_PutsLearnedFirst_Dedupes_Caps_KeepsField()
    {
        var field = new SfLocator { FieldApiName = "Task.Subject" };
        var existing = new List<SfLocator>
        {
            new() { Role = "combobox", Name = "Subject" },
            new() { Role = "combobox", Name = "נושא" },
            new() { AriaLabel = "Subject" },
            field,
        };
        var learned = new SfLocator { Role = "combobox", Name = "Betreff" };
        var r = LocatorRanking.Promote(existing, learned);
        Assert.Equal(LocatorRanking.MaxPerStep, r.Count);
        Assert.Equal(learned, r[0]);
        Assert.Contains(field, r);
        var again = LocatorRanking.Promote(r, learned);
        Assert.Equal(r.Count, again.Count);
    }

    [Theory]
    [InlineData("Subject", "Subject", true)]
    [InlineData("*Subject", "Subject", true)]
    [InlineData("Subject *Required", "Subject", true)]
    [InlineData("subject", "Subject", true)]
    [InlineData("Subject line", "Subject", false)]
    public void NameMatches(string actual, string wanted, bool ok) =>
        Assert.Equal(ok, LocatorRanking.NameMatches(new SfLocator { Role = "x", Name = wanted }, actual));

    [Fact]
    public void Stability_Order()
    {
        Assert.True(LocatorRanking.Stability(new SfLocator { FieldApiName = "a" }) > LocatorRanking.Stability(new SfLocator { Role = "button", Name = "a" }));
        Assert.True(LocatorRanking.Stability(new SfLocator { Role = "button", Name = "a" }) > LocatorRanking.Stability(new SfLocator { AriaLabel = "a" }));
        Assert.False(LocatorRanking.IsSafeCss("div > span:nth-child(2)"));
        Assert.True(LocatorRanking.IsSafeCss("[data-target-selection-name$='LogACall']"));
    }
}

public class AxTreeTests
{
    const string Sample = """
    {"nodes":[
      {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Dana | Salesforce"},"childIds":["2","3"],"backendDOMNodeId":1},
      {"nodeId":"2","parentId":"1","role":{"value":"button"},"name":{"value":"Save"},"childIds":[],"backendDOMNodeId":10},
      {"nodeId":"3","parentId":"1","role":{"value":"dialog"},"name":{"value":"Log a Call"},"properties":[{"name":"modal","value":{"type":"boolean","value":true}}],"childIds":["4","5","6","7"],"backendDOMNodeId":11},
      {"nodeId":"4","parentId":"3","role":{"value":"combobox"},"name":{"value":"Subject"},"value":{"value":"Call"},"childIds":[],"backendDOMNodeId":14},
      {"nodeId":"5","parentId":"3","role":{"value":"textbox"},"name":{"value":"Comments"},"childIds":[],"backendDOMNodeId":15},
      {"nodeId":"6","parentId":"3","role":{"value":"button"},"name":{"value":"Save"},"childIds":[],"backendDOMNodeId":20},
      {"nodeId":"7","parentId":"3","role":{"value":"button"},"name":{"value":"Delete"},"ignored":true,"properties":[{"name":"disabled","value":{"type":"boolean","value":true}}],"childIds":[],"backendDOMNodeId":21}
    ]}
    """;

    static List<AxNode> Nodes() => AxTree.Parse(JsonDocument.Parse(Sample).RootElement);

    [Fact]
    public void Parse_ReadsRolesNamesProps()
    {
        var n = Nodes();
        Assert.Equal(7, n.Count);
        Assert.True(n.Single(x => x.Id == "3").Modal);
        Assert.True(n.Single(x => x.Id == "7").Disabled);
        Assert.Equal("Call", n.Single(x => x.Id == "4").Value);
    }

    [Fact]
    public void Scope_OpenDialogWins_SoSaveIsUnique()
    {
        var all = Nodes();
        Assert.Equal(2, AxTree.Match(all, new SfLocator { Role = "button", Name = "Save" }).Count);
        var scoped = AxTree.Scope(all);
        Assert.Equal(20, Assert.Single(AxTree.Match(scoped, new SfLocator { Role = "button", Name = "Save" })).BackendId);
    }

    [Fact]
    public void Snapshot_PlaywrightStyleWithRefs()
    {
        var snap = AxTree.Snapshot(AxTree.Scope(Nodes()), fresh: new HashSet<int> { 15 });
        Assert.Equal(
            "- dialog \"Log a Call\"\n" +
            "  - combobox \"Subject\" value=\"Call\" [ref=e14]\n" +
            "  - *textbox \"Comments\" [ref=e15]\n" +
            "  - button \"Save\" [ref=e20]\n", snap);
        Assert.DoesNotContain("Delete", snap); // ignored nodes never offered
    }

    [Fact]
    public void Snapshot_Truncates()
    {
        var snap = AxTree.Snapshot(Nodes(), maxChars: 60);
        Assert.EndsWith("- … (truncated)\n", snap);
    }

    [Fact]
    public void AnyNameContains_SearchesNamesAndValues()
    {
        Assert.True(AxTree.AnyNameContains(Nodes(), "comments"));
        Assert.True(AxTree.AnyNameContains(Nodes(), "Call"));
        Assert.False(AxTree.AnyNameContains(Nodes(), "Delete")); // ignored
    }

    [Fact]
    public void Recovery_ParsesOnlyOfferedRefs()
    {
        var snap = AxTree.Snapshot(AxTree.Scope(Nodes()));
        Assert.Equal(20, RecoveryPrompt.Parse("{\"ref\":\"e20\"}", snap));
        Assert.Equal(15, RecoveryPrompt.Parse("```json\n{ \"ref\": \"e15\" }\n```", snap));
        Assert.Null(RecoveryPrompt.Parse("{\"ref\":null}", snap));
        Assert.Null(RecoveryPrompt.Parse("{\"ref\":\"e10\"}", snap)); // exists on page but not offered in scope
        Assert.Null(RecoveryPrompt.Parse("{\"ref\":\"e999\"}", snap));
        Assert.Null(RecoveryPrompt.Parse(null, snap));
        Assert.Contains("Intent: save", RecoveryPrompt.User("save", "click", snap, null));
    }
}

public class LoopDetectorTests
{
    [Fact]
    public void Nudges_Escalate_At5_8_12()
    {
        var d = new LoopDetector();
        var h = LoopDetector.ActionHash("click", 20, null);
        for (var i = 0; i < 4; i++) d.RecordAction(h);
        Assert.Null(d.Nudge());
        d.RecordAction(h);
        Assert.Contains("consider", d.Nudge());
        for (var i = 0; i < 3; i++) d.RecordAction(h);
        Assert.Contains("different element", d.Nudge());
        for (var i = 0; i < 4; i++) d.RecordAction(h);
        Assert.Contains("not working", d.Nudge());
    }

    [Fact]
    public void InputHash_NormalizesText() =>
        Assert.Equal(LoopDetector.ActionHash("fill", 3, " Hello  World"), LoopDetector.ActionHash("fill", 3, "hello world"));

    [Fact]
    public void Stagnation_After5IdenticalPages()
    {
        var d = new LoopDetector();
        for (var i = 0; i < 4; i++) d.RecordPage("p");
        Assert.False(d.Stagnant);
        d.RecordPage("p");
        Assert.True(d.Stagnant);
        Assert.Contains("not changed", d.Nudge());
        d.RecordPage("q");
        Assert.False(d.Stagnant);
    }

    [Fact]
    public void RecoveryBudget_IsTwoPerJob()
    {
        var d = new LoopDetector();
        Assert.True(d.TryUseRecovery());
        Assert.True(d.TryUseRecovery());
        Assert.False(d.TryUseRecovery());
    }

    [Fact]
    public void Window_Is20()
    {
        var d = new LoopDetector();
        for (var i = 0; i < 5; i++) d.RecordAction("a");
        for (var i = 0; i < 20; i++) d.RecordAction("b" + i);
        d.RecordAction("a");
        Assert.Equal(1, d.RepeatsOfLast);
    }
}

public class TeachMergeTests
{
    static TeachObservation Click(string role, string name, string? field = null) => new("click", new ElementFacts(role, name, null, field), null, "u");
    static TeachObservation Fill(string role, string name, string value, string? field = null) => new("fill", new ElementFacts(role, name, null, field, "INPUT", value), value, "u");

    [Fact]
    public void LogCall_HebrewUi_LearnsEveryStep()
    {
        var obs = new[]
        {
            Click("tab", "פעילות"),
            Click("button", "רישום שיחה"),
            Click("combobox", "נושא", "Task.Subject"),                         // focus click, dropped
            Fill("combobox", "נושא", TeachMerge.SubjectSentinel, "Task.Subject"),
            Fill("textbox", "תגובות", "a " + TeachMerge.CommentsSentinel),      // no field api-name: sentinel maps it
            Fill("textbox", "תאריך", "24/09/2026", "Task.ActivityDate"),
            Click("button", "שמור"),
        };
        var outcome = TeachMerge.Merge(DefaultSkills.Get("LogCall"), obs, new DateOnly(2026, 9, 24));
        var s = outcome.Skill;
        Assert.Empty(outcome.Unmatched);
        Assert.Equal(6, outcome.Learned.Count);
        Assert.Equal("פעילות", s.Steps[0].Locators[0].Name);
        Assert.Equal("רישום שיחה", s.Steps[1].Locators[0].Name);
        Assert.Equal("Task.Subject", s.Steps[2].Locators[0].FieldApiName);
        Assert.Equal("תגובות", s.Steps[3].Locators[0].Name);
        Assert.Equal("dd/MM/yyyy", s.DateFormat);
        Assert.Equal("שמור", s.Steps[5].Locators[0].Name);
        Assert.All(s.Steps, st => Assert.True(st.Locators.Count <= LocatorRanking.MaxPerStep));
        Assert.Equal(2, s.Version);
        Assert.NotNull(s.TaughtAt);
    }

    [Fact]
    public void ClickWithoutActivityTab_GoesToLogACall_NotTheOptionalStep()
    {
        var s = TeachMerge.Merge(DefaultSkills.Get("LogCall"), new[] { Click("button", "Log Call") }).Skill;
        Assert.Equal("Log Call", s.Steps[1].Locators[0].Name);
        Assert.Equal("tab", s.Steps[0].Locators[0].Role);
    }

    [Fact]
    public void FindByPhone_DigitsMapToTerm()
    {
        var obs = new[] { Click("button", "Search..."), Fill("searchbox", "Search Salesforce", "050 123 4567") };
        var s = TeachMerge.Merge(DefaultSkills.Get("FindRecordByPhone"), obs).Skill;
        Assert.Equal("Search Salesforce", s.Steps[1].Locators[0].Name);
    }

    [Fact]
    public void Unknown_IsReported_NotGuessed()
    {
        var outcome = TeachMerge.Merge(DefaultSkills.Get("LogCall"), new[] { Fill("textbox", "Weird", "hello") });
        Assert.Empty(outcome.Learned);
        Assert.Single(outcome.Unmatched);
        Assert.Equal(1, outcome.Skill.Version);
    }
}
