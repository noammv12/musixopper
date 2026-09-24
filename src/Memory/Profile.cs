using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Palon.Memory;

/// <summary>The four "About you" blocks (Letta-code style pinned files).</summary>
enum ProfileSection { Rules, Preferences, Style, People }

/// <summary>
/// One line of the user's profile. Source: "explicit" (they said
/// "remember…"), "correction" (they corrected Palon), "suggestion" (an
/// inferred habit they approved) or "manual" (typed on the Memory screen).
/// </summary>
sealed record ProfileItem(
    string Id,
    ProfileSection Section,
    string Text,
    string Source,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    bool Pinned = false,
    TimeRule? Rule = null);

/// <summary>An inferred habit waiting for the user's yes/no — never in the prompt.</summary>
sealed record ProfileSuggestion(string Id, ProfileSection Section, string Text, string? Quote, double Confidence, DateTime CreatedUtc);

/// <summary>Every change to the profile, with its reason (the letta-code
/// "commit message"): Undo replays Before.</summary>
sealed record ProfileChange(string Id, DateTime Utc, string Op, string Reason, ProfileItem? Before, ProfileItem? After);

sealed class ProfileData
{
    public int Version { get; set; } = 1;
    public List<ProfileItem> Items { get; set; } = new();
    public List<ProfileSuggestion> Suggestions { get; set; } = new();
    /// <summary>Hashes of rejected suggestions — never suggested again.</summary>
    public List<string> Rejected { get; set; } = new();
    /// <summary>"Don't remember this": no inferred suggestions, no client facts.</summary>
    public bool Paused { get; set; }
    public List<ProfileChange> History { get; set; } = new();
}

/// <summary>
/// Pure operations on the profile. Every mutation appends a ProfileChange
/// with a reason; the store persists and raises events.
/// </summary>
static class ProfileBook
{
    public const int MaxItemChars = 280;
    public const int PromptCapChars = 2400;
    const int MaxHistory = 400;
    const int MaxSuggestions = 20;
    public const double MinSuggestionConfidence = 0.6;

    static string NewId() => Guid.NewGuid().ToString("n")[..10];

    public static string Clip(string text)
    {
        text = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return text.Length > MaxItemChars ? text[..MaxItemChars].TrimEnd() + "…" : text;
    }

    static void Log(ProfileData d, string op, string reason, ProfileItem? before, ProfileItem? after, DateTime now)
    {
        d.History.Add(new ProfileChange(NewId(), now, op, string.IsNullOrWhiteSpace(reason) ? op : reason.Trim(), before, after));
        if (d.History.Count > MaxHistory) d.History.RemoveRange(0, d.History.Count - MaxHistory);
    }

    /// <summary>Adds a line (or returns the existing one with the same
    /// normalized text). A time-window rule found in the text is attached
    /// and the line filed under Rules.</summary>
    public static (ProfileItem Item, bool Added) Remember(ProfileData d, string text, ProfileSection section, string source,
        string reason, DateTime now, TimeRule? rule = null)
    {
        text = Clip(text);
        var hash = HebrewText.Hash(text);
        if (d.Items.FirstOrDefault(i => HebrewText.Hash(i.Text) == hash) is { } existing) return (existing, false);
        rule ??= TimeRules.TryParse(text);
        if (rule is { IsValid: false }) rule = null;
        if (rule is not null) section = ProfileSection.Rules;
        var item = new ProfileItem(NewId(), section, text, source, now, now, Rule: rule);
        d.Items.Add(item);
        d.Suggestions.RemoveAll(s => HebrewText.Hash(s.Text) == hash);
        Log(d, "add", reason, null, item, now);
        return (item, true);
    }

    public static ProfileItem? Edit(ProfileData d, string id, string newText, string reason, DateTime now, ProfileSection? section = null)
    {
        var index = d.Items.FindIndex(i => i.Id == id);
        if (index < 0 || newText.Trim().Length == 0) return null;
        var before = d.Items[index];
        var text = Clip(newText);
        var rule = TimeRules.TryParse(text) ?? (text == before.Text ? before.Rule : null);
        var after = before with
        {
            Text = text, UpdatedUtc = now, Rule = rule,
            Section = rule is not null ? ProfileSection.Rules : section ?? before.Section,
        };
        d.Items[index] = after;
        Log(d, "edit", reason, before, after, now);
        return after;
    }

