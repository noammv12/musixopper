using System.Globalization;
using System.IO;
using System.Text.Json;
using Palon.Notes;

namespace Palon.Salesforce;

enum ChangeStatus { Verified, SavedUnverified, DryRun, Failed, NotRun }

sealed record ChangeResult(SfChange Change, ChangeStatus Status, string? Detail = null, string? UndoNote = null);

sealed record ExecuteResult(bool Ok, IReadOnlyList<ChangeResult> Changes, string? Error = null, bool NeedsLogin = false, string? RecordUrl = null);

sealed record PrepareResult(SfPlan? Plan, string? Error = null, bool NeedsLogin = false);

/// <summary>
/// "Log to Salesforce": find → plan → (user approves) → execute → verify →
/// audit. The only way to write is <see cref="ExecuteAsync"/> with a token
/// from <see cref="Approve"/>, bound to the plan's hash and single-use.
/// Palon never deletes, never touches a record outside the plan, and
/// reports anything it could not verify as not saved.
/// </summary>
static class SalesforceAgent
{
    static readonly ApprovalGate Gate = new();
    static readonly SemaphoreSlim OneJob = new(1, 1);

    /// <summary>The model call used for step recovery (swappable in tests).</summary>
    public static Func<string, string, CancellationToken, Task<string?>> Ask { get; set; } = AiChat.LocateElementAsync;

    public static event Action<string>? Progress;

    // ---- prepare (read-only) -----------------------------------------------------------

    public static async Task<PrepareResult> PrepareAsync(CallNote note, DateTime? callbackLocal = null,
        SfPlanOptions? options = null, CancellationToken ct = default)
    {
        if (!await OneJob.WaitAsync(0, ct)) return new PrepareResult(null, "Palon כבר עובד ב-Salesforce");
        try
        {
            await using var page = await EdgeBrowser.SalesforcePageAsync(SfOrg.Saved, ct);
            var origin = await RememberOrgAsync(page, ct);
            if (origin is null)
                return new PrepareResult(null, "Salesforce לא פתוח או שאינך מחובר. התחבר בחלון ה-Edge של Palon.", NeedsLogin: true);

            var (records, error, login) = await FindByPhoneAsync(page, origin, note.Number, ct);
            if (error is not null) return new PrepareResult(null, error, login);
            var plan = SfPlanner.Build(note, callbackLocal, null, records, DateTime.UtcNow, options);
            Audit.Write("prepare", plan, new { found = records.Count });
            return new PrepareResult(plan);
        }
        catch (Exception ex) when (ex is CdpException or IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException)
        {
            Log.Write($"Salesforce: prepare failed: {ex.Message}");
            return new PrepareResult(null, ex is CdpException ? ex.Message : "אין חיבור ל-Edge של Palon");
        }
        finally
        {
            OneJob.Release();
        }
    }

    static async Task<string?> RememberOrgAsync(CdpPage page, CancellationToken ct)
    {
        var url = await page.UrlAsync(ct);
        if (SfSafety.IsLoginUrl(url)) return null;
        var origin = SfOrg.Origin(url);
        if (origin is not null && origin != SfOrg.Saved) SfOrg.Saved = origin;
        return origin;
    }

    /// <summary>Contacts/Leads for a phone: local form, then +972, then the last 7 digits.</summary>
    static async Task<(IReadOnlyList<SfRecordRef> Records, string? Error, bool Login)> FindByPhoneAsync(
        CdpPage page, string origin, string? phone, CancellationToken ct)
    {
        var terms = SfPhones.SearchTerms(phone);
        if (terms.Count == 0) return (Array.Empty<SfRecordRef>(), "לשיחה אין מספר טלפון", false);
        var skill = SkillStore.Load("FindRecordByPhone");
        foreach (var term in terms)
        {
            Progress?.Invoke($"מחפש {term} ב-Salesforce…");
            var runner = new SkillRunner(page, Ask, new LoopDetector(), s => Progress?.Invoke(s));
            var run = await runner.RunAsync(skill, new Dictionary<string, string> { ["term"] = term }, ReadOnlyGuard.Instance, null, ct);
            if (run.NeedsLogin) return (Array.Empty<SfRecordRef>(), run.Error, true);
            if (!run.Ok)
            {
                // The search box itself was not usable: go to the results page directly. [verify-org]
                await page.NavigateAsync(SfPhones.SearchUrl(origin, term), ct);
            }
            await page.SettleAsync(ct, 10000);
            var records = await ReadResultsAsync(page, origin, ct);
            if (records.Count > 0) return (records, null, false);
        }
        return (Array.Empty<SfRecordRef>(), null, false);
    }

