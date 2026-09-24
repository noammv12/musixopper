# Palon: Ask-Palon agent loop — gaps and upgrade plan

Status: 2026-09-24. I read Palon's `src/Agent/AgentLoop.cs`, `AssistantSession.cs`, `AgentTool.cs`, `ToolRegistry.cs`, `Tools.cs`, `src/Assistant.cs`, and `src/Notes/AiChat.cs` (Tools.cs and AiChat.cs only partly). References come from source clones under `research/repos/`: **block/goose** (Rust, Apache-2.0), **browser-use** (MIT), **letta-code** (Apache-2.0), **LangMem** (MIT), and Cline docs (Apache-2.0). Goose is the most relevant of these, because it is a desktop assistant with tool-permission modes.

## 1. What Palon has today

- **Voice assistant flow.** STT (speech-to-text) produces the question, and `AgentLoop.RunAsync` handles it. The loop allows at most **3 rounds** and **4 tool calls per round**, runs tools sequentially, and uses `temperature 0.2` and `maxTokens 700`.
- **Messages** use the OpenAI chat format and go through a provider chain: Gemini (OpenAI-compatible endpoint) first, then DeepSeek. Slow providers get a cooldown bench. The interactive timeout is 15 s, with one retry after 500 ms.
- **Session memory** keeps up to 4 exchanges with a 3-minute TTL. Answers are clipped at 600 chars, and tool calls and results are *not* kept in history.
- **System prompt** is rebuilt every turn: persona, time, call state, the caller's last note, today's stats, and saved commands.
- **Tools** can set `EndTurn`, which makes a toast the whole reply ("Opening WhatsApp"). Unknown tools, bad JSON and exceptions become tool-result strings.
- **Fallback:** `ProviderDown` degrades to the legacy single-shot JSON intent (`AiChat.AssistAsync`).

This is a clean, small design. The gaps are below.

## 2. Gaps (ordered by severity)

1. **Gemini 3.x thought signatures are dropped. This is a likely production bug.** `ToolCallRequest(Id, Name, ArgumentsJson)` (`AiChat.cs:17`) discards `tool_calls[].extra_content.google.thought_signature`. Gemini 3.x requires that field to be echoed back on the next request, and returns **400 "Function call is missing a thought_signature"** when it is missing. Many OpenAI-compatible clients hit this: see microsoft/vscode#296713, openai-agents-python#2137, and the Google AI forum thread "OpenAI API compatibility broken due to thought_signature". The effect in Palon is that every round-2 call to Gemini fails and falls through to DeepSeek, so tool use works only by accident. **Fix:** keep `extra_content` (raw `JsonElement`) on each tool call and write it back verbatim in the assistant message.
2. **Dropped tool calls break the protocol.** `turn.ToolCalls.Take(MaxToolCallsPerRound)`: when the model emits more than 4 calls, the extra calls get **no `tool` message**. OpenAI-format APIs reject an assistant `tool_calls` entry without a matching `tool_call_id` response. Every call needs a result, even if that result is "skipped: limit 4 per round".
3. **Provider switch in the middle of a loop.** Round 1 on Gemini and round 2 on DeepSeek sends Gemini-specific ids and signatures to DeepSeek. **Pin the provider for the duration of one run.** If it fails mid-run, restart the whole run on the next provider from the original messages; don't splice.
4. **Cancel doesn't reach the loop.** `Assistant.cs:184` passes `CancellationToken.None`, so `Assistant.Cancel()` stops audio but the HTTP requests and tools keep running, and their toasts can still fire later. Pass a linked CTS from `Cancel()`.
5. **A 3-round cap with no final answer.** On `RoundCap` the user gets "couldn't work that one out". Goose and browser-use both make one last call *without tools* that asks for the best answer from what was gathered. browser-use calls this `final_response_after_failure=True` (`agent/views.py:87`).
6. **No confirmation tier.** All tools run unconditionally. That is fine for today's tools, but it becomes a blocker the moment tools write to Salesforce, send WhatsApp messages or delete reminders.
7. **Session memory drops tool context.** The next turn doesn't know which reminder was just created, so "move it to 5" fails. Also, 600-char clipping cuts the answer mid-word.
8. **No streaming.** The user waits for the full answer before TTS starts. For voice, time-to-first-audio matters most.
9. **No per-tool timeout.** A hung tool (for example a COM media call) blocks the turn until the provider-level timeout, or forever.
10. **Tool results are not size-bounded.** `search_notes` can return long transcripts, and nothing caps the length.
11. **No step visibility.** Only "Thinking…" is shown. The user can't see "Searching notes…" or "Creating reminder…".
12. **No evals.** Nothing catches regressions in tool choice on Hebrew STT noise.

