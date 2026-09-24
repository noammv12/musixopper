using System.Text.Json;
using Palon;
using Palon.Agent;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class AgentPolicyTests
{
    static AssistantSession NewSession() => new(() => CallState.Idle, () => null);

    sealed class Probe : AgentTool
    {
        readonly string _name;
        readonly ToolRisk _risk;
        readonly Func<ToolOutcome>? _run;
        public int Calls;

        public Probe(string name, ToolRisk risk, Func<ToolOutcome>? run = null)
        {
            _name = name;
            _risk = risk;
            _run = run;
        }

        public override string Name => _name;
        public override string Description => _name;
        public override string ParametersJson => """{"type":"object","properties":{}}""";
        public override ToolRisk Risk => _risk;

        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_run?.Invoke() ?? new ToolOutcome($"{_name} ok"));
        }
    }

    sealed class Approver : IAgentApprover
    {
        public bool Tools;
        public bool Plans;
        public int ToolAsks;
        public string? PlanShown;
        public Task<bool> ApproveToolAsync(AgentTool tool, string argumentsJson, CancellationToken ct) { ToolAsks++; return Task.FromResult(Tools); }
        public Task<bool> ApprovePlanAsync(string plan, CancellationToken ct) { PlanShown = plan; return Task.FromResult(Plans); }
    }

    sealed class StepLog : IProgress<AgentStep>
    {
        public readonly List<AgentStep> Steps = new();
        public void Report(AgentStep value) => Steps.Add(value);
    }

    static ChatTurn Call(string name, string args = "{}", string id = "c1") =>
        new(null, new[] { new ToolCallRequest(id, name, args) });

    static ChatTurn Text(string text) => new(text, Array.Empty<ToolCallRequest>());

    /// <summary>Scripted model that also records which tool specs each round saw.</summary>
    sealed class Script
    {
        readonly ChatTurn?[] _turns;
        int _i;
        public readonly List<string[]> SeenTools = new();
        public readonly List<IReadOnlyList<object>> SeenMessages = new();

        public Script(params ChatTurn?[] turns) => _turns = turns;

        public Task<ChatTurn?> Chat(IReadOnlyList<object> messages, object[] spec)
        {
            SeenMessages.Add(messages.ToList());
            SeenTools.Add(spec.Select(s => JsonSerializer.Serialize(s)).Select(j =>
                JsonDocument.Parse(j).RootElement.GetProperty("function").GetProperty("name").GetString()!).ToArray());
            return Task.FromResult(_i < _turns.Length ? _turns[_i++] : null);
        }
    }

    // ---- permission tiers ------------------------------------------------------------

    [Fact]
    public void Builtin_tools_have_expected_tiers()
    {
        Assert.Equal(ToolRisk.ReadOnly, ToolRisks.For("search_notes"));
        Assert.Equal(ToolRisk.ReadOnly, ToolRisks.For("list_reminders"));
        Assert.Equal(ToolRisk.Reversible, ToolRisks.For("create_reminder"));
        Assert.Equal(ToolRisk.Reversible, ToolRisks.For("some_new_tool"));
    }

    [Fact]
    public async Task External_tool_declined_does_not_run_and_model_hears_user_declined()
    {
        var send = new Probe("send_message", ToolRisk.External);
        var approver = new Approver { Tools = false };
        var steps = new StepLog();
        var script = new Script(Call("send_message"), Text("בסדר, לא שלחתי."));
        var r = await AgentLoop.RunAsync(NewSession(), "שלח", CancellationToken.None,
            new AgentRunOptions { Approver = approver, Steps = steps, Plan = false }, new AgentTool[] { send }, script.Chat);
        Assert.Equal(0, send.Calls);
        Assert.Equal(1, approver.ToolAsks);
        Assert.Equal("בסדר, לא שלחתי.", r.Outcome!.Text);
        Assert.Contains(script.SeenMessages[1], m => m is Dictionary<string, object?> d && (d["content"] as string) == AgentLoop.DeclinedResult);
        Assert.Contains(steps.Steps, s => s.State == AgentStepState.Declined);
    }

    [Fact]
    public async Task External_tool_approved_runs()
    {
        var send = new Probe("send_message", ToolRisk.External);
        var script = new Script(Call("send_message"), Text("נשלח."));
        await AgentLoop.RunAsync(NewSession(), "שלח", CancellationToken.None,
            new AgentRunOptions { Approver = new Approver { Tools = true }, Plan = false }, new AgentTool[] { send }, script.Chat);
        Assert.Equal(1, send.Calls);
    }

    [Fact]
    public async Task External_tool_without_approver_is_declined_by_default()
    {
        var send = new Probe("send_message", ToolRisk.External);
        var script = new Script(Call("send_message"), Text("x"));
        await AgentLoop.RunAsync(NewSession(), "שלח", CancellationToken.None, new Probe[] { send }, script.Chat);
        Assert.Equal(0, send.Calls);
    }

    [Fact]
    public async Task ReadOnly_and_Reversible_run_without_asking_and_undo_surfaces()
    {
        var undone = false;
        var create = new Probe("create_reminder", ToolRisk.Reversible,
            () => new ToolOutcome("set") { Undo = () => { undone = true; return Task.FromResult("בוטל"); } });
        var approver = new Approver();
        var steps = new StepLog();
        var script = new Script(Call("create_reminder"), Text("קבעתי."));
        var r = await AgentLoop.RunAsync(NewSession(), "תזכיר לי", CancellationToken.None,
            new AgentRunOptions { Approver = approver, Steps = steps, Plan = false }, new AgentTool[] { create }, script.Chat);
        Assert.Equal(0, approver.ToolAsks);
        Assert.NotNull(r.Outcome!.Undo);
        await r.Outcome.Undo!();
        Assert.True(undone);
        Assert.Equal(new[] { AgentStepState.Started, AgentStepState.Done }, steps.Steps.Select(s => s.State));
        Assert.Equal("קובע חזרה", steps.Steps[0].Label);
    }

    // ---- loop detector ---------------------------------------------------------------

    [Fact]
    public void Loop_hash_ignores_key_order_and_whitespace()
    {
        Assert.Equal(ToolCallLoopGuard.Hash("t", """{"a":1,"b":"x"}"""), ToolCallLoopGuard.Hash("t", """{ "b":" x ", "a":1 }"""));
        Assert.NotEqual(ToolCallLoopGuard.Hash("t", """{"a":1}"""), ToolCallLoopGuard.Hash("u", """{"a":1}"""));
    }

    [Fact]
    public async Task Repeated_call_returns_cached_result_without_running_again()
    {
        var search = new Probe("search_notes", ToolRisk.ReadOnly);
        var script = new Script(Call("search_notes", """{"q":"דני"}"""), Call("search_notes", """{ "q": "דני" }""", "c2"), Text("done"));
        await AgentLoop.RunAsync(NewSession(), "q", CancellationToken.None, new Probe[] { search }, script.Chat);
        Assert.Equal(1, search.Calls);
        Assert.Contains(script.SeenMessages[2], m => m is Dictionary<string, object?> d && (d["content"] as string ?? "").StartsWith(ToolCallLoopGuard.RepeatPrefix));
    }

    [Fact]
    public async Task Two_consecutive_errors_stop_the_tool_phase()
    {
        var broken = new Probe("broken", ToolRisk.ReadOnly, () => throw new InvalidOperationException("boom"));
        var script = new Script(Call("broken", """{"n":1}"""), Call("broken", """{"n":2}""", "c2"), Text("sorry"));
        var r = await AgentLoop.RunAsync(NewSession(), "q", CancellationToken.None, new Probe[] { broken }, script.Chat);
        Assert.Equal(2, broken.Calls);
        Assert.Equal("sorry", r.Outcome!.Text);
        Assert.Empty(script.SeenTools[2]); // the third call is the no-tools final answer
    }

    // ---- plan step -------------------------------------------------------------------

    [Fact]
    public void Plan_needed_for_multi_step_or_risky_requests_only()
    {
        var tools = new AgentTool[] { new Probe("x", ToolRisk.Reversible) };
        Assert.True(PlanStep.Needs("תתעד את השיחה ותקבע חזרה למחר", tools));
        Assert.True(PlanStep.Needs("log this call and set a follow-up", tools));
        Assert.False(PlanStep.Needs("תזכיר לי להתקשר לדני בחמש", tools));
        Assert.False(PlanStep.Needs("מה הבירה של צרפת", tools));
        Assert.False(PlanStep.Needs("תעדכן בסיילספורס", tools)); // risky, but nothing External to do it with
        Assert.True(PlanStep.Needs("תעדכן בסיילספורס", new AgentTool[] { new Probe("sf", ToolRisk.External) }));
    }

    [Fact]
    public async Task Plan_turn_exposes_only_readonly_tools_and_waits_for_approval()
    {
        var search = new Probe("search_notes", ToolRisk.ReadOnly);
        var create = new Probe("create_reminder", ToolRisk.Reversible);
        var approver = new Approver { Plans = true };
        var script = new Script(Text("1. create_reminder דני מחר 17:00"), Call("create_reminder"), Text("קבעתי."));
        var r = await AgentLoop.RunAsync(NewSession(), "תמצא את דני ותקבע לו חזרה", CancellationToken.None,
            new AgentRunOptions { Approver = approver }, new AgentTool[] { search, create }, script.Chat);
        Assert.Equal(new[] { "search_notes" }, script.SeenTools[0]);
        Assert.Equal("1. create_reminder דני מחר 17:00", approver.PlanShown);
        Assert.Contains("create_reminder", script.SeenTools[1]);
        Assert.Equal(1, create.Calls);
        Assert.Equal("קבעתי.", r.Outcome!.Text);
    }

    [Fact]
    public async Task Declined_plan_acts_on_nothing()
    {
        var create = new Probe("create_reminder", ToolRisk.Reversible);
        var script = new Script(Text("1. create_reminder"), Call("create_reminder"));
        var r = await AgentLoop.RunAsync(NewSession(), "q", CancellationToken.None,
            new AgentRunOptions { Approver = new Approver { Plans = false }, Plan = true }, new AgentTool[] { create }, script.Chat);
        Assert.Equal(0, create.Calls);
        Assert.Equal(PlanStep.DeclinedAnswer, r.Outcome!.Text);
    }

    // ---- streaming + session ---------------------------------------------------------

    [Fact]
    public void Chunker_emits_whole_sentences_only()
    {
        var got = new List<string>();
        var c = new SentenceChunker(got.Add);
        c.Feed("שלום. מה ");
        Assert.Equal(new[] { "שלום." }, got);
        c.Feed("שלומך? טוב");
        Assert.Equal(new[] { "שלום.", "מה שלומך?" }, got);
        c.Flush();
        Assert.Equal("טוב", got[^1]);
    }

    [Fact]
    public async Task Streaming_chat_feeds_text_before_the_run_ends()
    {
        var got = new List<string>();
        Func<IReadOnlyList<object>, object[], Action<string>, Task<ChatTurn?>> stream = (_, _, onDelta) =>
        {
            onDelta("הבירה היא ");
            onDelta("פריז. זהו");
            return Task.FromResult<ChatTurn?>(Text("הבירה היא פריז. זהו"));
        };
        var r = await AgentLoop.RunAsync(NewSession(), "q", CancellationToken.None,
            new AgentRunOptions { StreamingChat = stream, OnText = got.Add, Plan = false }, Array.Empty<AgentTool>());
        Assert.Equal(new[] { "הבירה היא פריז.", "זהו" }, got);
        Assert.Equal("הבירה היא פריז. זהו", r.Outcome!.Text);
    }

    [Fact]
    public async Task Session_keeps_a_tool_digest_for_the_next_turn()
    {
        var session = NewSession();
        var create = new Probe("create_reminder", ToolRisk.Reversible, () => new ToolOutcome("Reminder \"Dani\" set for Thu 17:00."));
        var script = new Script(Call("create_reminder"), Text("קבעתי."));
        await AgentLoop.RunAsync(session, "q", CancellationToken.None, new AgentTool[] { create }, script.Chat);
        Assert.Contains("create_reminder → Reminder \"Dani\"", session.Exchanges[^1].Answer);
    }

    [Fact]
    public void Session_clip_never_cuts_mid_word()
    {
        var text = string.Join(" ", Enumerable.Repeat("מילה", 200));
        var clipped = AssistantSession.Clip(text);
        Assert.EndsWith("מילה…", clipped);
    }

    // ---- eval harness ----------------------------------------------------------------

    /// <summary>A keyword router standing in for the model: enough to prove the
    /// harness scores tool choice end to end, offline.</summary>
    static Task<ChatTurn?> FakeModel(IReadOnlyList<object> messages, object[] spec)
    {
        var last = messages.OfType<Dictionary<string, object?>>().Last();
        if ((string?)last["role"] == "tool") return Task.FromResult<ChatTurn?>(Text("ok"));
        var q = (string)last["content"]!;
        string? tool = q switch
        {
            _ when q.Contains("תזכיר") || q.Contains("תזקיר") || q.Contains("חזרה ל") => "create_reminder",
            _ when q.Contains("לחזור") || q.Contains("צלחת") => "list_reminders",
            _ when q.Contains("אמר") || q.Contains("סיכמנו") => "search_notes",
            _ when q.Contains("כמה שיחות") => "get_call_stats",
            _ when q.Contains("תסכם") => "daily_recap",
            _ when q.Contains("מוזיקה") || q.Contains("שיר") => "control_music",
            _ when q.Contains("וואטסאפ") => "open_whatsapp",
            _ when q.Contains("גוגל") => "open_url",
            _ => null,
        };
        return Task.FromResult<ChatTurn?>(tool is null ? Text("תשובה") : Call(tool));
    }

    static readonly string[] Names =
        { "create_reminder", "list_reminders", "search_notes", "get_call_stats", "daily_recap", "control_music", "open_whatsapp", "open_url" };

    [Fact]
    public async Task Eval_set_passes_with_a_correct_fake_model()
    {
        var report = await AgentEval.RunAsync(NewSession, FakeModel, toolNames: Names);
        Assert.True(report.Accuracy == 1.0, report.Summary());
        Assert.True(AgentEval.Hebrew.Count >= 12);
    }

    [Fact]
    public async Task Eval_catches_a_wrong_tool_choice()
    {
        Task<ChatTurn?> Wrong(IReadOnlyList<object> m, object[] s) =>
            ((string?)m.OfType<Dictionary<string, object?>>().Last()["role"]) == "tool"
                ? Task.FromResult<ChatTurn?>(Text("ok"))
                : Task.FromResult<ChatTurn?>(Call("search_notes"));
        var report = await AgentEval.RunAsync(NewSession, Wrong, toolNames: Names);
        Assert.True(report.Accuracy < 0.5);
        Assert.Contains("expected create_reminder, got search_notes", report.Summary());
    }
}