    static async Task<IReadOnlyList<SfRecordRef>> ReadResultsAsync(CdpPage page, string origin, CancellationToken ct)
    {
        // A lone record page means Salesforce jumped straight to the match.
        if (SfPhones.ParseRecordUrl(await page.UrlAsync(ct)) is { } direct && direct.ObjectType is "Contact" or "Lead")
        {
            var title = (await page.EvalAsync("document.title", ct))?.GetString() ?? "";
            return new[] { new SfRecordRef(direct.Id, direct.ObjectType!, title.Split('|')[0].Trim(), $"{origin}/lightning/r/{direct.ObjectType}/{direct.Id}/view") };
        }
        var js = CdpPage.DeepQueryJs + """
            __palonDeep('a[href*="/lightning/r/"], a[data-recordid]').filter(__palonVisible)
              .map(a => [a.getAttribute('href') || ('/lightning/r/' + a.getAttribute('data-recordid') + '/view'),
                         (a.getAttribute('title') || a.innerText || '').trim()])
            """;
        var v = await page.EvalAsync(js, ct);
        var links = new List<(string, string)>();
        if (v is { ValueKind: JsonValueKind.Array } arr)
            foreach (var pair in arr.EnumerateArray())
                links.Add((pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
        return SfPhones.ParseSearchResults(links, origin);
    }

    // ---- approve -------------------------------------------------------------------------

    /// <summary>Called only by the preview's Approve button.</summary>
    public static string Approve(SfPlan plan)
    {
        var token = Gate.Issue(plan);
        Audit.Write("approve", plan, null);
        return token;
    }

    // ---- execute -----------------------------------------------------------------------

    sealed class PlanGuard(SfPlan plan, bool dryRun) : ICommitGuard
    {
        public bool DryRun => dryRun;
        public int Commits { get; private set; }

        public Task<string?> CheckAsync(string currentUrl, string elementName, CancellationToken ct)
        {
            if (SfSafety.IsForbidden(elementName)) return Task.FromResult<string?>($"\"{elementName}\" is never clicked");
            if (!SfPhones.SameId(SfPhones.ParseRecordUrl(currentUrl)?.Id, plan.Record?.Id))
                return Task.FromResult<string?>("record on screen is not the planned record");
            if (Commits >= plan.Changes.Count) return Task.FromResult<string?>("more saves than planned changes");
            Commits++;
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Runs an approved plan. Refuses without a valid, unused token for this
    /// exact plan. <paramref name="dryRun"/> runs every step up to — not
    /// including — Save (no token needed; nothing is written).
    /// </summary>
    public static async Task<ExecuteResult> ExecuteAsync(SfPlan plan, string? approvalToken, bool dryRun = false, CancellationToken ct = default)
    {
        if (!plan.Ready) return new ExecuteResult(false, Array.Empty<ChangeResult>(), "לא נבחרה רשומה");
        if (!dryRun && !Gate.TryConsume(plan, approvalToken, out var reason))
        {
            Audit.Write("refused", plan, new { reason });
            return new ExecuteResult(false, Array.Empty<ChangeResult>(), reason);
        }
        if (!await OneJob.WaitAsync(0, ct)) return new ExecuteResult(false, Array.Empty<ChangeResult>(), "Palon כבר עובד ב-Salesforce");
        var results = new List<ChangeResult>();
        try
        {
            await using var page = await EdgeBrowser.SalesforcePageAsync(SfOrg.Saved, ct);
            var record = plan.Record!;
            var guard = new PlanGuard(plan, dryRun);
            var loop = new LoopDetector();
            Audit.Write(dryRun ? "dry-run-start" : "execute-start", plan, null);

            foreach (var change in plan.Changes)
            {
                ct.ThrowIfCancellationRequested();
                Progress?.Invoke(SfPlanner.Describe(change));
                await page.NavigateAsync(record.Url, ct);
                if (!SfPhones.SameId(SfPhones.ParseRecordUrl(await page.UrlAsync(ct))?.Id, record.Id))
                {
                    var login = SfSafety.IsLoginUrl(await page.UrlAsync(ct));
                    results.Add(new ChangeResult(change, ChangeStatus.Failed, login ? "נדרשת התחברות" : "הרשומה לא נפתחה"));
                    if (login) { await page.BringToFrontAsync(ct); return Finish(false, "Salesforce מבקש התחברות", needsLogin: true); }
                    break;
                }

                var (skill, vars) = SkillFor(change);
                string? oldValue = null;
                if (change.Kind == SfChangeKind.UpdateField)
                    oldValue = await ReadFieldAsync(page, change.Field!, ct);

                var runner = new SkillRunner(page, Ask, loop, s => Progress?.Invoke(s));
                var run = await runner.RunAsync(skill, vars, guard, record.Id, ct);
                Audit.Write("skill", plan, new { run.Skill, run.Ok, run.Error, steps = run.Steps.Select(s => $"{s.Index}:{s.Outcome}:{s.Detail}") });
                if (run.NeedsLogin)
                {
                    await page.BringToFrontAsync(ct);
                    results.Add(new ChangeResult(change, ChangeStatus.Failed, "נדרשת התחברות"));
                    return Finish(false, "Salesforce מבקש התחברות", needsLogin: true);
                }
                if (!run.Ok)
                {
                    await page.BringToFrontAsync(ct);
                    results.Add(new ChangeResult(change, ChangeStatus.Failed, run.Error));
                    break; // later changes stay NotRun; the tab is left for the user to see
                }
                if (dryRun)
                {
                    results.Add(new ChangeResult(change, ChangeStatus.DryRun, run.Steps.LastOrDefault()?.Detail));
                    continue;
                }

                var createdUrl = await CreatedRecordUrlAsync(page, ct);
                var verified = await VerifyAsync(page, change, ct);
                var undo = change.Kind switch
                {
                    SfChangeKind.UpdateField => $"להחזרה: {change.Field} = \"{oldValue ?? "?"}\"",
                    _ => createdUrl is not null ? $"להחזרה: למחוק ידנית את {createdUrl}" : "להחזרה: למחוק ידנית את הפעילות ברשומה",
                };
                results.Add(new ChangeResult(change with { OldValue = oldValue },
                    verified ? ChangeStatus.Verified : ChangeStatus.SavedUnverified,
                    verified ? null : "נשמר אבל לא אומת בדף — בדוק ידנית", undo));
            }
            foreach (var c in plan.Changes.Skip(results.Count))
                results.Add(new ChangeResult(c, ChangeStatus.NotRun));
            var ok = results.All(r => r.Status is ChangeStatus.Verified or ChangeStatus.DryRun);
            return Finish(ok, ok ? null : results.FirstOrDefault(r => r.Status == ChangeStatus.Failed)?.Detail);
        }
        catch (Exception ex) when (ex is CdpException or IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            Log.Write($"Salesforce: execute failed: {ex.Message}");
            foreach (var c in plan.Changes.Skip(results.Count)) results.Add(new ChangeResult(c, ChangeStatus.NotRun));
            return Finish(false, ex is OperationCanceledException ? "בוטל" : ex.Message);
        }
        finally
        {
            OneJob.Release();
        }

        ExecuteResult Finish(bool ok, string? error, bool needsLogin = false)
        {
            foreach (var c in plan.Changes.Skip(results.Count)) results.Add(new ChangeResult(c, ChangeStatus.NotRun));
            var r = new ExecuteResult(ok, results, error, needsLogin, plan.Record?.Url);
            Audit.Write(dryRun ? "dry-run-result" : "execute-result", plan,
                new { ok, error, changes = results.Select(x => new { kind = x.Change.Kind.ToString(), status = x.Status.ToString(), x.Detail, x.UndoNote }) });
            return r;
        }
    }

    public static (SfSkill Skill, Dictionary<string, string> Vars) SkillFor(SfChange change)
    {
        var name = change.Kind switch
        {
            SfChangeKind.LogCall => "LogCall",
            SfChangeKind.NewTask => "NewTask",
            _ => "UpdateField",
        };
        var skill = SkillStore.Load(name);
        var vars = new Dictionary<string, string> { ["subject"] = change.Subject };
        if (change.Comments is not null) vars["comments"] = change.Comments;
        if (change.Date is { } d) vars["date"] = SfVars.FormatDate(d, skill.DateFormat);
        if (change.Field is not null) vars["field"] = change.Field;
        if (change.NewValue is not null) vars["value"] = change.NewValue;
        return (skill, vars);
    }

    /// <summary>The Task link in the success toast, for the undo note.</summary>
    static async Task<string?> CreatedRecordUrlAsync(CdpPage page, CancellationToken ct)
    {
        try
        {
            var v = await page.EvalAsync(CdpPage.DeepQueryJs +
                "(__palonDeep('.forceToastMessage a[href*=\"/lightning/r/\"], [role=\"alert\"] a[href*=\"/lightning/r/\"], [role=\"status\"] a[href*=\"/lightning/r/\"]')[0] || {}).href || null", ct);
            return v?.ValueKind == JsonValueKind.String ? v.Value.GetString() : null;
        }
        catch (CdpException)
        {
            return null;
        }
    }

    static async Task<string?> ReadFieldAsync(CdpPage page, string field, CancellationToken ct)
    {
        try
        {
            var ax = await page.AxTreeAsync(ct);
            // Record detail pages expose "Label" as a term and the value next to it; best-effort. [verify-org]
            var idx = ax.FindIndex(n => !n.Ignored && LocatorRanking.NameMatches(new SfLocator { Name = field }, n.Name) && n.Role is "term" or "StaticText" or "LabelText");
            for (var i = idx + 1; idx >= 0 && i < Math.Min(ax.Count, idx + 8); i++)
                if (!ax[i].Ignored && ax[i].Name.Length > 0 && ax[i].Name != ax[idx].Name && !ax[i].Name.StartsWith("Edit ", StringComparison.Ordinal))
                    return ax[i].Name;
        }
        catch (CdpException)
        {
        }
        return null;
    }

    /// <summary>
    /// Reload the record and look for the change in the page itself (the
    /// Activity timeline for calls/tasks, the field for updates). A toast is
    /// not proof.
    /// </summary>
    static async Task<bool> VerifyAsync(CdpPage page, SfChange change, CancellationToken ct)
    {
        await page.ReloadAsync(ct);
        await page.SettleAsync(ct, 15000);
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until)
        {
            var ax = await page.AxTreeAsync(ct);
            var ok = change.Kind switch
            {
                SfChangeKind.UpdateField => AxTree.AnyNameContains(ax, change.NewValue ?? "\u0000"),
                _ => AxTree.AnyNameContains(ax, change.Subject)
                     && (change.Comments is null || AxTree.AnyNameContains(ax, FirstWords(change.Comments))
                         || await TimelineTextContainsAsync(page, FirstWords(change.Comments), ct)),
            };
            if (ok) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    static string FirstWords(string s)
    {
        var one = LocatorRanking.Fold(s);
        return one.Length <= 30 ? one : one[..30];
    }

    static async Task<bool> TimelineTextContainsAsync(CdpPage page, string text, CancellationToken ct)
    {
        try
        {
            // Collapsed timeline items keep their description in the DOM.
            var v = await page.EvalAsync(CdpPage.DeepQueryJs +
                $"__palonDeep('.slds-timeline__item, [class*=\"timeline\"]').some(e => (e.innerText || e.textContent || '').replace(/\\s+/g,' ').includes({JsonSerializer.Serialize(text)}))", ct);
            return v?.ValueKind == JsonValueKind.True;
        }
        catch (CdpException)
        {
            return false;
        }
    }

    public static async Task OpenInEdgeAsync(string? url, CancellationToken ct = default)
    {
        var port = await EdgeBrowser.EnsureAsync(url ?? (SfOrg.Saved is { } o ? o + "/lightning/page/home" : "https://login.salesforce.com"), ct);
        if (url is not null && await EdgeBrowser.LivePortAsync(ct) == port)
        {
            var t = await EdgeBrowser.NewTabAsync(port, url, ct);
            await using var p = await CdpPage.OpenAsync(t.WsUrl, t.Id, ct);
            await p.BringToFrontAsync(ct);
        }
    }
}

/// <summary>Append-only JSONL: %LOCALAPPDATA%\Palon\salesforce\audit.jsonl.</summary>
static class Audit
{
    public static string PathFor => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "salesforce", "audit.jsonl");

    static readonly object Gate = new();

    public static void Write(string evt, SfPlan plan, object? detail)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                evt,
                plan = plan.Hash[..16],
                note = plan.NoteId,
                record = plan.Record?.Id,
                recordName = plan.Record?.Name,
                changes = plan.Changes.Select(c => SfPlanner.Describe(c)),
                detail,
            });
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFor)!);
                File.AppendAllText(PathFor, line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Salesforce: audit write failed: {ex.Message}");
        }
    }
}
