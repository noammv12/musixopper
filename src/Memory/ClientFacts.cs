using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Palon.Memory;

/// <summary>
/// One durable fact about a client, Graphiti-bitemporal: ValidAt/InvalidAt
/// = when it was true in the world; CreatedAt/ExpiredAt = when Palon
/// learned/retired it. Outdated facts are invalidated, never deleted — only
/// active ones (both "ends" null) reach a prompt.
/// </summary>
sealed class ClientFact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];
    /// <summary>"p:&lt;last 9 digits&gt;" when the phone is known, else "n:&lt;normalized name&gt;".</summary>
    public string ClientKey { get; set; } = "";
    public string? ClientName { get; set; }
    public string? Phone { get; set; }
    public string Type { get; set; } = "other";
    public string Text { get; set; } = "";
    public string? Quote { get; set; }
    public string Hash { get; set; } = "";
    public DateTime ValidAt { get; set; }
    public DateTime? InvalidAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiredAt { get; set; }
    /// <summary>"call:&lt;note id&gt;" or "manual".</summary>
    public string Source { get; set; } = "manual";
    public bool Pinned { get; set; }

    public bool IsActive => InvalidAt is null && ExpiredAt is null;
}

/// <summary>What the summary call's FACTS trailer carried.</summary>
sealed record ParsedFact(string Type, string Text, string? Quote, DateTime? ValidAtUtc, IReadOnlyList<int> Replaces);
sealed record FactsPayload(string? ClientName, IReadOnlyList<ParsedFact> Facts);
sealed record FactsApplied(List<ClientFact> Added, int Duplicates, List<ClientFact> Invalidated);

static class FactBook
{
    public const string Marker = "FACTS:";
    const int MaxFactsPerCall = 8;
    const int MaxKnownInPrompt = 25;
    public static readonly string[] Types = { "budget", "experience", "family", "preference", "objection", "status", "contact", "other" };

    public static string? KeyFor(string? phone, string? name)
    {
        var digits = Palon.Agent.PhoneMatch.Digits(phone).TrimStart('0');
        if (digits.Length >= 7) return "p:" + (digits.Length > 9 ? digits[^9..] : digits);
        var n = HebrewText.Normalize(name);
        return n.Length >= 2 ? "n:" + n : null;
    }

    /// <summary>Facts for one client: by phone key, else by name.</summary>
    public static IEnumerable<ClientFact> ForClient(IEnumerable<ClientFact> all, string? name, string? phone)
    {
        var key = KeyFor(phone, null);
        return all.Where(f => (key is not null && f.ClientKey == key)
                              || (f.Phone is not null && Palon.Agent.PhoneMatch.Same(f.Phone, phone))
                              || (!string.IsNullOrWhiteSpace(name) && f.ClientName is not null && HebrewText.SameName(f.ClientName, name)));
    }

    // ---- summary-call integration --------------------------------------------------

    /// <summary>The instruction appended to the summary prompt (same call —
    /// no extra API request).</summary>
    public const string SummaryInstruction =
        "\nThen, only if the call revealed durable facts about the client worth knowing on a " +
        "future call (budget, trading experience, family/life situation, preferences, objections, " +
        "status), add one final line exactly like: FACTS: {\"client\":\"<client's name if said, else " +
        "null>\",\"facts\":[{\"type\":\"budget|experience|family|preference|objection|status|contact|other\"," +
        "\"text\":\"<one short fact, transcript language>\",\"quote\":\"<the words used>\",\"valid_at\":" +
        "\"yyyy-MM-dd or null\",\"replaces\":[<ids of KNOWN FACTS this fact updates or contradicts>]}]}. " +
        "Up to 6 facts, facts about the client only (never about the salesperson). A known fact that " +
        "is still true is not repeated. Different numbers or dates are never duplicates — the newer " +
        "one replaces the older.";