## 3. Patterns to port (from source)

### 3.1 Permission modes + tool annotations (goose)
Source: `crates/goose/src/permission/permission_inspector.rs:150-195`, `config/permission.rs`.
- Modes: `Auto`, `Approve`, `SmartApprove`, `Chat` (no tools). SmartApprove works like this:
  - The user's per-tool setting wins (`AlwaysAllow`/`AskBefore`/`NeverAllow`).
  - Otherwise, a tool annotated **read-only** is allowed.
  - Otherwise, an LLM judge classifies the call (`permission_judge.rs`, which returns `read_only_request_ids`).
  - Otherwise, the call requires approval.
- **Palon version:** add `ToolRisk { ReadOnly, Reversible, External }` to `AgentTool`.
  - ReadOnly (list_reminders, search_notes, stats) runs freely.
  - Reversible (create_reminder, open_url, music) runs and shows an **Undo** toast.
  - External (Salesforce save, sending a message) always needs the approve card, which the model can't bypass. Skip the LLM judge; the tool author's annotation is enough.
- A denial comes back to the model as a tool result, `"User declined."`, so it can respond gracefully (goose `Permission::DenyOnce`).

### 3.2 Large tool output → file / truncate (goose)
`agents/large_response_handler.rs`: text over `GOOSE_MAX_TOOL_RESPONSE_SIZE` (default 200k chars) is written to a temp file, and the model receives "The response ... was larger (N characters) and is stored in the file ...". For Palon's small models and voice latency, use **4k chars** per tool result: head and tail plus "[truncated N chars; call again with offset]". Put the cap in `AgentLoop`, not in each tool.

### 3.3 Context compaction (goose + LangMem)
`crates/goose/src/context_mgmt/mod.rs`: when token use exceeds `threshold × context_limit` (default **0.8**), summarize the older messages and continue after a fixed continuation message, for example: *"Your context was compacted. The previous message contains a summary… Do not mention that you read a summary… Continue calling tools as necessary."* Old **tool-call/result pairs** are summarized in batches of 10 (`TOOLCALL_SUMMARIZATION_BATCH_SIZE`). LangMem `short_term/summarization.py` keeps a `RunningSummary` and re-summarizes only the new tail.
- **Palon version:** keep the last 6 exchanges verbatim, including a one-line tool digest per exchange (`[create_reminder → id r_42, Dani, Thu 17:00]`). Keep one rolling summary for older exchanges, updated in the background (debounced, as in LangMem `ReflectionExecutor`). Replace the 3-minute hard reset with a relevance rule: reset when the Assist window closes, or after 30 minutes. At that point, hand the summary to memory extraction (see memory_coaching.md).

### 3.4 Loop / failure control (browser-use)
`browser_use/agent/views.py`: `max_failures=5` (consecutive), `ActionLoopDetector` with a window of 20, and soft nudges at 5, 8 and 12 repeats. It hashes the normalized action (tool name + sorted args) with SHA-256 and keeps the first 12 hex chars. **Palon version:** if the same `(tool, args)` hash repeats inside one run, return the cached result with "you already called this", and stop after 2 consecutive tool errors.

### 3.5 Structured step reasoning (browser-use)
The per-step schema `{evaluation_previous_goal, memory, next_goal, action[]}` (views.py:383) is for long browser tasks. Ask-Palon doesn't need it, but the **Salesforce executor** (computer_use.md) should use it.

### 3.6 Plan vs. act (Cline)
Cline's Plan mode can read and search but cannot modify anything. Act mode executes, and history carries over (`docs/core-workflows/plan-and-act.mdx`). **Palon version:** multi-step or risky requests ("log this call and set a follow-up") get a **plan turn** in which only ReadOnly tools are exposed. The model returns a plan, and the approve card shows it. Then comes an **act turn** with the approved tools only. Simple requests skip planning. This is the same preview→approve→execute→verify loop as in computer_use.md, applied to chat.

### 3.7 Memory tool (letta-code)
One `memory` tool with `str_replace|insert|create|delete` and a required `reason` (`src/tools/descriptions/Memory.md`). Pinned files go into the system prompt. See memory_coaching.md.

### 3.8 Errors vs. empty results (mem0)
mem0 changed from returning `[]` to raising `LLMError` so that callers can tell "provider down" from "nothing found" (`mem0/memory/main.py`, comment near line 960). Palon's `ChatTurn?` null already does this. Keep it, and add the HTTP status so a 429 sends the provider to the cooldown bench but a 400 does not (a 400 is our bug, as with the signatures above).