    public static ProfileItem? SetPinned(ProfileData d, string id, bool pinned, DateTime now)
    {
        var index = d.Items.FindIndex(i => i.Id == id);
        if (index < 0) return null;
        var before = d.Items[index];
        var after = before with { Pinned = pinned, UpdatedUtc = now };
        d.Items[index] = after;
        Log(d, pinned ? "pin" : "unpin", pinned ? "pinned" : "unpinned", before, after, now);
        return after;
    }

    public static ProfileItem? Delete(ProfileData d, string id, string reason, DateTime now)
    {
        var index = d.Items.FindIndex(i => i.Id == id);
        if (index < 0) return null;
        var before = d.Items[index];
        d.Items.RemoveAt(index);
        Log(d, "delete", reason, before, null, now);
        return before;
    }

    /// <summary>Best match for a forget request: exact id, else the item
    /// whose text shares the most tokens with the query (at least one).</summary>
    public static ProfileItem? Find(ProfileData d, string query)
    {
        if (d.Items.FirstOrDefault(i => i.Id == query.Trim()) is { } byId) return byId;
        return d.Items
            .Select(i => (Item: i, Score: HebrewText.Score(query, i.Text)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.UpdatedUtc)
            .Select(x => x.Item)
            .FirstOrDefault();
    }

    /// <summary>Reverts one change: an add is removed, an edit/delete restored.</summary>
    public static bool Undo(ProfileData d, string changeId, DateTime now)
    {
        var change = d.History.FirstOrDefault(c => c.Id == changeId);
        if (change is null) return false;
        var id = (change.After ?? change.Before)!.Id;
        var index = d.Items.FindIndex(i => i.Id == id);
        var current = index >= 0 ? d.Items[index] : null;
        if (change.Before is null)
        {
            if (index >= 0) d.Items.RemoveAt(index);
        }
        else if (index >= 0) d.Items[index] = change.Before;
        else d.Items.Add(change.Before);
        Log(d, "undo", $"undo {change.Op}: {change.Reason}", current, change.Before, now);
        return true;
    }

    public static IEnumerable<TimeRule> Rules(ProfileData d) =>
        d.Items.Where(i => i.Rule is { IsValid: true }).Select(i => i.Rule!);

    // ---- suggestions (LangMem-style background extraction) ------------------

    public static int AddSuggestions(ProfileData d, IEnumerable<ProfileSuggestion> incoming)
    {
        var added = 0;
        foreach (var s in incoming)
        {
            if (s.Confidence < MinSuggestionConfidence || s.Text.Trim().Length < 4) continue;
            var hash = HebrewText.Hash(s.Text);
            if (d.Rejected.Contains(hash)
                || d.Items.Any(i => HebrewText.Hash(i.Text) == hash)
                || d.Suggestions.Any(x => HebrewText.Hash(x.Text) == hash)) continue;
            d.Suggestions.Add(s with { Text = Clip(s.Text) });
            added++;
        }
        if (d.Suggestions.Count > MaxSuggestions) d.Suggestions.RemoveRange(0, d.Suggestions.Count - MaxSuggestions);
        return added;
    }

    public static ProfileItem? Approve(ProfileData d, string suggestionId, DateTime now)
    {
        var s = d.Suggestions.FirstOrDefault(x => x.Id == suggestionId);
        if (s is null) return null;
        d.Suggestions.Remove(s);
        return Remember(d, s.Text, s.Section, "suggestion", "approved suggestion" + (s.Quote is { Length: > 0 } q ? $" (\"{q}\")" : ""), now).Item;
    }

    public static bool Reject(ProfileData d, string suggestionId)
    {
        var s = d.Suggestions.FirstOrDefault(x => x.Id == suggestionId);
        if (s is null) return false;
        d.Suggestions.Remove(s);
        var hash = HebrewText.Hash(s.Text);
        if (!d.Rejected.Contains(hash)) d.Rejected.Add(hash);
        if (d.Rejected.Count > 500) d.Rejected.RemoveAt(0);
        return true;
    }

    // ---- prompts ----------------------------------------------------------------

    static string SectionTitle(ProfileSection s) => s switch
    {
        ProfileSection.Rules => "Rules",
        ProfileSection.Preferences => "Preferences",
        ProfileSection.Style => "Working style",
        _ => "People",
    };

    public static ProfileSection? ParseSection(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "rules" or "rule" => ProfileSection.Rules,
        "preferences" or "preference" => ProfileSection.Preferences,
        "style" or "working_style" or "working style" => ProfileSection.Style,
        "people" or "person" => ProfileSection.People,
        _ => null,
    };

    /// <summary>
    /// The "About you" block compiled into Palon's system prompt, whole
    /// (Letta "system/" files): Markdown by section, pinned lines first,
    /// capped — the overflow is named, never silently cut. Only profile
    /// lines go here; client facts never do.
    /// </summary>
    public static string RenderForPrompt(ProfileData d, int cap = PromptCapChars)
    {
        if (d.Items.Count == 0) return "";
        var sb = new StringBuilder("ABOUT THE USER — their standing rules and preferences (from their own memory; always follow them):");
        var omitted = 0;
        foreach (var section in Enum.GetValues<ProfileSection>())
        {
            var items = d.Items.Where(i => i.Section == section)
                .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.UpdatedUtc).ToList();
            if (items.Count == 0) continue;
            var head = $"\n## {SectionTitle(section)}";
            if (sb.Length + head.Length > cap)
            {
                omitted += items.Count;
                continue;
            }
            sb.Append(head);
            foreach (var item in items)
            {
                var line = "\n- " + item.Text;
                if (sb.Length + line.Length > cap) { omitted++; continue; }
                sb.Append(line);
            }
        }
        if (omitted > 0) sb.Append($"\n(+{omitted} more lines not shown — the user can see them on the Memory screen.)");
        return sb.ToString();
    }

