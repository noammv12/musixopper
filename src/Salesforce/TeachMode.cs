using System.Text.Json;

namespace Palon.Salesforce;

/// <summary>
/// Record-once teaching: while it runs, a capture-phase listener in Palon's
/// Edge reports every click and field change; Palon resolves the element
/// through CDP (backend node → partial AX tree for role+name, plus the
/// field api-name container) and on Stop folds it into the skill.
/// </summary>
sealed class TeachSession : IAsyncDisposable
{
    const string Binding = "__palonTeach";

    const string Listener = """
        (() => {
          if (window.__palonTeachOn) return; window.__palonTeachOn = true;
          window.__palonTeachEls = [];
          const target = e => (e.composedPath && e.composedPath()[0]) || e.target;
          const report = (kind, el) => {
            try {
              const i = window.__palonTeachEls.push(el) - 1;
              const v = ('value' in el) ? String(el.value ?? '') : null;
              window.__palonTeach(JSON.stringify({ i, kind, value: v,
                aria: el.getAttribute ? el.getAttribute('aria-label') : null,
                text: (el.innerText || '').trim().slice(0, 80), url: location.href }));
            } catch (x) {}
          };
          document.addEventListener('click', e => {
            let el = target(e);
            const c = el.closest ? el.closest('button, a, [role="button"], [role="tab"], [role="menuitem"], [role="option"], input, textarea') : null;
            report('click', c || el);
          }, true);
          document.addEventListener('change', e => report('fill', target(e)), true);
        })()
        """;

    readonly CdpPage _page;
    readonly List<TeachObservation> _seen = new();
    readonly List<Task> _inflight = new();
    public event Action<TeachObservation>? Observed;

    TeachSession(CdpPage page) => _page = page;

    public static async Task<TeachSession> StartAsync(CancellationToken ct)
    {
        var page = await EdgeBrowser.SalesforcePageAsync(SfOrg.Saved, ct);
        var s = new TeachSession(page);
        page.Session.Event += s.OnEvent;
        await page.Session.SendAsync("Runtime.addBinding", new { name = Binding }, ct);
        await page.Session.SendAsync("Page.addScriptToEvaluateOnNewDocument", new { source = Listener }, ct);
        await page.EvalAsync(Listener, ct);
        await page.BringToFrontAsync(ct);
        return s;
    }

    void OnEvent(string method, JsonElement p)
    {
        if (method != "Runtime.bindingCalled" || p.GetProperty("name").GetString() != Binding) return;
        var payload = p.GetProperty("payload").GetString() ?? "{}";
        lock (_inflight) _inflight.Add(ResolveAsync(payload));
    }

    async Task ResolveAsync(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            var i = r.GetProperty("i").GetInt32();
            var kind = r.GetProperty("kind").GetString() ?? "click";
            string? S(string n) => r.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            ElementFacts facts;
            try
            {
                var obj = await _page.Session.SendAsync("Runtime.evaluate", new { expression = $"window.__palonTeachEls[{i}]" });
                var bid = await _page.BackendIdAsync(obj.GetProperty("result").GetProperty("objectId").GetString()!, CancellationToken.None);
                facts = bid is { } b ? await _page.FactsAsync(b, CancellationToken.None) : new ElementFacts(null, S("text"), S("aria"), null);
            }
            catch (Exception ex) when (ex is CdpException or KeyNotFoundException or InvalidOperationException)
            {
                facts = new ElementFacts(null, S("text"), S("aria"), null); // the page navigated away first
            }
            var o = new TeachObservation(kind, facts, S("value"), S("url") ?? "");
            lock (_seen) _seen.Add(o);
            Observed?.Invoke(o);
        }
        catch (Exception ex) when (ex is JsonException or CdpException)
        {
            Log.Write($"Salesforce teach: dropped an event: {ex.Message}");
        }
    }

    /// <summary>Stops listening and folds the observations into <paramref name="skillName"/> (saved).</summary>
    public async Task<TeachOutcome> FinishAsync(string skillName)
    {
        Task[] pending;
        lock (_inflight) pending = _inflight.ToArray();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        List<TeachObservation> seen;
        lock (_seen) seen = _seen.ToList();
        var outcome = TeachMerge.Merge(SkillStore.Load(skillName), seen, DateOnly.FromDateTime(DateTime.Now));
        if (outcome.Learned.Count > 0) SkillStore.Save(outcome.Skill);
        Log.Write($"Salesforce teach {skillName}: learned {outcome.Learned.Count}, unmatched {outcome.Unmatched.Count}");
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        _page.Session.Event -= OnEvent;
        try
        {
            await _page.EvalAsync("window.__palonTeach = () => {}", CancellationToken.None);
        }
        catch (CdpException)
        {
        }
        await _page.DisposeAsync();
    }
}
