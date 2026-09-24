namespace Palon.Notes;

/// <summary>One row of the model picker. Model is null for Auto.</summary>
sealed record AiModelOption(string Id, string Label, string Provider, string Hint, string? Model);

/// <summary>One step of the resolved provider chain.</summary>
readonly record struct AiRoute(string Provider, string Model);

/// <summary>
/// The model picker's catalog and the pure rule that turns a selection plus
/// the keys on hand into the provider order. Auto keeps the original chain
/// (Gemini primary, DeepSeek fallback); a specific model makes its provider
/// primary with that model, and the other provider stays the fallback with
/// its configured model.
/// </summary>
static class AiModels
{
    public const string Gemini = "Gemini";
    public const string DeepSeek = "DeepSeek";
    public const string AutoId = "auto";

    public static readonly IReadOnlyList<AiModelOption> Catalog =
    [
        new(AutoId, "אוטומטי", "Gemini → DeepSeek", "Gemini קודם, DeepSeek כגיבוי", null),
        new("gemini-3.8-flash", "Gemini 3.8 Flash", Gemini, "מהיר · שכבה חינמית · ברירת מחדל", "gemini-3.8-flash"),
        new("gemini-3.5-flash", "Gemini 3.5 Flash", Gemini, "הדור הקודם · שכבה חינמית", "gemini-3.5-flash"),
        new("deepseek-flash", "DeepSeek V4.1 Flash", DeepSeek, "מהיר וזול", "deepseek-flash"),
        new("deepseek-v4-pro", "DeepSeek V4 Pro", DeepSeek, "החזק ביותר · איטי יותר", "deepseek-v4-pro"),
    ];

    /// <summary>The catalog entry for a stored choice; unknown/empty → Auto.</summary>
    public static AiModelOption Find(string? id) =>
        Catalog.FirstOrDefault(o => string.Equals(o.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Catalog[0];

    public static bool IsAvailable(AiModelOption option, bool hasGemini, bool hasDeepSeek) => option.Provider switch
    {
        Gemini => hasGemini,
        DeepSeek => hasDeepSeek,
        _ => hasGemini || hasDeepSeek,
    };

    /// <summary>
    /// Provider order for a selection. A chosen model whose key is missing
    /// degrades to whatever keys exist rather than to nothing.
    /// </summary>
    public static IReadOnlyList<AiRoute> Resolve(
        string? choice, bool hasGemini, bool hasDeepSeek, string geminiModel, string deepSeekModel)
    {
        var option = Find(choice);
        var gemini = new AiRoute(Gemini, option.Provider == Gemini ? option.Model! : geminiModel);
        var deepSeek = new AiRoute(DeepSeek, option.Provider == DeepSeek ? option.Model! : deepSeekModel);
        var order = option.Provider == DeepSeek ? new[] { deepSeek, gemini } : new[] { gemini, deepSeek };
        return order.Where(r => r.Provider == Gemini ? hasGemini : hasDeepSeek).ToList();
    }
}