    /// <summary>Input for the debounced inference call: the current profile
    /// with small integer ids (mem0 id mapping — models mangle GUIDs) and
    /// the recent exchanges.</summary>
    public static string BuildReflectionInput(ProfileData d, IReadOnlyList<(string Question, string Answer)> exchanges)
    {
        var sb = new StringBuilder("CURRENT PROFILE (for dedup only):");
        for (var i = 0; i < d.Items.Count; i++) sb.Append($"\n[{i}] ({SectionTitle(d.Items[i].Section)}) {d.Items[i].Text}");
        if (d.Items.Count == 0) sb.Append("\n(empty)");
        sb.Append("\n\nRECENT CONVERSATION:");
        foreach (var (q, a) in exchanges) sb.Append($"\nUser: {q}\nPalon: {a}");
        return sb.ToString();
    }

    public const string ReflectionPrompt =
        "You maintain a salesperson's personal profile for their assistant Palon. From the " +
        "conversation, extract only DURABLE facts about the USER themself — preferences, working " +
        "habits, rules about how/when they work or call people, people in their work life (e.g. " +
        "their manager) — that are not already in the profile. Never extract facts about clients, " +
        "one-off requests, or Palon's own answers. Each item needs a verbatim quote from the user's " +
        "words and a confidence 0..1. Reply with PURE JSON only: " +
        "{\"suggestions\":[{\"section\":\"rules|preferences|style|people\",\"text\":\"<one short line, " +
        "user's language>\",\"quote\":\"<their words>\",\"confidence\":0.8}]} — or {\"suggestions\":[]}.";

    /// <summary>Parses the inference reply. Malformed → empty (suggestions
    /// are optional); a missing quote drops the item (anti-hallucination).</summary>
    public static List<ProfileSuggestion> ParseSuggestions(string? reply, DateTime now)
    {
        var list = new List<ProfileSuggestion>();
        if (JsonSlice.Object(reply) is not { } json) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("suggestions", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var text = JsonSlice.Str(el, "text")?.Trim();
                var quote = JsonSlice.Str(el, "quote")?.Trim();
                if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(quote)) continue;
                var confidence = el.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5;
                var section = ParseSection(JsonSlice.Str(el, "section")) ?? ProfileSection.Preferences;
                list.Add(new ProfileSuggestion(NewId(), section, Clip(text), quote, confidence, now));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }
}

/// <summary>Small JSON helpers shared by the memory parsers.</summary>
static class JsonSlice
{
    /// <summary>The outermost {...} in a reply (models wrap JSON in prose/fences).</summary>
    public static string? Object(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start < 0 || end <= start ? null : text[start..(end + 1)];
    }

    public static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static DateTime? Date(JsonElement obj, string name) =>
        Str(obj, name) is { Length: > 0 } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
