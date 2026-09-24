using Palon.Notes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Agent;

/// <summary>
/// What running a tool can do to the world (agent_chat.md §3.1):
/// ReadOnly runs freely (and may run in the plan turn), Reversible runs
/// and offers an Undo, External always needs the user's approval — the
/// model cannot bypass it.
/// </summary>
enum ToolRisk
{
    ReadOnly,
    Reversible,
    External,
}

/// <summary>Default annotations for the built-in tools. Tools added later
/// override <see cref="AgentTool.Risk"/> instead of editing this table.</summary>
static class ToolRisks
{
    static readonly Dictionary<string, ToolRisk> Known = new(StringComparer.Ordinal)
    {
        ["list_reminders"] = ToolRisk.ReadOnly,
        ["search_notes"] = ToolRisk.ReadOnly,
        ["get_call_stats"] = ToolRisk.ReadOnly,
        ["daily_recap"] = ToolRisk.ReadOnly,
        ["create_reminder"] = ToolRisk.Reversible,
        ["open_url"] = ToolRisk.Reversible,
        ["open_command"] = ToolRisk.Reversible,
        ["open_whatsapp"] = ToolRisk.Reversible,
        ["control_music"] = ToolRisk.Reversible,
        ["recall_client"] = ToolRisk.ReadOnly,
        ["remember_about_me"] = ToolRisk.Reversible, // returns an undo
        ["forget_about_me"] = ToolRisk.Reversible,   // returns an undo
        // look_at_screen already shows its own privacy gate (the user marks a
        // region and confirms before anything is captured or sent), so it is
        // ReadOnly here to avoid a second, redundant approval card.
        ["look_at_screen"] = ToolRisk.ReadOnly,
        // Agentic layer: reads and drafts run freely; add_deal writes and returns an undo.
        ["draft_followup"] = ToolRisk.ReadOnly,
        ["summarize_client"] = ToolRisk.ReadOnly,
        ["next_steps_today"] = ToolRisk.ReadOnly,
        ["daily_brief"] = ToolRisk.ReadOnly,
        ["prepare_deal_from_note"] = ToolRisk.ReadOnly,
        ["find_in_notes"] = ToolRisk.ReadOnly,
        ["add_deal"] = ToolRisk.Reversible,
    };

    /// <summary>Unknown tools default to Reversible: they run without a card
    /// (as before), and have no undo unless they return one.</summary>
    public static ToolRisk For(string toolName) =>
        Known.TryGetValue(toolName, out var risk) ? risk : ToolRisk.Reversible;
}

/// <summary>One visible step of a run — a progress chip under the answer.</summary>
enum AgentStepState
{
    Started,
    Done,
    Failed,
    Declined,
}

sealed record AgentStep(string Tool, string Label, AgentStepState State);

/// <summary>Asks the user about a risky call or a plan. Must return false
/// (never throw) when the user declines or closes the card.</summary>
interface IAgentApprover
{
    Task<bool> ApproveToolAsync(AgentTool tool, string argumentsJson, CancellationToken ct);

    Task<bool> ApprovePlanAsync(string plan, CancellationToken ct);
}

/// <summary>No UI attached: External tools and plans are declined.</summary>
sealed class DenyAllApprover : IAgentApprover
{
    public static readonly DenyAllApprover Instance = new();
    public Task<bool> ApproveToolAsync(AgentTool tool, string argumentsJson, CancellationToken ct) => Task.FromResult(false);
    public Task<bool> ApprovePlanAsync(string plan, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// Everything optional about a run. All members default to "behave as the
/// original loop did", so <c>RunAsync(session, q, ct)</c> is unchanged
/// except that External tools are now declined without an approver.
/// </summary>
sealed class AgentRunOptions
{
    /// <summary>Progress chips ("מחפש בהערות…" → ✓).</summary>
    public IProgress<AgentStep>? Steps { get; init; }

    /// <summary>Approve cards for External tools and for plans.</summary>
    public IAgentApprover Approver { get; init; } = DenyAllApprover.Instance;

    /// <summary>Answer text as it arrives (sentence-sized pieces) — feeds the
    /// Ask typewriter and TTS before the whole answer is in.</summary>
    public Action<string>? OnText { get; init; }

    /// <summary>A streaming chat: like the plain chat delegate, but calls the
    /// callback with text deltas as the provider sends them.</summary>
    public Func<IReadOnlyList<object>, object[], Action<string>, Task<ChatTurn?>>? StreamingChat { get; init; }

    /// <summary>Force (true) or skip (false) the plan turn; null = decide by <see cref="PlanStep.Needs"/>.</summary>
    public bool? Plan { get; init; }
}

/// <summary>
/// browser-use's loop detector, cut down for chat: the same (tool, args)
/// inside one run returns the earlier result instead of running again, and
/// two consecutive tool errors stop the tool phase.
/// </summary>
sealed class ToolCallLoopGuard
{
    public const int MaxConsecutiveErrors = 2;
    public const string RepeatPrefix = "Already called with these arguments; use the earlier result: ";

    readonly Dictionary<string, string> _results = new(StringComparer.Ordinal);
    int _consecutiveErrors;

    public bool Tripped => _consecutiveErrors >= MaxConsecutiveErrors;

    /// <summary>SHA-256 of name + canonical args (sorted keys, no whitespace), first 12 hex.</summary>
    public static string Hash(string name, string? argumentsJson)
    {
        string canonical;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            canonical = Canonical(doc.RootElement);
        }
        catch (JsonException)
        {
            canonical = (argumentsJson ?? "").Trim();
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + canonical));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()!.Trim()),
        _ => e.GetRawText(),
    };

    public bool TryGetRepeat(string name, string? argumentsJson, out string cached) =>
        _results.TryGetValue(Hash(name, argumentsJson), out cached!);

    public void Record(string name, string? argumentsJson, string result, bool failed)
    {
        _results[Hash(name, argumentsJson)] = result;
        _consecutiveErrors = failed ? _consecutiveErrors + 1 : 0;
    }

    /// <summary>The loop's own failure strings (see AgentLoop.ExecuteAsync).</summary>
    public static bool LooksFailed(string result) =>
        result.StartsWith("The tool failed", StringComparison.Ordinal)
        || result.StartsWith("The tool timed out", StringComparison.Ordinal)
        || result.StartsWith("Unknown tool", StringComparison.Ordinal)
        || result.StartsWith("The tool arguments were not valid JSON", StringComparison.Ordinal);
}

