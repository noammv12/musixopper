using System.Text.Json;

namespace Palon.Agent;

/// <summary>
/// What a tool run hands back. ResultForModel goes into the conversation as
/// the tool message (the model reads it and composes the reply). EndTurn
/// short-circuits the loop instead — for actions that are their own
/// feedback (a window opening, music stopping), where another model round
/// would only add latency; Toast is then what the user sees.
/// </summary>
sealed record ToolOutcome(string ResultForModel, bool EndTurn = false, string? Toast = null)
{
    /// <summary>Reverses what a Reversible tool did (delete the reminder it
    /// created, …). Surfaced to the user as an Undo toast; null = no undo.</summary>
    public Func<Task<string>>? Undo { get; init; }
}

/// <summary>
/// One capability Palon can invoke: a name + description + JSON-Schema
/// parameters (the OpenAI function-calling contract both Gemini and
/// DeepSeek speak) and the code that runs it. Adding a capability to the
/// assistant = adding one subclass to ToolRegistry.CreateDefault.
/// </summary>
abstract class AgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }

    /// <summary>JSON Schema for the arguments object.</summary>
    public abstract string ParametersJson { get; }

    public abstract Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct);

    /// <summary>How long the loop waits before answering the model with
    /// "timed out" and moving on. Override for tools that are slow by nature.</summary>
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(10);

    /// <summary>Permission tier (agent_chat.md §3.1). ReadOnly runs freely and
    /// is allowed in the plan turn; External always goes through the approve card.</summary>
    public virtual ToolRisk Risk => ToolRisks.For(Name);

    /// <summary>The Hebrew chip shown while this call runs ("מחפש בהערות…").</summary>
    public virtual string ProgressLabel(JsonElement args) => ToolLabels.For(Name);

    protected static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    protected static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

/// <summary>Chip labels for the built-in tools; new tools override ProgressLabel.</summary>
static class ToolLabels
{
    static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["open_command"] = "פותח את הפקודה",
        ["open_url"] = "פותח קישור",
        ["create_reminder"] = "קובע חזרה",
        ["list_reminders"] = "בודק חזרות",
        ["search_notes"] = "מחפש בהערות מהשיחות",
        ["get_call_stats"] = "סופר שיחות",
        ["open_whatsapp"] = "פותח וואטסאפ",
        ["daily_recap"] = "מסכם את היום",
        ["control_music"] = "שולט במוזיקה",
        ["look_at_screen"] = "מחכה שתסמן אזור במסך ותאשר",
        ["recall_client"] = "בודק מה אני זוכר על הלקוח",
        ["remember_about_me"] = "שומר בזיכרון",
        ["forget_about_me"] = "מוחק מהזיכרון",
    };

    public static string For(string toolName) => Known.TryGetValue(toolName, out var label) ? label : toolName;
}
