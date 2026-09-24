using System.Text.RegularExpressions;

namespace Palon.Salesforce;

enum StepOutcome { Replayed, Healed, Skipped, DryRunStopped, Failed }

sealed record StepReport(int Index, string Intent, StepOutcome Outcome, string? Detail = null);

sealed record SkillRun(string Skill, bool Ok, IReadOnlyList<StepReport> Steps, string? Error = null, bool NeedsLogin = false)
{
    public bool Healed => Steps.Any(s => s.Outcome == StepOutcome.Healed);
}

/// <summary>
/// Decides whether a commit click may happen. Implemented by the agent from a
/// consumed approval token; the runner asks it immediately before clicking.
/// </summary>
interface ICommitGuard
{
    /// <summary>Null = allowed; otherwise why not.</summary>
    Task<string?> CheckAsync(string currentUrl, string elementName, CancellationToken ct);
    bool DryRun { get; }
}

sealed class ReadOnlyGuard : ICommitGuard
{
    public static readonly ReadOnlyGuard Instance = new();
    public Task<string?> CheckAsync(string currentUrl, string elementName, CancellationToken ct) =>
        Task.FromResult<string?>("read-only run");
    public bool DryRun => false;
}

/// <summary>
/// Replay-then-heal (Stagehand's pattern, adapted for Lightning): each step
/// tries its cached locators in order and needs exactly one visible, enabled
/// match. On 0 or &gt;1 matches it asks the model once, with the scoped ref
/// snapshot, for one element or null; the healed role+name locator is
/// cached only after the step's post-condition passes.
/// </summary>
sealed class SkillRunner
{
    readonly CdpPage _page;
    readonly Func<string, string, CancellationToken, Task<string?>> _ask;
    readonly LoopDetector _loop;
    readonly Action<string>? _progress;

    public SkillRunner(CdpPage page, Func<string, string, CancellationToken, Task<string?>> ask, LoopDetector loop, Action<string>? progress = null)
    {
        _page = page;
        _ask = ask;
        _loop = loop;
        _progress = progress;
    }

