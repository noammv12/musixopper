using System.Text.Json;
using Palon;
using Palon.Agent;
using Palon.Notes;
using Xunit;

namespace Palon.Tests;

public class AgentLoopTests
{
    static AssistantSession NewSession() => new(() => CallState.Idle, () => null);

    sealed class FakeDataTool : AgentTool
    {
        public int Calls;
        public override string Name => "fake_data";
        public override string Description => "test data tool";
        public override string ParametersJson => """{"type":"object","properties":{}}""";
        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ToolOutcome("the data is 42"));
        }
    }

    sealed class FakeTerminalTool : AgentTool
    {
        public override string Name => "fake_open";
        public override string Description => "test terminal tool";
        public override string ParametersJson => """{"type":"object","properties":{}}""";
        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct) =>
            Task.FromResult(new ToolOutcome("opened", EndTurn: true, Toast: "Opening it"));
    }

    static Func<IReadOnlyList<object>, object[], Task<ChatTurn?>> Script(params ChatTurn?[] turns)
    {
        var i = 0;
        return (_, _) => Task.FromResult(i < turns.Length ? turns[i++] : null);
    }

    [Fact]
    public async Task Plain_answer_comes_back_and_lands_in_session_memory()
    {
        var session = NewSession();
        var outcome = await AgentLoop.RunAsync(session, "מה השעה?", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script(new ChatTurn("שתיים בצהריים.", Array.Empty<ToolCallRequest>())));

        Assert.NotNull(outcome);
        Assert.False(outcome!.Acted);
        Assert.Equal("שתיים בצהריים.", outcome.Text);
        Assert.Single(session.Exchanges);
    }

    [Fact]
    public async Task Tool_result_feeds_the_next_round()
    {
        var tool = new FakeDataTool();
        var outcome = await AgentLoop.RunAsync(NewSession(), "what is the data?", CancellationToken.None,
            new AgentTool[] { tool },
            Script(
                new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") }),
                new ChatTurn("It is 42.", Array.Empty<ToolCallRequest>())));

        Assert.Equal(1, tool.Calls);
        Assert.NotNull(outcome);
        Assert.Equal("It is 42.", outcome!.Text);
    }

    [Fact]
    public async Task Terminal_tool_ends_the_turn_without_another_round()
    {
        var outcome = await AgentLoop.RunAsync(NewSession(), "open it", CancellationToken.None,
            new AgentTool[] { new FakeTerminalTool() },
            Script(new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_open", "{}") })));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Acted);
        Assert.Equal("Opening it", outcome.Text);
    }

    [Fact]
    public async Task Unknown_tool_and_bad_json_degrade_to_tool_errors_not_crashes()
    {
        var outcome = await AgentLoop.RunAsync(NewSession(), "hm", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script(
                new ChatTurn(null, new[]
                {
                    new ToolCallRequest("1", "no_such_tool", "{}"),
                    new ToolCallRequest("2", "fake_data", "not json"),
                }),
                new ChatTurn("recovered", Array.Empty<ToolCallRequest>())));

        Assert.NotNull(outcome);
        Assert.Equal("recovered", outcome!.Text);
    }

    [Fact]
    public async Task Provider_failure_returns_null_for_the_legacy_fallback()
    {
        var outcome = await AgentLoop.RunAsync(NewSession(), "hi", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script((ChatTurn?)null));
        Assert.Null(outcome);
    }

    [Fact]
    public async Task Round_cap_returns_null_instead_of_spinning()
    {
        var tool = new FakeDataTool();
        var loopForever = new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") });
        var outcome = await AgentLoop.RunAsync(NewSession(), "loop", CancellationToken.None,
            new AgentTool[] { tool },
            Script(loopForever, loopForever, loopForever, loopForever, loopForever, loopForever));

        Assert.Null(outcome);
        Assert.Equal(4, tool.Calls); // MaxRounds
    }
}
