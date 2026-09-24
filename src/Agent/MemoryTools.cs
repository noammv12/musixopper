using System.Text;
using System.Text.Json;
using Palon.Memory;

namespace Palon.Agent;

/// <summary>
/// Saves a line to the user's own "About you" memory — explicit requests
/// ("תזכור ש…", "remember…") and corrections of how Palon should work.
/// Never for client facts (those come from call notes). The UI toasts
/// Undo/Edit; the model confirms briefly.
/// </summary>
sealed class RememberAboutMeTool : AgentTool
{
    public override string Name => "remember_about_me";
    public override string Description =>
        "Save something about the USER (not a client) to their permanent profile, which you always see: " +
        "a preference, a rule about when/how they work or call someone, their working style, or a person " +
        "in their work life (e.g. their manager). Call it when the user says 'תזכור ש…'/'remember…', or " +
        "corrects how you do something. For a time-window calling rule (\"don't call Dani before 12\") " +
        "also fill rule_who and not_before/not_after so it is enforced when scheduling.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "text":{"type":"string","description":"One short line in the user's language, phrased as a standing fact/rule (e.g. \"לא להתקשר לדני לפני 12:00\")."},
          "section":{"type":"string","enum":["rules","preferences","style","people"]},
          "reason":{"type":"string","description":"Why this is being saved, e.g. \"user asked to remember\" or \"user corrected the callback time\"."},
          "source":{"type":"string","enum":["explicit","correction"]},
          "rule_who":{"type":"string","description":"For a calling time-window rule: the person's name."},
          "rule_phone":{"type":"string","description":"Optional phone of that person."},
          "not_before":{"type":"string","description":"Earliest allowed call time, \"HH:mm\"."},
          "not_after":{"type":"string","description":"Latest allowed call time, \"HH:mm\"."}
        },"required":["text","section","reason"]}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var text = (Str(args, "text") ?? "").Trim();
        if (text.Length == 0) return Task.FromResult(new ToolOutcome("Nothing to remember — text is empty."));
        var section = ProfileBook.ParseSection(Str(args, "section")) ?? ProfileSection.Preferences;
        var source = Str(args, "source") == "correction" ? "correction" : "explicit";
        var reason = Str(args, "reason") ?? "user asked to remember";
        var rule = RuleFrom(args);
        var (item, added) = MemoryStore.RememberFromChat(text, section, source, reason, rule);
        var ruleNote = item.Rule is { } r ? $" Enforced when scheduling: {r.Describe()}." : "";
        return Task.FromResult(new ToolOutcome(added
            ? $"Saved to the user's profile ({item.Section}).{ruleNote} Confirm in a few words."
            : "Already in the user's profile — nothing changed."));
    }

    internal static TimeRule? RuleFrom(JsonElement args)
    {
        var who = Str(args, "rule_who")?.Trim() ?? "";
        var phone = Str(args, "rule_phone");
        TimeSpan? Parse(string name) => TimeSpan.TryParse(Str(args, name), System.Globalization.CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;
        var rule = new TimeRule(who, phone, Parse("not_before"), Parse("not_after"));
        return rule.IsValid ? rule : null;
    }
}

/// <summary>Removes a line from "About you" (best text match, or id). Undoable.</summary>
sealed class ForgetAboutMeTool : AgentTool
{
    public override string Name => "forget_about_me";
    public override string Description =>
        "Remove something from the USER's profile when they ask you to forget it or say it no longer holds. " +
        "Pass words from the line to remove.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Words identifying the profile line to remove."},
          "reason":{"type":"string","description":"Why, e.g. \"user asked to forget\"."}
        },"required":["query","reason"]}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = (Str(args, "query") ?? "").Trim();
        if (query.Length == 0) return Task.FromResult(new ToolOutcome("Say which line to forget."));
        var removed = MemoryStore.ForgetFromChat(query, Str(args, "reason") ?? "user asked to forget");
        return Task.FromResult(new ToolOutcome(removed is null
            ? "No matching line in the user's profile."
            : $"Removed from the profile: \"{removed.Text}\". Confirm in a few words."));
    }
}

/// <summary>Looks up what Palon learned about a client from past calls.</summary>
sealed class RecallClientTool : AgentTool
{
    const int MaxFacts = 15;

    public override string Name => "recall_client";
    public override string Description =>
        "Recall durable facts learned about a CLIENT from past call notes (budget, experience, family " +
        "situation, preferences, objections, status). Look up by name and/or phone, or search all " +
        "clients by keywords. Outdated facts are marked as such when include_history is true.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "name":{"type":"string","description":"Client name (Hebrew or as said)."},
          "phone":{"type":"string","description":"Client phone number."},
          "query":{"type":"string","description":"Keywords to search facts, e.g. \"budget\" or \"גירושים\"."},
          "include_history":{"type":"boolean","description":"Also list facts that are no longer true."}
        }}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name");
        var phone = Str(args, "phone");
        var query = Str(args, "query");
        var history = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("include_history", out var h) && h.ValueKind == JsonValueKind.True;
        var all = MemoryStore.Facts;
        IEnumerable<ClientFact> pool = string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(phone)
            ? all
            : FactBook.ForClient(all, name, phone);
        var hits = FactBook.Search(pool, query, history).Take(MaxFacts).ToList();
        if (hits.Count == 0)
            return Task.FromResult(new ToolOutcome("No remembered facts match. (Call notes may still mention it — try search_notes.)"));
        var sb = new StringBuilder();
        foreach (var group in hits.GroupBy(f => f.ClientKey))
        {
            var who = group.Select(f => f.ClientName).FirstOrDefault(n => n is not null) ?? group.First().Phone ?? "client";
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"{who}{(group.First().Phone is { } p ? $" ({p})" : "")}:");
            foreach (var f in group) sb.Append('\n').Append(FactBook.Line(f));
        }
        return Task.FromResult(new ToolOutcome(sb.ToString()));
    }
}
