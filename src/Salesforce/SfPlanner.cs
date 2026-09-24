using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Palon.Notes;

namespace Palon.Salesforce;

/// <summary>User-tunable wording of what gets written. Defaults are Hebrew.</summary>
sealed record SfPlanOptions(
    string CallSubject = "שיחה",
    string FollowUpSubject = "לחזור ללקוח",
    int MaxCommentsChars = 30_000, // Task.Description holds 32k; keep margin
    string? StatusField = null,    // e.g. "Status" — only when the user asked for a field change
    string? StatusValue = null);

/// <summary>
/// Pure: turns a call note (and the callback the user accepted or Palon
/// heard) into a typed plan. No model in the loop — what is written is
/// exactly the summary the user already saw, so the preview is honest.
/// </summary>
static class SfPlanner
{
    public static SfPlan Build(
        CallNote note,
        DateTime? callbackLocal,
        SfRecordRef? record,
        IReadOnlyList<SfRecordRef> candidates,
        DateTime nowUtc,
        SfPlanOptions? options = null)
    {
        options ??= new SfPlanOptions();
        var callDate = DateOnly.FromDateTime(note.StartedUtc.ToLocalTime());
        var changes = new List<SfChange>
        {
            new(SfChangeKind.LogCall, options.CallSubject, Comments(note, options.MaxCommentsChars), callDate),
        };

        // The callback: an explicit choice wins; otherwise the one heard on
        // the call, but only while it is still a live proposal or accepted.
        var due = callbackLocal
            ?? (note.ProposedCallback is { State: "proposed" or "accepted" } p ? p.WhenUtc.ToLocalTime() : (DateTime?)null);
        if (due is { } when && when.ToUniversalTime() > nowUtc.AddMinutes(-5))
        {
            var reason = note.ProposedCallback?.Reason;
            var subject = string.IsNullOrWhiteSpace(reason)
                ? options.FollowUpSubject
                : $"{options.FollowUpSubject}: {OneLine(reason, 80)}";
            changes.Add(new SfChange(SfChangeKind.NewTask, subject, Date: DateOnly.FromDateTime(when)));
        }

        if (!string.IsNullOrWhiteSpace(options.StatusField) && !string.IsNullOrWhiteSpace(options.StatusValue))
            changes.Add(new SfChange(SfChangeKind.UpdateField, options.StatusField!, Field: options.StatusField, NewValue: options.StatusValue));

        // A unique match is picked; several are left for the user to choose.
        var chosen = record ?? (candidates.Count == 1 ? candidates[0] : null);
        return new SfPlan(note.Id, note.Number ?? "", chosen, candidates, changes, nowUtc);
    }

    /// <summary>The Comments body: the summary, or a trimmed transcript head
    /// when no summary exists. Never empty.</summary>
    public static string Comments(CallNote note, int max)
    {
        var text = !string.IsNullOrWhiteSpace(note.Summary)
            ? note.Summary!.Trim()
            : !string.IsNullOrWhiteSpace(note.Transcript)
                ? "תמליל (ללא סיכום):\n" + note.Transcript.Trim()
                : "שיחה ללא תמליל";
        text = text.Replace("\r\n", "\n");
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    static string OneLine(string s, int max)
    {
        var one = Regex.Replace(s, @"\s+", " ").Trim();
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }

    /// <summary>Hebrew preview lines, one per change.</summary>
    public static string Describe(SfChange c) => c.Kind switch
    {
        SfChangeKind.LogCall => $"תיעוד שיחה · {c.Subject} · {c.Date?.ToString("d.M.yyyy", CultureInfo.InvariantCulture)}",
        SfChangeKind.NewTask => $"משימת המשך · {c.Subject} · עד {c.Date?.ToString("d.M.yyyy", CultureInfo.InvariantCulture)}",
        SfChangeKind.UpdateField => $"עדכון שדה · {c.Field}: {(c.OldValue is null ? "" : c.OldValue + " ← ")}{c.NewValue}",
        _ => c.Kind.ToString(),
    };
}

/// <summary>
/// The code-enforced approval gate. A token is minted only by the preview's
/// Approve button, is bound to the plan hash it was shown for, expires, and
/// is consumed on first use. The executor cannot click any commit button
/// without a consumed token for the exact plan it is running.
/// </summary>
sealed class ApprovalGate
{
    sealed record Grant(string PlanHash, DateTime ExpiresUtc);

    readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    readonly object _lock = new();
    readonly Func<DateTime> _clock;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public ApprovalGate(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public string Issue(SfPlan plan)
    {
        if (!plan.Ready) throw new InvalidOperationException("plan has no record or no changes");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock) _grants[token] = new Grant(plan.Hash, _clock() + Lifetime);
        return token;
    }

    /// <summary>True exactly once per token, and only for the plan it was
    /// issued for. A mismatched attempt burns the token too.</summary>
    public bool TryConsume(SfPlan plan, string? token, out string reason)
    {
        lock (_lock)
        {
            if (token is null || !_grants.Remove(token, out var grant)) { reason = "אין אישור תקף"; return false; }
            if (grant.ExpiresUtc < _clock()) { reason = "האישור פג תוקף"; return false; }
            if (!string.Equals(grant.PlanHash, plan.Hash, StringComparison.Ordinal)) { reason = "התוכנית השתנתה אחרי האישור"; return false; }
        }
        reason = "";
        return true;
    }
}

/// <summary>Hard rules no skill, heal or model output can bypass.</summary>
static class SfSafety
{
    static readonly Regex Forbidden = new(
        @"\b(delete|merge|mass|convert|remove|deactivate)\b|מחק|מחיקה|מזג|המר|הסר",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex CommitName = new(
        @"^\s*(save|save\s*&\s*new|submit|שמור|שמירה|שלח)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Never clicked, in any mode.</summary>
    public static bool IsForbidden(string? elementName) =>
        elementName is not null && Forbidden.IsMatch(elementName);

    /// <summary>A click that persists data — only allowed through the gate.</summary>
    public static bool LooksLikeCommit(string? elementName) =>
        elementName is not null && CommitName.IsMatch(elementName);

    static readonly Regex Login = new(
        @"login\.salesforce\.com|/_ui/identity/verification|/secur/|\.my\.salesforce\.com/\?|/login",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsLoginUrl(string? url) => url is not null && Login.IsMatch(url);
}