    /// <summary>
    /// Runs <paramref name="skill"/>. <paramref name="expectedRecordId"/>, when
    /// set, is asserted against the URL before every mutating step.
    /// </summary>
    public async Task<SkillRun> RunAsync(SfSkill skill, IReadOnlyDictionary<string, string> vars, ICommitGuard guard,
        string? expectedRecordId, CancellationToken ct)
    {
        var reports = new List<StepReport>();
        var learned = false;
        for (var i = 0; i < skill.Steps.Count; i++)
        {
            var step = skill.Steps[i];
            var intent = SfVars.Apply(step.Intent, vars);
            _progress?.Invoke(intent);
            if (_loop.OverBudget) return Fail("too many actions in one job");

            var url = await _page.UrlAsync(ct);
            if (SfSafety.IsLoginUrl(url)) return new SkillRun(skill.Skill, false, reports, "Salesforce מבקש התחברות", NeedsLogin: true);

            if (step.Pre is { } pre && !await ConditionAsync(pre, vars, ct, quick: true))
            {
                if (step.Optional) { reports.Add(new StepReport(i, intent, StepOutcome.Skipped, "precondition")); continue; }
                return Fail($"precondition failed before \"{intent}\"");
            }

            var mutating = step.Method is "click" or "fill" or "select" or "press";
            if (mutating && expectedRecordId is not null && !SfPhones.SameId(SfPhones.ParseRecordUrl(url)?.Id, expectedRecordId))
                return Fail("the open record is not the one in the plan — stopped");

            if (step.Method == "navigate")
            {
                await _page.NavigateAsync(SfVars.Apply(step.Args.FirstOrDefault() ?? "", vars), ct);
                reports.Add(new StepReport(i, intent, StepOutcome.Replayed));
                continue;
            }
            if (step.Method == "press" && step.Locators.Count == 0)
            {
                await _page.PressAsync(step.Args.FirstOrDefault() ?? "Enter", ct);
                _loop.RecordAction(LoopDetector.ActionHash("press", null, step.Args.FirstOrDefault()));
                await _page.SettleAsync(ct);
                if (step.Post is { } p0 && !await ConditionAsync(p0, vars, ct)) return Fail($"\"{intent}\" had no effect");
                reports.Add(new StepReport(i, intent, StepOutcome.Replayed));
                continue;
            }
            if (step.Method == "expect")
            {
                if (step.Post is { } pe && !await ConditionAsync(pe, vars, ct)) return Fail($"expected state not reached: {intent}");
                reports.Add(new StepReport(i, intent, StepOutcome.Replayed));
                continue;
            }

            // ---- resolve the element: replay, then heal -----------------------------
            var locators = step.Locators.Select(l => SfVars.Apply(l, vars)).ToList();
            var (element, via) = await ResolveAsync(locators, ct);
            var healed = false;
            if (element is null)
            {
                if (step.Optional) { reports.Add(new StepReport(i, intent, StepOutcome.Skipped, "not present")); continue; }
                if (!_loop.TryUseRecovery()) return Fail($"could not find the element for \"{intent}\" (recovery budget used)");
                element = await HealAsync(intent, step.Method, ct);
                if (element is null) return Fail($"could not find the element for \"{intent}\"");
                healed = true;
            }
            var facts = await _page.FactsAsync(element.Value, ct);

            // ---- hard safety, in code --------------------------------------------------
            if (SfSafety.IsForbidden(facts.Name) || SfSafety.IsForbidden(facts.AriaLabel))
                return Fail($"refused to touch \"{facts.Name}\"");
            var isCommit = step.Commit || (step.Method == "click" && SfSafety.LooksLikeCommit(facts.Name));
            if (isCommit)
            {
                if (guard.DryRun)
                {
                    reports.Add(new StepReport(i, intent, StepOutcome.DryRunStopped, $"would click \"{facts.Name}\""));
                    await _page.PressAsync("Escape", ct); // leave the composer unsaved
                    if (learned) SkillStore.Save(skill);
                    return new SkillRun(skill.Skill, true, reports);
                }
                if (await guard.CheckAsync(await _page.UrlAsync(ct), facts.Name ?? "", ct) is { } refusal)
                    return Fail("save blocked: " + refusal);
            }

            // ---- act ----------------------------------------------------------------------
            var arg = step.Args.Count > 0 ? SfVars.Apply(step.Args[0], vars) : null;
            if (arg is not null && SfVars.HasUnresolved(arg)) return Fail($"missing value for \"{intent}\"");
            _loop.RecordAction(LoopDetector.ActionHash(step.Method, element, arg));
            switch (step.Method)
            {
                case "click":
                    await _page.ClickAsync(element.Value, ct);
                    break;
                case "fill":
                    await _page.FillAsync(element.Value, arg ?? "", ct);
                    break;
                case "press":
                    await _page.ClickAsync(element.Value, ct);
                    await _page.PressAsync(arg ?? "Enter", ct);
                    break;
                case "select":
                    await _page.ClickAsync(element.Value, ct);
                    await _page.SettleAsync(ct, 4000);
                    var (option, _) = await ResolveAsync(new[] { new SfLocator { Role = "option", Name = arg } }, ct);
                    if (option is null) return Fail($"no option \"{arg}\"");
                    await _page.ClickAsync(option.Value, ct);
                    break;
            }
            await _page.SettleAsync(ct);
            _loop.RecordPage(await FingerprintAsync(ct));

            // ---- verify the step ------------------------------------------------------
            if (step.Method == "fill" && arg is { Length: > 0 } && !step.Args.Contains("%date%")) // dates get reformatted by the input
            {
                var v = await SafeValueAsync(element.Value, ct);
                if (v is not null && !Norm(v).Contains(Norm(arg.Length > 40 ? arg[..40] : arg)))
                    return Fail($"\"{intent}\": the field did not take the text");
            }
            if (step.Post is { } post && !await ConditionAsync(post, vars, ct))
                return Fail($"\"{intent}\" did not reach its expected result");

            // Postcondition passed: now (and only now) learn.
            step.Hits++;
            step.LastGood = DateTime.Now.ToString("yyyy-MM-dd");
            if (healed && LocatorRanking.Derive(facts) is { } derived && !SfVars.HasUnresolved(derived.Name ?? ""))
            {
                // Don't bake call content into a locator (a healed "Subject" field is fine; its value isn't).
                if (!vars.Values.Any(v => v.Length > 3 && (derived.Name ?? derived.AriaLabel ?? "").Contains(v, StringComparison.Ordinal)))
                {
                    step.Locators = LocatorRanking.Promote(step.Locators, derived);
                    step.Heals++;
                    skill.Version++;
                    Log.Write($"Salesforce: healed {skill.Skill}#{i} ({intent}) → {derived}");
                }
            }
            learned = true;
            reports.Add(new StepReport(i, intent, healed ? StepOutcome.Healed : StepOutcome.Replayed, via));
        }
        if (learned) SkillStore.Save(skill);
        return new SkillRun(skill.Skill, true, reports);

        SkillRun Fail(string why)
        {
            Log.Write($"Salesforce: {skill.Skill} failed: {why}");
            if (learned) SkillStore.Save(skill); // keep hit counts even on failure
            return new SkillRun(skill.Skill, false, reports, why);
        }
    }

