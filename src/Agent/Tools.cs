using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Palon.Notes;

namespace Palon.Agent;

/// <summary>Opens one of the user's saved commands (the ids are listed in
/// the system context). Terminal: the window opening is its own feedback.</summary>
sealed class OpenCommandTool : AgentTool
{
    public override string Name => "open_command";
    public override string Description =>
        "Open one of the user's saved commands (an app, site, folder or file they configured). " +
        "Match the request generously across languages and phrasings against the saved-commands list.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "id":{"type":"string","description":"The id of the saved command to open, from the saved-commands list."}
        },"required":["id"]}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = Str(args, "id");
        var command = CommandStore.Load().FirstOrDefault(c => c.Id == id);
        if (command is null)
            return Task.FromResult(new ToolOutcome("No saved command with that id — check the saved-commands list."));
        return Task.FromResult(CommandStore.Execute(command)
            ? new ToolOutcome($"Opened {command.Label}.", EndTurn: true, Toast: $"Opening {command.Label}")
            : new ToolOutcome($"Opening {command.Label} failed — its target may be broken.",
                EndTurn: true, Toast: $"Couldn't open {command.Label} — see log"));
    }
}

/// <summary>Opens a well-known website or a search-results page. Terminal.</summary>
sealed class OpenUrlTool : AgentTool
{
    public override string Name => "open_url";
    public override string Description =>
        "Open a website in the browser. Use for well-known sites that are not saved commands " +
        "(YouTube, Gmail, WhatsApp Web...) or for web searches " +
        "(https://www.google.com/search?q=..., URL-encoded). Prefer open_command when one matches.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "url":{"type":"string","description":"Full https:// address to open."}
        },"required":["url"]}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "url") is not { } raw
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return Task.FromResult(new ToolOutcome("That is not a valid http(s) URL."));
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return Task.FromResult(new ToolOutcome($"Opened {uri.Host}.", EndTurn: true, Toast: "Opening it"));
        }
        catch (Exception ex)
        {
            Log.Write($"Open URL failed: {ex.Message}");
            return Task.FromResult(new ToolOutcome("Opening the browser failed.",
                EndTurn: true, Toast: "Couldn't open that — see log"));
        }
    }
}

/// <summary>Creates a call-back reminder — link, text-only, or both. The
/// model confirms it out loud, so this is not terminal.</summary>
sealed class CreateReminderTool : AgentTool
{
    public override string Name => "create_reminder";
    public override string Description =>
        "Set a reminder that pops up above the taskbar at the given time — e.g. to call someone " +
        "back. Compute the time from the current time in the context.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "label":{"type":"string","description":"Short text shown when the reminder fires, in the user's language (e.g. the person to call back)."},
          "due_at":{"type":"string","description":"Local time to fire, formatted \"yyyy-MM-dd HH:mm\" (24h)."},
          "url":{"type":"string","description":"Optional http(s) link to open from the reminder (CRM page, WhatsApp chat)."}
        },"required":["label","due_at"]}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var label = (Str(args, "label") ?? "").Trim();
        if (label.Length == 0)
            return Task.FromResult(new ToolOutcome("A reminder needs a label."));
        if (!TryParseDueLocal(Str(args, "due_at"), DateTime.Now, out var dueLocal))
            return Task.FromResult(new ToolOutcome("Bad due_at — use \"yyyy-MM-dd HH:mm\" local time, in the future."));

        var reminder = ReminderStore.Add(Str(args, "url") ?? "", label, dueLocal.ToUniversalTime());
        return Task.FromResult(reminder is null
            ? new ToolOutcome("Saving the reminder failed (too many pending, or a bad link).")
            : new ToolOutcome($"Reminder \"{reminder.DisplayLabel}\" set for {dueLocal:ddd d MMM HH:mm}."));
    }

    /// <summary>"yyyy-MM-dd HH:mm" (also with 'T') or bare "HH:mm" — today if
    /// still ahead, otherwise tomorrow. Rejects past and far-future times.</summary>
    internal static bool TryParseDueLocal(string? raw, DateTime nowLocal, out DateTime dueLocal)
    {
        dueLocal = default;
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;

        string[] full = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss" };
        if (DateTime.TryParseExact(text, full, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            dueLocal = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
        }
        else if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var time))
        {
            dueLocal = nowLocal.Date + time;
            if (dueLocal <= nowLocal) dueLocal = dueLocal.AddDays(1);
        }
        else
        {
            return false;
        }
        return dueLocal > nowLocal && dueLocal < nowLocal.AddDays(366);
    }
}

