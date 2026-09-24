using System.IO;

namespace Palon.Salesforce;

sealed record UndoResult(bool Ok, string Message);

/// <summary>
/// Undo from the audit log. This is the single delete path in Palon: it
/// deletes only a Task Palon itself created ≤24h ago, on that Task's own page,
/// under a token minted by the Undo button for that one audit entry. Every
/// other flow keeps the global ban (<see cref="SfSafety.IsForbidden"/>).
/// </summary>
static partial class SalesforceAgent
{
    static readonly UndoGate UndoTokens = new();

    static readonly SfLocator[] DeleteButton =
    {
        new() { Role = "button", Name = "Delete" },
        new() { Role = "button", Name = "מחק" },
        new() { Role = "menuitem", Name = "Delete" },
        new() { Role = "menuitem", Name = "מחק" },
    };

    static readonly SfLocator[] MoreActions =
    {
        new() { Role = "button", Name = "Show more actions" },
        new() { Role = "button", Name = "הצג פעולות נוספות" },
        new() { Role = "button", Name = "More Actions", NameContains = true },
    };

    /// <summary>Called only by an Undo button, for the entry it is shown on.</summary>
    public static string ApproveUndo(string auditEntryId)
    {
        Audit.WriteUndo("undo-approve", auditEntryId, null);
        return UndoTokens.Issue(auditEntryId);
    }

    sealed class RestoreGuard(string recordId) : ICommitGuard
    {
        int _commits;
        public bool DryRun => false;

        public Task<string?> CheckAsync(string currentUrl, string elementName, CancellationToken ct)
        {
            if (SfSafety.IsForbidden(elementName)) return Task.FromResult<string?>($"\"{elementName}\" is never clicked");
            if (!SfPhones.SameId(SfPhones.ParseRecordUrl(currentUrl)?.Id, recordId))
                return Task.FromResult<string?>("record on screen is not the one being restored");
            return Task.FromResult<string?>(_commits++ == 0 ? null : "more than one save");
        }
    }

    public static async Task<UndoResult> UndoAsync(string auditEntryId, string? undoToken, CancellationToken ct = default)
    {
        var entries = AuditLog.Read(out var undone);
        var entry = entries.FirstOrDefault(e => e.Id == auditEntryId);
        if (UndoPolicy.Check(entry, DateTime.UtcNow, undone.Contains(auditEntryId)) is { } why)
        {
            Audit.WriteUndo("undo-refused", auditEntryId, new { why });
            return new UndoResult(false, why);
        }
        if (!UndoTokens.TryConsume(auditEntryId, undoToken, out var reason))
        {
            Audit.WriteUndo("undo-refused", auditEntryId, new { why = reason });
            return new UndoResult(false, reason);
        }
        if (!await OneJob.WaitAsync(0, ct)) return new UndoResult(false, "Palon כבר עובד ב-Salesforce");
        try
        {
            await using var page = await EdgeBrowser.SalesforcePageAsync(SfOrg.Saved, ct);
            Audit.WriteUndo("undo-start", auditEntryId, new { entry!.Kind, entry.CreatedUrl, entry.Field });
            var result = entry.Kind == SfChangeKind.UpdateField
                ? await RestoreFieldAsync(page, entry, ct)
                : await DeleteCreatedAsync(page, entry, ct);
            Audit.WriteUndo(result.Ok ? "undo-done" : "undo-failed", auditEntryId, new { result.Message });
            if (!result.Ok) await page.BringToFrontAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is CdpException or IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            Audit.WriteUndo("undo-failed", auditEntryId, new { ex.Message });
            return new UndoResult(false, ex is OperationCanceledException ? "בוטל" : ex.Message);
        }
        finally
        {
            OneJob.Release();
        }
    }

    static async Task<UndoResult> DeleteCreatedAsync(CdpPage page, SfAuditChange entry, CancellationToken ct)
    {
        await page.NavigateAsync(entry.CreatedUrl!, ct);
        await page.SettleAsync(ct);
        if (!UndoPolicy.OnCreatedItem(entry, await page.UrlAsync(ct)))
            return new UndoResult(false, SfSafety.IsLoginUrl(await page.UrlAsync(ct)) ? "Salesforce מבקש התחברות" : "הפריט ש-Palon יצר לא נפתח (אולי כבר נמחק)");

        var runner = new SkillRunner(page, Ask, new LoopDetector(), s => Progress?.Invoke(s));
        var (delete, _) = await runner.ResolveAsync(DeleteButton, ct);
        if (delete is null)
        {
            var (more, _) = await runner.ResolveAsync(MoreActions, ct, waitMs: 1500);
            if (more is not null)
            {
                await page.ClickAsync(more.Value, ct);
                await page.SettleAsync(ct, 3000);
                (delete, _) = await runner.ResolveAsync(DeleteButton, ct);
            }
        }
        if (delete is null) return new UndoResult(false, "לא נמצא כפתור מחיקה בפריט — מחק ידנית");
        if (!UndoPolicy.OnCreatedItem(entry, await page.UrlAsync(ct))) return new UndoResult(false, "הדף השתנה — עצרתי");
        Progress?.Invoke("מוחק את הפריט ש-Palon יצר…");
        await page.ClickAsync(delete.Value, ct);
        await page.SettleAsync(ct, 4000);

        // The confirmation modal: its own "Delete" button, scoped to the modal.
        var (confirm, _) = await runner.ResolveAsync(new[] { new SfLocator { Role = "button", Name = "Delete" }, new SfLocator { Role = "button", Name = "מחק" } }, ct);
        if (confirm is null) return new UndoResult(false, "חלון האישור של המחיקה לא הופיע — בדוק ידנית");
        if (!UndoPolicy.OnCreatedItem(entry, await page.UrlAsync(ct))) return new UndoResult(false, "הדף השתנה — עצרתי");
        await page.ClickAsync(confirm.Value, ct);
        await page.SettleAsync(ct, 6000);
        var gone = await page.SuccessToastAsync(ct) || !UndoPolicy.OnCreatedItem(entry, await page.UrlAsync(ct));
        return gone ? new UndoResult(true, $"בוטל: {entry.Description}") : new UndoResult(false, "המחיקה לא אומתה — בדוק ידנית");
    }

    static async Task<UndoResult> RestoreFieldAsync(CdpPage page, SfAuditChange entry, CancellationToken ct)
    {
        await page.NavigateAsync(entry.RecordUrl!, ct);
        if (!SfPhones.SameId(SfPhones.ParseRecordUrl(await page.UrlAsync(ct))?.Id, entry.RecordId))
            return new UndoResult(false, "הרשומה לא נפתחה");
        var restore = new SfChange(SfChangeKind.UpdateField, "undo", Field: entry.Field, NewValue: entry.OldValue);
        var (skill, vars) = SkillFor(restore);
        var run = await new SkillRunner(page, Ask, new LoopDetector(), s => Progress?.Invoke(s))
            .RunAsync(skill, vars, new RestoreGuard(entry.RecordId!), entry.RecordId, ct);
        if (!run.Ok) return new UndoResult(false, run.Error ?? "השחזור נכשל");
        var verified = await VerifyAsync(page, restore, ct);
        return new UndoResult(verified, verified ? $"שוחזר: {entry.Field} = \"{entry.OldValue}\"" : "נשמר אבל לא אומת — בדוק ידנית");
    }
}