    /// <summary>Known active facts rendered with integer ids (mem0 id
    /// mapping); returns the ids those integers stand for.</summary>
    public static (string Block, List<string> IdMap) KnownFactsBlock(IEnumerable<ClientFact> clientFacts)
    {
        var active = clientFacts.Where(f => f.IsActive).OrderByDescending(f => f.ValidAt).Take(MaxKnownInPrompt).ToList();
        var map = new List<string>();
        if (active.Count == 0) return ("", map);
        var sb = new StringBuilder("KNOWN FACTS about this client (use these ids in \"replaces\"):");
        foreach (var f in active)
        {
            sb.Append($"\n[{map.Count}] ({f.Type}, since {f.ValidAt.ToLocalTime():yyyy-MM-dd}) {f.Text}");
            map.Add(f.Id);
        }
        return (sb.ToString(), map);
    }

    /// <summary>Removes the FACTS line from the reply (the note stays clean) and parses it.</summary>
    public static (string Reply, FactsPayload? Facts) Extract(string reply)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindLastIndex(l => l.TrimStart().TrimStart('*', '`', ' ').StartsWith(Marker, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (reply, null);
        var line = lines[index];
        lines.RemoveAt(index);
        var rest = string.Join("\n", lines).Trim();
        if (rest.Length == 0) rest = reply.Trim();
        return (rest, Parse(line));
    }

    internal static FactsPayload? Parse(string line)
    {
        if (JsonSlice.Object(line) is not { } json) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var client = JsonSlice.Str(root, "client")?.Trim();
            if (client is "null" or "") client = null;
            var facts = new List<ParsedFact>();
            if (root.TryGetProperty("facts", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                {
                    if (facts.Count >= MaxFactsPerCall) break;
                    var text = JsonSlice.Str(el, "text")?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    var type = (JsonSlice.Str(el, "type") ?? "other").Trim().ToLowerInvariant();
                    if (!Types.Contains(type)) type = "other";
                    var replaces = new List<int>();
                    if (el.TryGetProperty("replaces", out var r) && r.ValueKind == JsonValueKind.Array)
                        foreach (var x in r.EnumerateArray())
                            if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i)) replaces.Add(i);
                            else if (x.ValueKind == JsonValueKind.String && int.TryParse(x.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j)) replaces.Add(j);
                    facts.Add(new ParsedFact(type, ProfileBook.Clip(text), JsonSlice.Str(el, "quote")?.Trim(), JsonSlice.Date(el, "valid_at"), replaces));
                }
            return facts.Count == 0 && client is null ? null : new FactsPayload(client, facts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies extracted facts: MD5 dedup against the client's active
    /// facts and within the batch, then Graphiti invalidation for every
    /// "replaces" id (mapped back through <paramref name="idMap"/>; unknown
    /// integers are ignored). An old fact whose ValidAt is later than the
    /// new one's wins — the late-arriving stale fact is expired at once.
    /// </summary>
    public static FactsApplied Apply(List<ClientFact> all, string key, string? name, string? phone, FactsPayload payload,
        IReadOnlyList<string> idMap, string source, DateTime observedUtc, DateTime nowUtc)
    {
        var added = new List<ClientFact>();
        var invalidated = new List<ClientFact>();
        var duplicates = 0;
        var seen = all.Where(f => f.ClientKey == key && f.IsActive).Select(f => f.Hash).ToHashSet();
        foreach (var p in payload.Facts)
        {
            var hash = HebrewText.Hash(p.Text);
            if (!seen.Add(hash))
            {
                duplicates++;
                continue;
            }
            var fact = new ClientFact
            {
                ClientKey = key, ClientName = name, Phone = phone, Type = p.Type, Text = p.Text, Quote = p.Quote,
                Hash = hash, ValidAt = p.ValidAtUtc ?? observedUtc, CreatedAt = nowUtc, Source = source,
            };
            foreach (var index in p.Replaces.Distinct())
            {
                if (index < 0 || index >= idMap.Count) continue;
                var old = all.FirstOrDefault(f => f.Id == idMap[index] && f.ClientKey == key && f.IsActive);
                if (old is null) continue;
                if (old.ValidAt > fact.ValidAt)
                {
                    // Stale info arriving late: the newer world-truth stays.
                    fact.InvalidAt ??= old.ValidAt;
                    fact.ExpiredAt ??= nowUtc;
                    continue;
                }
                old.InvalidAt = fact.ValidAt;
                old.ExpiredAt = nowUtc;
                invalidated.Add(old);
            }
            all.Add(fact);
            added.Add(fact);
        }
        if (name is not null)
            foreach (var f in all.Where(f => f.ClientKey == key && f.ClientName is null)) f.ClientName = name;
        return new FactsApplied(added, duplicates, invalidated);
    }

    /// <summary>User marked a fact outdated: invalidated as of now, kept as history.</summary>
    public static bool Invalidate(List<ClientFact> all, string id, DateTime nowUtc)
    {
        var f = all.FirstOrDefault(x => x.Id == id && x.IsActive);
        if (f is null) return false;
        f.InvalidAt = nowUtc;
        f.ExpiredAt = nowUtc;
        return true;
    }

    /// <summary>Edit = the old wording retires, a corrected fact takes over (history kept).</summary>
    public static ClientFact? Correct(List<ClientFact> all, string id, string newText, DateTime nowUtc)
    {
        var f = all.FirstOrDefault(x => x.Id == id && x.IsActive);
        if (f is null || newText.Trim().Length == 0) return null;
        f.ExpiredAt = nowUtc; // learned-wrong, not world-changed: InvalidAt stays null
        var fixedFact = new ClientFact
        {
            ClientKey = f.ClientKey, ClientName = f.ClientName, Phone = f.Phone, Type = f.Type, Text = ProfileBook.Clip(newText),
            Quote = f.Quote, Hash = HebrewText.Hash(newText), ValidAt = f.ValidAt, CreatedAt = nowUtc, Source = "manual", Pinned = f.Pinned,
        };
        all.Add(fixedFact);
        return fixedFact;
    }

    /// <summary>Keyword/prefix recall over active facts (Hebrew-normalized),
    /// best first; an empty query returns everything, newest first.</summary>
    public static List<ClientFact> Search(IEnumerable<ClientFact> facts, string? query, bool includeHistory = false)
    {
        var pool = facts.Where(f => includeHistory || f.IsActive);
        if (string.IsNullOrWhiteSpace(query))
            return pool.OrderByDescending(f => f.Pinned).ThenByDescending(f => f.IsActive).ThenByDescending(f => f.ValidAt).ToList();
        return pool
            .Select(f => (f, s: HebrewText.Score(query, $"{f.Text} {f.Type} {f.ClientName}")))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenByDescending(x => x.f.IsActive).ThenByDescending(x => x.f.ValidAt)
            .Select(x => x.f)
            .ToList();
    }

    /// <summary>One line per fact for a tool result or a context block.</summary>
    public static string Line(ClientFact f)
    {
        var s = $"- [{f.Type}] {f.Text} (since {f.ValidAt.ToLocalTime():d MMM yyyy}";
        if (!f.IsActive) s += f.InvalidAt is { } until ? $"; OUTDATED since {until.ToLocalTime():d MMM yyyy}" : "; retracted";
        return s + ")";
    }

    /// <summary>The client block for a prompt — labelled so the model never
    /// confuses a client's facts with the user's own profile.</summary>
    public static string RenderClientBlock(string who, IEnumerable<ClientFact> facts, int max = 10)
    {
        var active = facts.Where(f => f.IsActive).OrderByDescending(f => f.Pinned).ThenByDescending(f => f.ValidAt).Take(max).ToList();
        if (active.Count == 0) return "";
        var sb = new StringBuilder($"CLIENT MEMORY — facts about the client {who} (NOT about the user):");
        foreach (var f in active) sb.Append('\n').Append(Line(f));
        return sb.ToString();
    }
}
