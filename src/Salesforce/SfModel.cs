using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Salesforce;

/// <summary>A Salesforce record Palon found in the browser (never typed by a model).</summary>
sealed record SfRecordRef(string Id, string ObjectType, string Name, string Url)
{
    /// <summary>The 15-char prefix-comparable form (18-char ids carry a case suffix).</summary>
    public string Id15 => Id.Length >= 15 ? Id[..15] : Id;
}

enum SfChangeKind { LogCall, NewTask, UpdateField }

/// <summary>One write the plan will make. Everything here is shown to the
/// user verbatim in the preview and is part of the approval hash.</summary>
sealed record SfChange(
    SfChangeKind Kind,
    string Subject,
    string? Comments = null,
    DateOnly? Date = null,
    string? Field = null,     // UpdateField: the field's label as the org shows it (e.g. "Status")
    string? NewValue = null,  // UpdateField: the picklist/text value
    string? OldValue = null); // filled at execute time for the undo note, not hashed

/// <summary>
/// What Palon intends to do for one call. Immutable: choosing a different
/// record or editing a change yields a new plan with a new hash, so an
/// approval can never carry over to something the user did not see.
/// </summary>
sealed record SfPlan(
    string NoteId,
    string Phone,
    SfRecordRef? Record,
    IReadOnlyList<SfRecordRef> Candidates,
    IReadOnlyList<SfChange> Changes,
    DateTime CreatedUtc)
{
    public bool Ready => Record is not null && Changes.Count > 0;

    /// <summary>SHA-256 over a canonical serialization of exactly the
    /// fields the user approves: record id, and every change's content.</summary>
    public string Hash => SfHash.Of(this);

    public SfPlan WithRecord(SfRecordRef record) => this with { Record = record };
    public SfPlan WithChanges(IReadOnlyList<SfChange> changes) => this with { Changes = changes };
}

static class SfHash
{
    public static string Of(SfPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("v1\n");
        sb.Append("note=").Append(plan.NoteId).Append('\n');
        sb.Append("record=").Append(plan.Record?.Id15 ?? "-").Append('|').Append(plan.Record?.ObjectType ?? "-").Append('\n');
        foreach (var c in plan.Changes)
        {
            sb.Append("change=").Append(c.Kind).Append('\n');
            Field(sb, "subject", c.Subject);
            Field(sb, "comments", c.Comments);
            Field(sb, "date", c.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Field(sb, "field", c.Field);
            Field(sb, "value", c.NewValue);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Length-prefixed so no field content can forge a boundary.
    static void Field(StringBuilder sb, string name, string? value) =>
        sb.Append(name).Append(':').Append(value?.Length ?? -1).Append(':').Append(value).Append('\n');
}

/// <summary>Phone forms to type into Salesforce global search, and record-id helpers.</summary>
static class SfPhones
{
    /// <summary>
    /// Search terms in the order to try: the local 05X form, then +972, then
    /// (last resort) the last 7 digits. Duplicates and unusable input are dropped.
    /// </summary>
    public static IReadOnlyList<string> SearchTerms(string? raw)
    {
        var list = new List<string>();
        var intl = Phones.ToInternationalDigits(raw);
        if (intl is null)
        {
            var digits = Agent.PhoneMatch.Digits(raw);
            if (digits.Length >= 7) list.Add(digits);
            return list;
        }
        if (intl.StartsWith(Phones.DefaultCountryCode, StringComparison.Ordinal))
            list.Add("0" + intl[Phones.DefaultCountryCode.Length..]);
        list.Add("+" + intl);
        if (intl.Length >= 7) list.Add(intl[^7..]);
        return list.Distinct().ToList();
    }

    static readonly Regex RecordUrl = new(@"/lightning/r/(?:(?<obj>[A-Za-z0-9_]+)/)?(?<id>[a-zA-Z0-9]{15}(?:[a-zA-Z0-9]{3})?)(?:/view|/|$|\?)",
        RegexOptions.CultureInvariant);

    /// <summary>The record id in a Lightning record URL, or null.</summary>
    public static (string Id, string? ObjectType)? ParseRecordUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = RecordUrl.Match(url);
        if (!m.Success) return null;
        var obj = m.Groups["obj"].Success ? m.Groups["obj"].Value : null;
        return (m.Groups["id"].Value, obj ?? ObjectFromId(m.Groups["id"].Value));
    }

    /// <summary>Standard key prefixes (the first 3 chars of every id).</summary>
    public static string? ObjectFromId(string id) => id.Length < 3 ? null : id[..3] switch
    {
        "003" => "Contact",
        "00Q" => "Lead",
        "001" => "Account",
        "006" => "Opportunity",
        "00T" => "Task",
        "00U" => "Event",
        _ => null,
    };

    public static bool SameId(string? a, string? b) =>
        a is { Length: >= 15 } && b is { Length: >= 15 } && string.Equals(a[..15], b[..15], StringComparison.Ordinal);

    /// <summary>
    /// Contact/Lead rows from the links found on a search results page,
    /// de-duplicated by id, in page order. Links with no usable name are kept
    /// only if nothing better appears for that id.
    /// </summary>
    public static IReadOnlyList<SfRecordRef> ParseSearchResults(IEnumerable<(string Href, string Text)> links, string origin)
    {
        var byId = new Dictionary<string, SfRecordRef>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (href, text) in links)
        {
            if (ParseRecordUrl(href) is not { } parsed) continue;
            var obj = parsed.ObjectType ?? ObjectFromId(parsed.Id);
            if (obj is not ("Contact" or "Lead")) continue;
            var key = parsed.Id[..15];
            var name = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            var url = $"{origin.TrimEnd('/')}/lightning/r/{obj}/{parsed.Id}/view";
            if (!byId.TryGetValue(key, out var existing))
            {
                byId[key] = new SfRecordRef(parsed.Id, obj, name, url);
                order.Add(key);
            }
            else if (existing.Name.Length == 0 && name.Length > 0)
                byId[key] = existing with { Name = name };
        }
        return order.Select(k => byId[k]).ToList();
    }

    /// <summary>
    /// The one-page-load search URL Lightning's own search box navigates to
    /// (the base64 forceSearch:search component def). A fallback when the
    /// search box itself can't be found. [verify-org]
    /// </summary>
    public static string SearchUrl(string origin, string term)
    {
        var json = JsonSerializer.Serialize(new
        {
            componentDef = "forceSearch:searchPageDesktop",
            attributes = new { term, scopeMap = new { type = "TOP_RESULTS" }, groupId = "DEFAULT" },
            state = new { },
        });
        return $"{origin.TrimEnd('/')}/one/one.app#{Convert.ToBase64String(Encoding.UTF8.GetBytes(json))}";
    }
}