/// <summary>Lists pending reminders so the model can answer "what's on my plate".</summary>
sealed class ListRemindersTool : AgentTool
{
    public override string Name => "list_reminders";
    public override string Description => "List the user's pending reminders with their due times.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var pending = ReminderStore.Load()
            .Where(r => r.State == ReminderState.Pending)
            .OrderBy(r => r.DueAtUtc)
            .Take(10)
            .ToList();
        if (pending.Count == 0) return Task.FromResult(new ToolOutcome("No pending reminders."));
        var sb = new StringBuilder("Pending reminders:");
        foreach (var reminder in pending)
            sb.Append($"\n- {reminder.DisplayLabel} — {reminder.DueAtUtc.ToLocalTime():ddd d MMM HH:mm}");
        return Task.FromResult(new ToolOutcome(sb.ToString()));
    }
}

/// <summary>Searches the call notes — by text, by caller number, or just the
/// most recent ones.</summary>
sealed class SearchNotesTool : AgentTool
{
    const int MaxResults = 5;
    const int MaxCharsPerNote = 450;

    public override string Name => "search_notes";
    public override string Description =>
        "Search the user's saved call notes (summaries + transcripts of their recorded calls). " +
        "Filter by free text, by caller phone number, or neither for the most recent calls.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "query":{"type":"string","description":"Free text to look for in the notes (optional)."},
          "number":{"type":"string","description":"Caller phone number to filter by (optional)."},
          "limit":{"type":"integer","description":"Max notes to return, 1-5 (default 3)."}
        }}
        """;

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var matches = Filter(NotesStore.Load(), Str(args, "query"), Str(args, "number"),
            Math.Clamp(Int(args, "limit") ?? 3, 1, MaxResults));
        if (matches.Count == 0)
            return Task.FromResult(new ToolOutcome("No matching call notes."));
        var sb = new StringBuilder();
        foreach (var note in matches)
        {
            var local = note.StartedUtc.ToLocalTime();
            var body = note.Summary ?? note.Transcript;
            if (body.Length > MaxCharsPerNote) body = body[..MaxCharsPerNote] + "…";
            sb.Append($"[{local:ddd d MMM HH:mm}] {Math.Max(1, note.DurationSec / 60)} min")
              .Append(note.Number is { } number ? $" · {number}" : "")
              .Append('\n').Append(body).Append("\n---\n");
        }
        return Task.FromResult(new ToolOutcome(sb.ToString().TrimEnd()));
    }

    internal static List<CallNote> Filter(List<CallNote> notes, string? query, string? number, int limit)
    {
        IEnumerable<CallNote> result = notes.OrderByDescending(n => n.StartedUtc);
        if (!string.IsNullOrWhiteSpace(number))
            result = result.Where(n => PhoneMatch.Same(n.Number, number));
        if (!string.IsNullOrWhiteSpace(query))
            result = result.Where(n =>
                (n.Summary ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                n.Transcript.Contains(query, StringComparison.OrdinalIgnoreCase));
        return result.Take(limit).ToList();
    }
}

/// <summary>Answers "how many calls did I make today" from the local stats store.</summary>
sealed class CallStatsTool : AgentTool
{
    public override string Name => "get_call_stats";
    public override string Description => "Get the user's call counts and talk time for today and the last 7 days.";
    public override string ParametersJson => """{"type":"object","properties":{}}""";

    public override Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var all = CallStatsStore.Load();
        var today = all.Where(c => c.StartedUtc.ToLocalTime().Date == DateTime.Now.Date).ToList();
        var week = all.Where(c => c.StartedUtc.ToLocalTime().Date >= DateTime.Now.Date.AddDays(-6)).ToList();
        static string Line(List<CallRecord> calls) => calls.Count == 0
            ? "no calls"
            : $"{calls.Count} call(s), {TimeSpan.FromSeconds(calls.Sum(c => (long)c.DurationSec)):h\\:mm\\:ss} on the line";
        return Task.FromResult(new ToolOutcome($"Today: {Line(today)}. Last 7 days: {Line(week)}."));
    }
}

/// <summary>Pauses or resumes the user's media by voice. Terminal — the
/// silence (or the music) is its own feedback.</summary>
sealed class ControlMusicTool : AgentTool
{
    public override string Name => "control_music";
    public override string Description => "Pause the user's currently playing music/media, or resume what was paused this way.";
    public override string ParametersJson => """
        {"type":"object","properties":{
          "action":{"type":"string","enum":["pause","resume"],"description":"pause stops playing media; resume restarts what pause stopped."}
        },"required":["action"]}
        """;

    public override async Task<ToolOutcome> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        switch (Str(args, "action"))
        {
            case "pause":
                await MediaController.CliPauseAsync();
                return new ToolOutcome("Music paused.", EndTurn: true, Toast: "Music paused");
            case "resume":
                await MediaController.CliResumeAsync();
                return new ToolOutcome("Music resumed.", EndTurn: true, Toast: "Music resumed");
            default:
                return new ToolOutcome("action must be \"pause\" or \"resume\".");
        }
    }
}