/// <summary>
/// Plan vs. act (Cline, agent_chat.md §3.6): multi-step or risky requests get
/// a plan turn with only ReadOnly tools, shown on the approve card; simple
/// ones skip it.
/// </summary>
static class PlanStep
{
    // "log this call AND set a follow-up": two action verbs joined by "and / then".
    static readonly Regex ActionVerb = new(
        @"(?<![\u0590-\u05FF])[וש]?(תקבע|קבע|תיצור|צור|תשלח|שלח|תעדכן|עדכן|תתעד|תעד|תרשום|רשום|תפתח|פתח|תמחק|מחק|תזכיר|הזכר|" +
        @"תוסיף|הוסף|תשנה|שנה|תחפש|חפש|תמצא|מצא)(?![\u0590-\u05FF])|" +
        @"\b(set|create|send|update|log|open|delete|add|change|remind|schedule|find|search)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex Joiner = new(@"(\sו(?=\S)|\bואז\b|\bאחר כך\b|\bthen\b|\band\b|,)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex Risky = new(
        @"סיילספורס|salesforce|תשלח ל|שלח ל|\bsend\b|\bemail\b|מייל|לכולם|כל ה",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the request should be planned before acting.</summary>
    public static bool Needs(string question, IEnumerable<AgentTool> tools)
    {
        var q = question.Trim();
        if (q.Length == 0) return false;
        var verbs = ActionVerb.Matches(q).Count;
        if (verbs >= 2 && Joiner.IsMatch(q)) return true;
        // A risky phrase only matters when a tool could actually act on it.
        return Risky.IsMatch(q) && tools.Any(t => t.Risk == ToolRisk.External);
    }

    public const string PlanInstruction =
        "PLAN FIRST. Do not act yet. You may call read-only tools to look things up. Then reply with a " +
        "short numbered plan in Hebrew — one line per action you will take, naming the tool and the key " +
        "values (who, when, what). No other text.";

    public const string ActInstruction =
        "The user approved this plan. Carry it out now with the tools, exactly as planned, then answer briefly.";

    public const string DeclinedAnswer = "בסדר, לא עשיתי כלום.";
}

/// <summary>
/// Splits streamed text into whole sentences for TTS: nothing is released
/// until a sentence ends (or <see cref="Flush"/>), so the voice never reads
/// half a word.
/// </summary>
sealed class SentenceChunker
{
    readonly StringBuilder _buffer = new();
    readonly Action<string> _emit;

    public SentenceChunker(Action<string> emit) => _emit = emit;

    public void Feed(string delta)
    {
        _buffer.Append(delta);
        while (true)
        {
            var text = _buffer.ToString();
            var cut = -1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] is '.' or '!' or '?' or '…' or '\n' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                {
                    cut = i + 1;
                    break;
                }
            }
            if (cut < 0) return;
            var sentence = text[..cut].Trim();
            _buffer.Remove(0, cut);
            if (sentence.Length > 0) _emit(sentence);
        }
    }

    public void Flush()
    {
        var rest = _buffer.ToString().Trim();
        _buffer.Clear();
        if (rest.Length > 0) _emit(rest);
    }
}
