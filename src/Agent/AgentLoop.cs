using System.Text.Json;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>The loop's verdict: a spoken/shown answer, or an action whose
/// toast is the whole feedback (a window opened, music paused).</summary>
sealed record AgentOutcome(bool Acted, string Text);

/// <summary>Why the loop produced no outcome: the provider chain is unusable
/// (worth retrying on the v7 single-shot path), the model burning through
/// its rounds without an answer (another round-trip won't help), or the
/// user cancelling (say nothing).</summary>
enum AgentFailure
{
    None,
    ProviderDown,
    RoundCap,
    Cancelled,
}

/// <summary>A finished loop: the outcome, or why there is none.</summary>
sealed record AgentRunResult(AgentOutcome? Outcome, AgentFailure Failure);

/// <summary>
/// The Ask-Palon agent loop: system context + short history + the question
/// go to the model with the tool specs; tool calls are executed and their
/// results fed back until the model produces a final answer (or a terminal
/// tool ends the turn). When the round budget runs out, one last call with
/// tools forbidden asks for the best answer from what was gathered. A
/// ProviderDown result means the model path is unusable — the caller
/// degrades to the v7 single-shot JSON intent.
/// </summary>
static class AgentLoop
{
    const int MaxRounds = 5;
    const int MaxToolCallsPerRound = 4;
    internal const int MaxToolResultChars = 4000;

    const string FinalAnswerNudge =
        "You are out of tool calls for this question. Answer now, from what the tool results " +
        "above already gave you — no more tool calls. If something is still missing, say so " +
        "briefly.";

    public static async Task<AgentRunResult> RunAsync(
        AssistantSession session, string question, CancellationToken ct,
        IReadOnlyList<AgentTool>? tools = null,
        Func<IReadOnlyList<object>, object[], Task<ChatTurn?>>? chat = null)
    {
        tools ??= ToolRegistry.CreateDefault();
        var toolsSpec = ToolRegistry.ToToolsSpec(tools);
        if (chat is null)
        {
            // One conversation per run: the provider that answers first is
            // pinned for every later round. An empty tools array is the
            // loop's final-answer turn — tools stay declared, calls forbidden.
            var conversation = new AiChat.ToolConversation();
            chat = (messages, spec) => spec.Length == 0
                ? conversation.ChatAsync(messages, toolsSpec, 0.2, 700, ct, toolChoiceNone: true)
                : conversation.ChatAsync(messages, spec, 0.2, 700, ct);
        }

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

        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                var turn = await chat(messages, toolsSpec);
                ct.ThrowIfCancellationRequested();
                if (turn is null) return new AgentRunResult(null, AgentFailure.ProviderDown);

                if (turn.ToolCalls.Count == 0)
                {
                    var answer = (turn.Content ?? "").Trim();
                    if (answer.Length == 0) return new AgentRunResult(null, AgentFailure.ProviderDown);
                    session.Record(question, answer);
                    return new AgentRunResult(new AgentOutcome(Acted: false, answer), AgentFailure.None);
                }

                messages.Add(AssistantMessage(turn));

                // Every tool_call_id gets a tool message — OpenAI-format APIs
                // reject an assistant tool_calls entry left unanswered.
                for (var i = 0; i < turn.ToolCalls.Count; i++)
                {
                    var call = turn.ToolCalls[i];
                    if (i >= MaxToolCallsPerRound)
                    {
                        messages.Add(ToolMessage(call.Id, SkippedResult));
                        continue;
                    }
                    var outcome = await ExecuteAsync(tools, call, ct);
                    ct.ThrowIfCancellationRequested();
                    Log.Write($"Palon tool {call.Name}: {(outcome.EndTurn ? "done (end turn)" : outcome.ResultForModel)}");
                    if (outcome.EndTurn)
                    {
                        var text = outcome.Toast ?? outcome.ResultForModel;
                        session.Record(question, text);
                        return new AgentRunResult(new AgentOutcome(Acted: true, text), AgentFailure.None);
                    }
                    messages.Add(ToolMessage(call.Id, CapToolResult(outcome.ResultForModel)));
                }
            }

            // Out of rounds: one last call with tools forbidden, so the user
            // gets the best answer from what was gathered rather than a shrug.
            Log.Write($"Palon agent: no final answer within {MaxRounds} rounds — asking for one without tools");
            messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = FinalAnswerNudge });
            var final = await chat(messages, Array.Empty<object>());
            ct.ThrowIfCancellationRequested();
            var finalAnswer = (final?.Content ?? "").Trim();
            if (final is not null && final.ToolCalls.Count == 0 && finalAnswer.Length > 0)
            {
                session.Record(question, finalAnswer);
                return new AgentRunResult(new AgentOutcome(Acted: false, finalAnswer), AgentFailure.None);
            }
            return new AgentRunResult(null, AgentFailure.RoundCap);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Write("Palon agent: cancelled");
            return new AgentRunResult(null, AgentFailure.Cancelled);
        }
    }

    internal const string SkippedResult =
        "Skipped: at most 4 tool calls per round. Call it again next round if still needed.";

    /// <summary>The assistant message echoing a tool-calling turn. Each
    /// call's provider payload (Gemini's thought_signature under
    /// extra_content) goes back verbatim — Gemini 3.x 400s without it.</summary>
    internal static Dictionary<string, object?> AssistantMessage(ChatTurn turn) => new()
    {
        ["role"] = "assistant",
        ["content"] = turn.Content,
        ["tool_calls"] = turn.ToolCalls.Select(call =>
        {
            var entry = new Dictionary<string, object?>
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            };
            if (call.ExtraContent is { } extra) entry["extra_content"] = extra;
            return (object)entry;
        }).ToArray(),
    };

    internal static Dictionary<string, object?> ToolMessage(string toolCallId, string content) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = toolCallId,
        ["content"] = content,
    };

    /// <summary>Bounds a tool result for small models and voice latency:
    /// head and tail kept, the middle replaced by a marker naming the cut.</summary>
    internal static string CapToolResult(string result, int max = MaxToolResultChars)
    {
        if (result.Length <= max) return result;
        var cut = result.Length - max;
        var marker = $"\n[… truncated {cut} chars …]\n";
        var head = max * 3 / 4;
        var tail = max - head;
        return result[..head] + marker + result[^tail..];
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
        // Per-tool budget: a hung tool (a COM media call, a slow disk) must
        // not hold the turn hostage. WhenAny covers tools that ignore the token.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(tool.Timeout);
        try
        {
            var run = tool.ExecuteAsync(args, budget.Token);
            var winner = await Task.WhenAny(run, Task.Delay(Timeout.Infinite, budget.Token));
            if (winner == run) return await run;
            ct.ThrowIfCancellationRequested();
            _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            Log.Write($"Tool {call.Name} timed out after {tool.Timeout.TotalSeconds:0}s");
            return new ToolOutcome($"The tool timed out after {tool.Timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Write($"Tool {call.Name} timed out after {tool.Timeout.TotalSeconds:0}s");
            return new ToolOutcome($"The tool timed out after {tool.Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            Log.Write($"Tool {call.Name} failed: {ex}");
            return new ToolOutcome($"The tool failed: {ex.Message}");
        }
    }
}