    static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    async Task<string?> SafeValueAsync(int id, CancellationToken ct)
    {
        try
        {
            return await _page.ValueAsync(id, ct);
        }
        catch (CdpException)
        {
            return null; // re-rendered after input: the post-condition decides
        }
    }

    async Task<string> FingerprintAsync(CancellationToken ct)
    {
        try
        {
            var v = await _page.EvalAsync("location.href + '|' + document.title + '|' + document.querySelectorAll('*').length", ct);
            return v?.GetString() ?? "";
        }
        catch (CdpException)
        {
            return "";
        }
    }

    /// <summary>First locator with exactly one visible, enabled match wins.</summary>
    public async Task<(int? Id, string? Via)> ResolveAsync(IReadOnlyList<SfLocator> locators, CancellationToken ct, int waitMs = 4000)
    {
        await _page.EvalAsync(CdpPage.DeepQueryJs + "0", ct);
        var until = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (true)
        {
            List<AxNode>? ax = null;
            foreach (var l in locators)
            {
                List<int> hits;
                if (l.Role is not null)
                {
                    ax ??= await _page.AxTreeAsync(ct);
                    var scope = AxTree.Scope(ax);
                    var matches = AxTree.Match(scope, l).Where(n => !n.Disabled).ToList();
                    hits = new List<int>();
                    foreach (var m in matches)
                        if (await _page.IsVisibleAsync(m.BackendId!.Value, ct)) hits.Add(m.BackendId.Value);
                }
                else
                    hits = await _page.QueryAttrAsync(l, ct);
                if (hits.Count == 1) return (hits[0], l.ToString());
            }
            if (DateTime.UtcNow >= until) return (null, null);
            await Task.Delay(250, ct);
        }
    }

    async Task<int?> HealAsync(string intent, string method, CancellationToken ct)
    {
        var scope = AxTree.Scope(await _page.AxTreeAsync(ct));
        var snapshot = AxTree.Snapshot(scope);
        if (snapshot.Length == 0) return null;
        _progress?.Invoke("מחפש את הכפתור מחדש…");
        var reply = await _ask(RecoveryPrompt.System, RecoveryPrompt.User(intent, method, snapshot, _loop.Nudge()), ct);
        var id = RecoveryPrompt.Parse(reply, snapshot);
        Log.Write($"Salesforce: recovery for \"{intent}\" → {(id is null ? "null" : "e" + id)}");
        return id;
    }

    async Task<bool> ConditionAsync(SfCondition c, IReadOnlyDictionary<string, string> vars, CancellationToken ct, bool quick = false)
    {
        var until = DateTime.UtcNow.AddMilliseconds(quick ? Math.Min(c.TimeoutMs, 3000) : c.TimeoutMs);
        while (true)
        {
            var ok = true;
            if (c.UrlRegex is { } rx && !Regex.IsMatch(await _page.UrlAsync(ct), rx)) ok = false;
            if (ok && c.Element is { } el)
                ok = (await ResolveAsync(new[] { SfVars.Apply(el, vars) }, ct, waitMs: 0)).Id is not null;
            if (ok && c.TextContains is { } t)
            {
                var text = SfVars.Apply(t, vars);
                ok = AxTree.AnyNameContains(await _page.AxTreeAsync(ct), text.Length > 60 ? text[..60] : text);
            }
            if (ok && c.SuccessToast) ok = await _page.SuccessToastAsync(ct);
            if (ok) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(300, ct);
        }
    }
}
