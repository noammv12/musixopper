using System.Text.Json;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>One offline eval case: a Hebrew question (STT noise included)
/// and the tool the model should reach for first — null = answer directly.</summary>
sealed record EvalCase(string Question, string? ExpectedTool, string Why = "");

sealed record EvalResult(EvalCase Case, string? ActualTool, bool Pass);

sealed record EvalReport(IReadOnlyList<EvalResult> Results)
{
    public int Passed => Results.Count(r => r.Pass);
    public double Accuracy => Results.Count == 0 ? 0 : (double)Passed / Results.Count;

    public string Summary() =>
        $"{Passed}/{Results.Count} ({Accuracy:P0})" + string.Concat(Results.Where(r => !r.Pass)
            .Select(r => $"\n  ✗ \"{r.Case.Question}\": expected {r.Case.ExpectedTool ?? "no tool"}, got {r.ActualTool ?? "no tool"}"));
}

/// <summary>
/// Catches regressions in tool choice (agent_chat.md weakness 12). Each case
/// runs the real loop against probe tools — same names, descriptions and
/// schemas as the real ones, but they only record the call — so a run has
/// no side effects. With a fake model it tests the harness offline; with the
/// real chat delegate it scores the model.
/// </summary>
static class AgentEval
{
    public static readonly IReadOnlyList<EvalCase> Hebrew = new EvalCase[]
    {
        new("תזכיר לי להתקשר לדני מחר בחמש", "create_reminder", "reminder with a time"),
        new("תקבע לי חזרה למשה בעוד שעה", "create_reminder", "callback"),
        new("תזקיר לי לשלוח את החוזה בשלוש", "create_reminder", "STT typo of תזכיר"),
        new("למי אני צריך לחזור היום", "list_reminders", "open callbacks"),
        new("מה יש לי על הצלחת", "list_reminders", "what's on my plate"),
        new("מה הלקוח מהבוקר אמר על המחיר", "search_notes", "search call notes"),
        new("מה סיכמנו עם יוסי בשיחה האחרונה", "search_notes", "note lookup by name"),
        new("כמה שיחות עשיתי היום", "get_call_stats", "counts"),
        new("כמה שיחות היו לי השבוע", "get_call_stats", "counts, week"),
        new("תסכם לי את היום", "daily_recap", "recap"),
        new("תעצור את המוזיקה", "control_music", "pause"),
        new("שים שיר הבא", "control_music", "next track"),
        new("תפתח וואטסאפ לדני", "open_whatsapp", "chat"),
        new("תפתח לי את גוגל", "open_url", "a site"),
        new("מה הבירה של צרפת", null, "general knowledge, no tool"),
        new("תודה רבה", null, "small talk"),
    };

    /// <summary>A stand-in with the real tool's contract that records instead of acting.</summary>
    sealed class ProbeTool : AgentTool
    {
        readonly AgentTool? _real;
        readonly string _name;
        readonly List<string> _calls;

        public ProbeTool(AgentTool real, List<string> calls)
        {
            _real = real;
            _name = real.Name;
            _calls = calls;
        }

        public ProbeTool(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override string Name => _name;
        public override string Description => _real?.Description ?? _name;
        public override string ParametersJson => _real?.ParametersJson ?? """{"type":"object","properties":{}}""";
        public override ToolRisk Risk => ToolRisk.ReadOnly; // probes never touch anything, never ask

        public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            lock (_calls) _calls.Add(_name);
            return Task.FromResult(new ToolOutcome("ok", EndTurn: true, Toast: "ok"));
        }
    }

    /// <summary>Runs every case through the loop and scores the first tool
    /// called. <paramref name="toolNames"/> or <paramref name="realTools"/>
    /// give the probe set (names only is enough for a fake model).</summary>
    public static async Task<EvalReport> RunAsync(
        Func<AssistantSession> newSession,
        Func<IReadOnlyList<object>, object[], Task<ChatTurn?>> chat,
        IReadOnlyList<EvalCase>? cases = null,
        IReadOnlyList<AgentTool>? realTools = null,
        IReadOnlyList<string>? toolNames = null,
        CancellationToken ct = default)
    {
        cases ??= Hebrew;
        var results = new List<EvalResult>();
        foreach (var c in cases)
        {
            var calls = new List<string>();
            var probes = realTools is not null
                ? realTools.Select(t => (AgentTool)new ProbeTool(t, calls)).ToList()
                : (toolNames ?? Array.Empty<string>()).Select(n => (AgentTool)new ProbeTool(n, calls)).ToList();
            await AgentLoop.RunAsync(newSession(), c.Question, ct, new AgentRunOptions { Plan = false }, probes, chat);
            var actual = calls.FirstOrDefault();
            results.Add(new EvalResult(c, actual, actual == c.ExpectedTool));
        }
        return new EvalReport(results);
    }
}
