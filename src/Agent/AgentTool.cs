using System.Text.Json;

namespace Palon.Agent;

/// <summary>
/// What a tool run hands back. ResultForModel goes into the conversation as
/// the tool message (the model reads it and composes the reply). EndTurn
/// short-circuits the loop instead — for actions that are their own
/// feedback (a window opening, music stopping), where another model round
/// would only add latency; Toast is then what the user sees.
/// </summary>
sealed record ToolOutcome(string ResultForModel, bool EndTurn = false, string? Toast = null);

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
