using System.Text.Json;

namespace Palon.Agent;

/// <summary>
/// The assistant's toolbelt. CreateDefault is the one place a new
/// capability gets registered; ToToolsSpec renders the OpenAI
/// function-calling "tools" array both providers accept.
/// </summary>
static class ToolRegistry
{
    public static IReadOnlyList<AgentTool> CreateDefault() => new AgentTool[]
    {
        new OpenCommandTool(),
        new OpenUrlTool(),
        new CreateReminderTool(),
        new ListRemindersTool(),
        new SearchNotesTool(),
        new CallStatsTool(),
        new OpenWhatsAppTool(),
        new DailyRecapTool(),
        new ControlMusicTool(),
        new RememberAboutMeTool(),
        new ForgetAboutMeTool(),
        new RecallClientTool(),
        new LookAtScreenTool(),
        // "Palon does it" — the agentic layer (src/Agentic).
        new Palon.Agentic.DraftFollowUpTool(),
        new Palon.Agentic.SummarizeClientTool(),
        new Palon.Agentic.NextStepsTodayTool(),
        new Palon.Agentic.RitualTool(),
        new Palon.Agentic.PrepareDealFromNoteTool(),
        new Palon.Agentic.AddDealTool(),
        new Palon.Agentic.FindInNotesTool(),
    };

    /// <summary>Serializable request payload for the "tools" field.</summary>
    public static object[] ToToolsSpec(IEnumerable<AgentTool> tools) => tools
        .Select(tool => (object)new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJson),
            },
        })
        .ToArray();
}
