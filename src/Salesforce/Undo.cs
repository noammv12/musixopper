using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Palon.Salesforce;

/// <summary>
/// One saved change as the audit log records it — the unit Undo works on.
/// Written by <see cref="Audit.WriteChange"/> right after a save.
/// </summary>
sealed record SfAuditChange(
    string Id,
    DateTime AtUtc,
    SfChangeKind Kind,
    string Status,        // ChangeStatus name
    string? RecordId,
    string? RecordUrl,
    string? CreatedUrl,   // LogCall/NewTask: the Task Palon created (from the success toast)
    string? Field,
    string? OldValue,
    string? NewValue,
    string Description);

/// <summary>
/// Pure rules for what may be undone. The only delete Palon ever performs is
/// the undo of an item it created itself, ≤24h ago, found by the exact
/// record link it recorded at save time; field updates are undone by writing
/// the recorded old value back.
/// </summary>
static class UndoPolicy
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Task key prefix: logged calls and tasks are both Task records.</summary>
    const string TaskPrefix = "00T";

    /// <summary>Null = may be undone; otherwise why not (Hebrew, shown to the user).</summary>
    public static string? Check(SfAuditChange? entry, DateTime nowUtc, bool alreadyUndone)
    {
        if (entry is null) return "הפעולה לא נמצאה ביומן";
        if (alreadyUndone) return "הפעולה כבר בוטלה";
        if (entry.Status is not (nameof(ChangeStatus.Verified) or nameof(ChangeStatus.SavedUnverified)))
            return "הפעולה לא נשמרה — אין מה לבטל";
        if (nowUtc - entry.AtUtc > Window || entry.AtUtc > nowUtc + TimeSpan.FromMinutes(5))
            return "אפשר לבטל רק פעולות מ-24 השעות האחרונות";
        switch (entry.Kind)
        {
            case SfChangeKind.LogCall:
            case SfChangeKind.NewTask:
                var created = SfPhones.ParseRecordUrl(entry.CreatedUrl);
                if (created is null) return "אין קישור לפריט ש-Palon יצר — בטל ידנית";
                if (!created.Value.Id.StartsWith(TaskPrefix, StringComparison.Ordinal)) return "הקישור אינו לפעילות — Palon לא ימחק אותו";
                if (SfPhones.SameId(created.Value.Id, entry.RecordId)) return "הקישור מצביע על הרשומה עצמה — Palon לא ימחק אותה";
                return null;
            case SfChangeKind.UpdateField:
                if (entry.Field is null || entry.OldValue is null) return "הערך הקודם לא נקרא — בטל ידנית";
                if (entry.RecordUrl is null) return "אין קישור לרשומה";
                return null;
            default:
                return "סוג פעולה לא מוכר";
        }
    }

    /// <summary>The delete is legal only on the created item's own page.</summary>
    public static bool OnCreatedItem(SfAuditChange entry, string currentUrl) =>
        SfPhones.ParseRecordUrl(entry.CreatedUrl) is { } created
        && SfPhones.SameId(SfPhones.ParseRecordUrl(currentUrl)?.Id, created.Id);
}

/// <summary>
/// The undo's own approval gate: a token is minted by the Undo button for one
/// audit entry id, expires, and is consumed on first use. It never authorizes
/// anything but undoing that entry.
/// </summary>
sealed class UndoGate
{
    sealed record Grant(string EntryId, DateTime ExpiresUtc);

    readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    readonly object _lock = new();
    readonly Func<DateTime> _clock;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public UndoGate(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public string Issue(string entryId)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock) _grants[token] = new Grant(entryId, _clock() + Lifetime);
        return token;
    }

    public bool TryConsume(string entryId, string? token, out string reason)
    {
        lock (_lock)
        {
            if (token is null || !_grants.Remove(token, out var grant)) { reason = "אין אישור תקף לביטול"; return false; }
            if (grant.ExpiresUtc < _clock()) { reason = "אישור הביטול פג תוקף"; return false; }
            if (!string.Equals(grant.EntryId, entryId, StringComparison.Ordinal)) { reason = "האישור ניתן לפעולה אחרת"; return false; }
        }
        reason = "";
        return true;
    }
}

/// <summary>Reads the audit log back: saved changes and which were undone.</summary>
static class AuditLog
{
    public static IReadOnlyList<SfAuditChange> Changes(IEnumerable<string> lines, out HashSet<string> undone)
    {
        var list = new List<SfAuditChange>();
        undone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                var evt = S(r, "evt");
                if (evt == "undo-done" && S(r, "ref") is { } done) undone.Add(done);
                if (evt != "change" || S(r, "id") is not { } id) continue;
                if (!Enum.TryParse<SfChangeKind>(S(r, "kind"), out var kind)) continue;
                if (!DateTime.TryParse(S(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) continue;
                list.Add(new SfAuditChange(id, at.ToUniversalTime(), kind, S(r, "status") ?? "", S(r, "record"), S(r, "recordUrl"),
                    S(r, "createdUrl"), S(r, "field"), S(r, "oldValue"), S(r, "newValue"), S(r, "description") ?? kind.ToString()));
            }
            catch (JsonException)
            {
            }
        }
        return list;
    }

    public static IReadOnlyList<SfAuditChange> Read(out HashSet<string> undone)
    {
        try
        {
            return Changes(File.Exists(Audit.PathFor) ? File.ReadAllLines(Audit.PathFor) : Array.Empty<string>(), out undone);
        }
        catch (IOException)
        {
            undone = new HashSet<string>();
            return Array.Empty<SfAuditChange>();
        }
    }

    /// <summary>Recent changes that can still be undone, newest first.</summary>
    public static IReadOnlyList<SfAuditChange> Undoable(DateTime nowUtc)
    {
        var all = Read(out var undone);
        return all.Where(c => UndoPolicy.Check(c, nowUtc, undone.Contains(c.Id)) is null).OrderByDescending(c => c.AtUtc).ToList();
    }

    static string? S(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
