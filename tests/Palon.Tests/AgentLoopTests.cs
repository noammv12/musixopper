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
        var result = await AgentLoop.RunAsync(session, "מה השעה?", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script(new ChatTurn("שתיים בצהריים.", Array.Empty<ToolCallRequest>())));

        Assert.NotNull(result.Outcome);
        Assert.False(result.Outcome!.Acted);
        Assert.Equal("שתיים בצהריים.", result.Outcome.Text);
        Assert.Equal(AgentFailure.None, result.Failure);
        Assert.Single(session.Exchanges);
    }

    [Fact]
    public async Task Tool_result_feeds_the_next_round()
    {
        var tool = new FakeDataTool();
        var result = await AgentLoop.RunAsync(NewSession(), "what is the data?", CancellationToken.None,
            new AgentTool[] { tool },
            Script(
                new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") }),
                new ChatTurn("It is 42.", Array.Empty<ToolCallRequest>())));

        Assert.Equal(1, tool.Calls);
        Assert.NotNull(result.Outcome);
        Assert.Equal("It is 42.", result.Outcome!.Text);
    }

    [Fact]
    public async Task Terminal_tool_ends_the_turn_without_another_round()
    {
        var result = await AgentLoop.RunAsync(NewSession(), "open it", CancellationToken.None,
            new AgentTool[] { new FakeTerminalTool() },
            Script(new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_open", "{}") })));

        Assert.NotNull(result.Outcome);
        Assert.True(result.Outcome!.Acted);
        Assert.Equal("Opening it", result.Outcome.Text);
    }

    [Fact]
    public async Task Unknown_tool_and_bad_json_degrade_to_tool_errors_not_crashes()
    {
        var result = await AgentLoop.RunAsync(NewSession(), "hm", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script(
                new ChatTurn(null, new[]
                {
                    new ToolCallRequest("1", "no_such_tool", "{}"),
                    new ToolCallRequest("2", "fake_data", "not json"),
                }),
                new ChatTurn("recovered", Array.Empty<ToolCallRequest>())));

        Assert.NotNull(result.Outcome);
        Assert.Equal("recovered", result.Outcome!.Text);
    }

    [Fact]
    public async Task Provider_failure_reads_as_ProviderDown_for_the_legacy_fallback()
    {
        var result = await AgentLoop.RunAsync(NewSession(), "hi", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script((ChatTurn?)null));
        Assert.Null(result.Outcome);
        Assert.Equal(AgentFailure.ProviderDown, result.Failure);
    }

    [Fact]
    public async Task Round_cap_asks_once_more_without_tools_for_a_real_answer()
    {
        var tool = new FakeDataTool();
        var loopForever = new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") });
        var specs = new List<int>();
        var turns = new[] { loopForever, loopForever, loopForever, loopForever, loopForever,
            new ChatTurn("Best guess: 42.", Array.Empty<ToolCallRequest>()) };
        var i = 0;
        var result = await AgentLoop.RunAsync(NewSession(), "loop", CancellationToken.None,
            new AgentTool[] { tool },
            (_, spec) => { specs.Add(spec.Length); return Task.FromResult<ChatTurn?>(turns[i++]); });

        Assert.Equal(5, tool.Calls); // MaxRounds
        Assert.Equal(AgentFailure.None, result.Failure);
        Assert.Equal("Best guess: 42.", result.Outcome!.Text);
        Assert.Equal(0, specs[^1]); // the final turn offers no tools
    }

    [Fact]
    public async Task Round_cap_with_no_usable_final_answer_reads_as_RoundCap()
    {
        var loopForever = new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") });
        var result = await AgentLoop.RunAsync(NewSession(), "loop", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            Script(loopForever, loopForever, loopForever, loopForever, loopForever, loopForever));

        Assert.Null(result.Outcome);
        Assert.Equal(AgentFailure.RoundCap, result.Failure);
    }

    [Fact]
    public async Task Thought_signature_round_trips_verbatim_in_the_next_request()
    {
        var extra = JsonDocument.Parse("""{"google":{"thought_signature":"c2lnLTEyMw=="}}""").RootElement.Clone();
        string? secondRequest = null;
        var calls = 0;
        await AgentLoop.RunAsync(NewSession(), "data?", CancellationToken.None,
            new AgentTool[] { new FakeDataTool() },
            (messages, _) =>
            {
                if (calls++ == 0)
                    return Task.FromResult<ChatTurn?>(new ChatTurn(null,
                        new[] { new ToolCallRequest("fc_1", "fake_data", "{}", extra) }));
                secondRequest = JsonSerializer.Serialize(messages);
                return Task.FromResult<ChatTurn?>(new ChatTurn("42", Array.Empty<ToolCallRequest>()));
            });

        using var doc = JsonDocument.Parse(secondRequest!);
        var assistant = doc.RootElement.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("c2lnLTEyMw==",
            call.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
    }

    [Fact]
    public void Calls_without_provider_extras_carry_no_extra_content_key()
    {
        var message = AgentLoop.AssistantMessage(
            new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") }));
        var json = JsonSerializer.Serialize(message);
        Assert.DoesNotContain("extra_content", json);
    }

    [Fact]
    public void Stripping_provider_extras_for_a_takeover_leaves_the_original_intact()
    {
        var extra = JsonDocument.Parse("""{"google":{"thought_signature":"x"}}""").RootElement.Clone();
        var original = new List<object>
        {
            new Dictionary<string, object?> { ["role"] = "user", ["content"] = "q" },
            AgentLoop.AssistantMessage(new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}", extra) })),
            AgentLoop.ToolMessage("1", "r"),
        };
        var stripped = AiChat.StripProviderExtras(original);

        Assert.DoesNotContain("extra_content", JsonSerializer.Serialize(stripped));
        Assert.Contains("\"tool_call_id\":\"1\"", JsonSerializer.Serialize(stripped));
        Assert.Contains("extra_content", JsonSerializer.Serialize(original));
    }

    [Fact]
    public async Task Every_tool_call_gets_a_result_even_past_the_per_round_cap()
    {
        var tool = new FakeDataTool();
        string? secondRequest = null;
        var calls = 0;
        var six = Enumerable.Range(1, 6).Select(n => new ToolCallRequest($"c{n}", "fake_data", "{}")).ToArray();
        await AgentLoop.RunAsync(NewSession(), "lots", CancellationToken.None,
            new AgentTool[] { tool },
            (messages, _) =>
            {
                if (calls++ == 0) return Task.FromResult<ChatTurn?>(new ChatTurn(null, six));
                secondRequest = JsonSerializer.Serialize(messages);
                return Task.FromResult<ChatTurn?>(new ChatTurn("done", Array.Empty<ToolCallRequest>()));
            });

        Assert.Equal(4, tool.Calls); // MaxToolCallsPerRound actually run
        using var doc = JsonDocument.Parse(secondRequest!);
        var toolMessages = doc.RootElement.EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        Assert.Equal(six.Select(c => c.Id), toolMessages.Select(m => m.GetProperty("tool_call_id").GetString()));
        Assert.StartsWith("Skipped", toolMessages[5].GetProperty("content").GetString());
    }

    [Fact]
    public void Tool_results_are_capped_with_a_truncation_marker()
    {
        Assert.Equal("short", AgentLoop.CapToolResult("short"));
        var big = "HEAD" + new string('x', 10_000) + "TAIL";
        var capped = AgentLoop.CapToolResult(big);
        Assert.StartsWith("HEAD", capped);
        Assert.EndsWith("TAIL", capped);
        Assert.Contains($"truncated {big.Length - AgentLoop.MaxToolResultChars} chars", capped);
        Assert.True(capped.Length < AgentLoop.MaxToolResultChars + 100);
    }

    sealed class HangingTool : AgentTool
    {
        public override string Name => "hang";
        public override string Description => "never returns";
        public override string ParametersJson => """{"type":"object","properties":{}}""";
        public override TimeSpan Timeout => TimeSpan.FromMilliseconds(100);
        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct) =>
            new TaskCompletionSource<ToolOutcome>().Task; // ignores the token on purpose
    }

    [Fact]
    public async Task A_hung_tool_times_out_into_a_tool_result()
    {
        string? secondRequest = null;
        var calls = 0;
        var result = await AgentLoop.RunAsync(NewSession(), "hang", CancellationToken.None,
            new AgentTool[] { new HangingTool() },
            (messages, _) =>
            {
                if (calls++ == 0)
                    return Task.FromResult<ChatTurn?>(new ChatTurn(null, new[] { new ToolCallRequest("1", "hang", "{}") }));
                secondRequest = JsonSerializer.Serialize(messages);
                return Task.FromResult<ChatTurn?>(new ChatTurn("sorry", Array.Empty<ToolCallRequest>()));
            });

        Assert.Equal("sorry", result.Outcome!.Text);
        Assert.Contains("timed out", secondRequest);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_and_reads_as_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        var tool = new FakeDataTool();
        var result = await AgentLoop.RunAsync(NewSession(), "q", cts.Token,
            new AgentTool[] { tool },
            (_, _) =>
            {
                cts.Cancel(); // user hit cancel while the request was in flight
                return Task.FromResult<ChatTurn?>(new ChatTurn(null, new[] { new ToolCallRequest("1", "fake_data", "{}") }));
            });

        Assert.Null(result.Outcome);
        Assert.Equal(AgentFailure.Cancelled, result.Failure);
        Assert.Equal(0, tool.Calls);
    }
}