## 4. Target loop (C# sketch)

```csharp
record ToolCall(string Id, string Name, string Args, JsonElement? ProviderExtra); // keep extra_content!
enum ToolRisk { ReadOnly, Reversible, External }

async Task<AgentRunResult> RunAsync(Session s, string q, IProgress<AgentStep> ui, CancellationToken ct) {
  var provider = chain.PickHealthy();                 // pinned for the whole run
  var msgs = s.BuildMessages(q);                      // system + summary + last 6 exchanges w/ tool digests
  for (int round = 0; round < 5; round++) {
    var turn = await provider.StreamAsync(msgs, tools, onText: tts.Feed, ct); // stream text to TTS
    if (turn.ToolCalls.Count == 0) return Final(turn.Text);
    msgs.Add(AssistantMsg(turn));                     // echo ProviderExtra verbatim
    var results = await Task.WhenAll(turn.ToolCalls.Select(async (c, i) => {
      if (i >= 4) return Result(c, "skipped: max 4 tool calls per round");
      if (loopDetector.Seen(c)) return Result(c, "already called with these args; use the earlier result");
      var tool = registry[c.Name];
      if (tool.Risk == ToolRisk.External && !await approvals.AskAsync(c, ct)) return Result(c, "User declined.");
      ui.Report(new AgentStep(tool.ProgressLabel(c)));             // "מחפש בסיכומים…"
      using var tcs = CancellationTokenSource.CreateLinkedTokenSource(ct); tcs.CancelAfter(tool.Timeout);
      var o = await SafeExecute(tool, c, tcs.Token);
      return Result(c, Truncate(o.ResultForModel, 4000));
    }));                                              // ReadOnly calls in parallel; External ones serialized
    msgs.AddRange(results);
    if (results.Any(r => r.EndTurn)) return Acted(...);
  }
  return await FinalAnswerWithoutTools(msgs, ct);     // never end with "couldn't work it out"
}
```
- **Parallelism:** goose runs extension calls through `join_all`. In Palon, run ReadOnly tools concurrently, but run Reversible and External tools one at a time in model order.
- **Streaming:** use SSE `stream:true` on both providers. Tool-call deltas arrive in pieces, so accumulate `arguments` per index. Send sentence-complete text to TTS as it arrives, and for voice cut the reply at "at most 2 sentences".

## 5. UX
- **Step chips** under the Assist bubble, one per tool, with a Hebrew progress label and a final ✓/✗. They are also logged.
- **Interrupt:** Esc or the hotkey cancels the linked CTS. If the user speaks during TTS, stop TTS and cancel the run (barge-in).
- **Undo** toast for Reversible tools, and an approve card for External ones.
- A "what did you just do?" question answers from the tool digest in the session.

## 6. Prioritized upgrade list

| # | Change | Effort | Why |
|---|---|---|---|
| 1 | Preserve `extra_content`/thought_signature on tool calls | S | Gemini 3.x multi-round tool use currently 400s |
| 2 | Answer every tool_call_id (skipped/declined results) | S | Protocol correctness |
| 3 | Pin the provider per run; restart on failover | S | Stops cross-provider id/signature mixing |
| 4 | Real CancellationToken from `Assistant.Cancel()` + per-tool timeouts | S | No ghost toasts, no hung turns |
| 5 | Final no-tools answer on round cap; raise rounds to 5 | S | Fewer "couldn't work it out" failures |
| 6 | Tool-result cap (4k) in the loop | S | Latency and quota |
| 7 | `ToolRisk` annotations + approve/undo tiers | M | Needed before any Salesforce or messaging tool |
| 8 | Session: tool digests + rolling summary, 30-min window | M | Follow-ups like "move it to 5" |
| 9 | Streaming to TTS | M | Time-to-first-audio |
| 10 | Step chips / progress events | S | Trust and visibility |
| 11 | Repeat-call detector | S | Cheap guard |
| 12 | Plan turn for multi-step/External requests | M | Feeds the Salesforce preview flow |
| 13 | Eval set: 100 Hebrew STT-noisy utterances → expected tool+args, run in CI with a recorded-response fake `chat` (the existing `chat` injection parameter already allows it) | M | Regression safety |

Licenses: goose, letta-code and Cline are Apache-2.0, and browser-use and LangMem are MIT. Porting their logic is fine as long as the notices are kept. No blockers.
