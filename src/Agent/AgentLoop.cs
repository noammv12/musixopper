using System.Text.Json;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>The loop's verdict: a spoken/shown answer, or an action whose
/// toast is the whole feedback (a window opened, music paused).</summary>
sealed record AgentOutcome(bool Acted, string Text)
{
    /// <summary>Undo for the last Reversible tool of the run (the Undo toast), or null.</summary>
    public Func<Task<string>>? Undo { get; init; }
}

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

    public static Task<AgentRunResult> RunAsync(
        AssistantSession session, string question, CancellationToken ct,
        IReadOnlyList<AgentTool>? tools = null,
        Func<IReadOnlyList<object>, object[], Task<ChatTurn?>>? chat = null) =>
        RunAsync(session, question, ct, new AgentRunOptions { Plan = false }, tools, chat);

    /// <summary>
    /// The full loop: permission tiers (External → approve card, declined →
    /// "User declined."), progress chips, the loop guard, an optional plan
    /// turn with ReadOnly tools only, and streamed answer text.
    /// </summary>
    public static async Task<AgentRunResult> RunAsync(
        AssistantSession session, string question, CancellationToken ct,
        AgentRunOptions options,
        IReadOnlyList<AgentTool>? tools = null,
        Func<IReadOnlyList<object>, object[], Task<ChatTurn?>>? chat = null)
    {
        tools ??= ToolRegistry.CreateDefault();
        var toolsSpec = ToolRegistry.ToToolsSpec(tools);
        if (chat is null && options.StreamingChat is null)
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

        var guard = new ToolCallLoopGuard();
        var digests = new List<string>();
        Func<Task<string>>? undo = null;
        SentenceChunker? chunker = null;
        string? acted = null;

        try
        {
            // ---- plan turn: ReadOnly tools only, then the approve card --------------------
            if (options.Plan ?? PlanStep.Needs(question, tools))
            {
                var readOnly = tools.Where(t => t.Risk == ToolRisk.ReadOnly).ToList();
                var planMessages = new List<object>(messages)
                {
                    new Dictionary<string, object?> { ["role"] = "system", ["content"] = PlanStep.PlanInstruction },
                };
                var (planText, planFailure) = await RoundsAsync(planMessages, readOnly, emit: false);
                if (planText is null) return new AgentRunResult(null, planFailure);
                const string planLabel = "מציג תוכנית לאישור";
                options.Steps?.Report(new AgentStep("plan", planLabel, AgentStepState.Started));
                var approved = await options.Approver.ApprovePlanAsync(planText, ct);
                ct.ThrowIfCancellationRequested();
                options.Steps?.Report(new AgentStep("plan", planLabel, approved ? AgentStepState.Done : AgentStepState.Declined));
                if (!approved)
                {
                    session.Record(question, PlanStep.DeclinedAnswer);
                    return new AgentRunResult(new AgentOutcome(Acted: false, PlanStep.DeclinedAnswer), AgentFailure.None);
                }
                messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = planText });
                messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = PlanStep.ActInstruction });
            }

            // ---- act ------------------------------------------------------------------------
            if (options.OnText is { } onText) chunker = new SentenceChunker(onText);
            var (text, failure) = await RoundsAsync(messages, tools, emit: true);
            if (text is null) return new AgentRunResult(null, failure);
            if (acted is not null)
            {
                session.Record(question, acted, digests);
                return new AgentRunResult(new AgentOutcome(Acted: true, acted) { Undo = undo }, AgentFailure.None);
            }
            if (chunker is not null)
            {
                // Streamed rounds already fed the chunker; a plain round feeds it now.
                if (options.StreamingChat is null) chunker.Feed(text);
                chunker.Flush();
            }
            session.Record(question, text, digests);
            return new AgentRunResult(new AgentOutcome(Acted: false, text) { Undo = undo }, AgentFailure.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Write("Palon agent: cancelled");
            return new AgentRunResult(null, AgentFailure.Cancelled);
        }

        // A streaming round feeds text as it arrives; a plain round's text is
        // fed once the answer is known (the chunker still splits it for TTS).
        Task<ChatTurn?> Chat(IReadOnlyList<object> msgs, object[] spec, bool emit)
        {
            if (options.StreamingChat is { } streaming)
                return streaming(msgs, spec, delta => { if (emit) chunker?.Feed(delta); });
            return chat!(msgs, spec);
        }

        // Rounds until a final answer: (text, None), or (null, failure). A
        // terminal (EndTurn) tool sets `acted` and returns its toast as text.
        async Task<(string? Text, AgentFailure Failure)> RoundsAsync(List<object> msgs, IReadOnlyList<AgentTool> available, bool emit)
        {
            acted = null;
            var spec = available.Count == tools.Count ? toolsSpec : ToolRegistry.ToToolsSpec(available);
            for (var round = 0; round < MaxRounds && !guard.Tripped; round++)
            {
                ct.ThrowIfCancellationRequested();
                // A plan turn with no read-only tools still declares tools (an empty
                // array means "final answer" to the chat delegate), so fall back to all.
                var turn = await Chat(msgs, spec.Length == 0 ? toolsSpec : spec, emit);
                ct.ThrowIfCancellationRequested();
                if (turn is null) return (null, AgentFailure.ProviderDown);

                if (turn.ToolCalls.Count == 0)
                {
                    var answer = (turn.Content ?? "").Trim();
                    return answer.Length == 0 ? (null, AgentFailure.ProviderDown) : (answer, AgentFailure.None);
                }

                msgs.Add(AssistantMessage(turn));

                // Every tool_call_id gets a tool message — OpenAI-format APIs
                // reject an assistant tool_calls entry left unanswered.
                for (var i = 0; i < turn.ToolCalls.Count; i++)
                {
                    var call = turn.ToolCalls[i];
                    if (i >= MaxToolCallsPerRound)
                    {
                        msgs.Add(ToolMessage(call.Id, SkippedResult));
                        continue;
                    }
                    if (guard.TryGetRepeat(call.Name, call.ArgumentsJson, out var cached))
                    {
                        msgs.Add(ToolMessage(call.Id, ToolCallLoopGuard.RepeatPrefix + CapToolResult(cached)));
                        continue;
                    }
                    if (guard.Tripped)
                    {
                        msgs.Add(ToolMessage(call.Id, TooManyErrorsResult));
                        continue;
                    }
                    var tool = available.FirstOrDefault(t => t.Name == call.Name);
                    var label = tool is null ? call.Name : SafeLabel(tool, call.ArgumentsJson);
                    options.Steps?.Report(new AgentStep(call.Name, label, AgentStepState.Started));
                    if (tool is { Risk: ToolRisk.External })
                    {
                        var ok = await options.Approver.ApproveToolAsync(tool, call.ArgumentsJson, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!ok)
                        {
                            options.Steps?.Report(new AgentStep(call.Name, label, AgentStepState.Declined));
                            Log.Write($"Palon tool {call.Name}: declined by the user");
                            msgs.Add(ToolMessage(call.Id, DeclinedResult));
                            continue;
                        }
                    }
                    var outcome = await ExecuteAsync(available, call, ct);
                    ct.ThrowIfCancellationRequested();
                    var failed = ToolCallLoopGuard.LooksFailed(outcome.ResultForModel);
                    guard.Record(call.Name, call.ArgumentsJson, outcome.ResultForModel, failed);
                    options.Steps?.Report(new AgentStep(call.Name, label, failed ? AgentStepState.Failed : AgentStepState.Done));
                    if (outcome.Undo is not null) undo = outcome.Undo;
                    digests.Add(Digest(call.Name, outcome.Toast ?? outcome.ResultForModel));
                    Log.Write($"Palon tool {call.Name}: {(outcome.EndTurn ? "done (end turn)" : outcome.ResultForModel)}");
                    if (outcome.EndTurn)
                    {
                        acted = outcome.Toast ?? outcome.ResultForModel;
                        return (acted, AgentFailure.None);
                    }
                    msgs.Add(ToolMessage(call.Id, CapToolResult(outcome.ResultForModel)));
                }
            }

            // Out of rounds (or two tool errors in a row): one last call with tools
            // forbidden, so the user gets the best answer from what was gathered.
            Log.Write("Palon agent: no final answer within the round budget — asking for one without tools");
            msgs.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = FinalAnswerNudge });
            var final = await Chat(msgs, Array.Empty<object>(), emit);
            ct.ThrowIfCancellationRequested();
            var finalAnswer = (final?.Content ?? "").Trim();
            if (final is not null && final.ToolCalls.Count == 0 && finalAnswer.Length > 0)
                return (finalAnswer, AgentFailure.None);
            return (null, AgentFailure.RoundCap);
        }
    }

    internal const string DeclinedResult = "User declined.";

    internal const string TooManyErrorsResult =
        "Skipped: the last tool calls failed twice in a row. Answer from what you have.";

    static string SafeLabel(AgentTool tool, string? argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return tool.ProgressLabel(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return ToolLabels.For(tool.Name);
        }
    }

    /// <summary>"create_reminder → Reminder "Dani" set for Thu 17:00" — the
    /// one-line memory of a tool call the next turn can refer back to.</summary>
    internal static string Digest(string tool, string result)
    {
        var line = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();
        if (line.Length > 80) line = line[..80] + "…";
        return $"{tool} → {line}";
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
