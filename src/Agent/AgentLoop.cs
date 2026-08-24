using System.Text.Json;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>The loop's verdict: a spoken/shown answer, or an action whose
/// toast is the whole feedback (a window opened, music paused).</summary>
sealed record AgentOutcome(bool Acted, string Text);

/// <summary>Why the loop produced no outcome: the provider chain is unusable
/// (worth retrying on the v7 single-shot path) versus the model burning
/// through its rounds without an answer (another round-trip won't help).</summary>
enum AgentFailure
{
    None,
    ProviderDown,
    RoundCap,
}

/// <summary>A finished loop: the outcome, or why there is none.</summary>
sealed record AgentRunResult(AgentOutcome? Outcome, AgentFailure Failure);

/// <summary>
/// The Ask-Palon agent loop: system context + short history + the question
/// go to the model with the tool specs; tool calls are executed and their
/// results fed back until the model produces a final answer (or a terminal
/// tool ends the turn). A ProviderDown result means the model path is
/// unusable — the caller degrades to the v7 single-shot JSON intent.
/// </summary>
static class AgentLoop
{
    const int MaxRounds = 3;
    const int MaxToolCallsPerRound = 4;

    public static async Task<AgentRunResult> RunAsync(
        AssistantSession session, string question, CancellationToken ct,
        IReadOnlyList<AgentTool>? tools = null,
        Func<IReadOnlyList<object>, object[], Task<ChatTurn?>>? chat = null)
    {
        tools ??= ToolRegistry.CreateDefault();
        chat ??= (messages, spec) => AiChat.ToolChatAsync(messages, spec, 0.2, 700, ct);
        var toolsSpec = ToolRegistry.ToToolsSpec(tools);

        var messages = new List<object>
        {
            new Dictionary<string, object?> { ["role"] = "system", ["content"] = session.BuildSystemPrompt() },
        };
        foreach (var (previousQuestion, previousAnswer) in session.Exchanges)
        {
            messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = previousQuestion });
            messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = previousAnswer });
        }
        messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = question });

        for (var round = 0; round < MaxRounds; round++)
        {
            var turn = await chat(messages, toolsSpec);
            if (turn is null) return new AgentRunResult(null, AgentFailure.ProviderDown);

            if (turn.ToolCalls.Count == 0)
            {
                var answer = (turn.Content ?? "").Trim();
                if (answer.Length == 0) return new AgentRunResult(null, AgentFailure.ProviderDown);
                session.Record(question, answer);
                return new AgentRunResult(new AgentOutcome(Acted: false, answer), AgentFailure.None);
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = turn.Content,
                ["tool_calls"] = turn.ToolCalls.Select(call => (object)new Dictionary<string, object?>
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    },
                }).ToArray(),
            });

            foreach (var call in turn.ToolCalls.Take(MaxToolCallsPerRound))
            {
                var outcome = await ExecuteAsync(tools, call, ct);
                Log.Write($"Palon tool {call.Name}: {(outcome.EndTurn ? "done (end turn)" : outcome.ResultForModel)}");
                if (outcome.EndTurn)
                {
                    var text = outcome.Toast ?? outcome.ResultForModel;
                    session.Record(question, text);
                    return new AgentRunResult(new AgentOutcome(Acted: true, text), AgentFailure.None);
                }
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = outcome.ResultForModel,
                });
            }
        }

        Log.Write($"Palon agent: no final answer within {MaxRounds} rounds");
        return new AgentRunResult(null, AgentFailure.RoundCap);
    }

    static async Task<ToolOutcome> ExecuteAsync(
        IReadOnlyList<AgentTool> tools, ToolCallRequest call, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null) return new ToolOutcome($"Unknown tool \"{call.Name}\".");
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new ToolOutcome("The tool arguments were not valid JSON.");
        }
        try
        {
            return await tool.ExecuteAsync(args, ct);
        }
        catch (Exception ex)
        {
            Log.Write($"Tool {call.Name} failed: {ex}");
            return new ToolOutcome($"The tool failed: {ex.Message}");
        }
    }
}
