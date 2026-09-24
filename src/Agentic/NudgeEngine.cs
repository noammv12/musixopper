using System.Security.Cryptography;
using System.Text;
using Palon.Notes;
using Palon.Salesforce;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.Agentic;

/// <summary>
/// The proactive loop. Every 30 s (and a few seconds after a deal or a call note
/// is saved) it loads a snapshot, runs <see cref="NudgeRules"/>, lets
/// <see cref="NudgeGovernor"/> pick what may be shown, and raises it on
/// <see cref="NudgeHub"/>. It also retires nudges that went stale and, when the
/// user is idle, pre-drafts a follow-up for the warmest quiet lead (one AI call,
/// cached, never during a call). All work is off the UI thread.
/// </summary>
static class NudgeEngine
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public const int MaxDraftsPerDay = 3;

    static readonly object Gate = new();
    static Timer? _timer;
    static int _ticking;
    static bool _wasOnCall;
    static DateTime? _lastCallEndedLocal;
    static DateTime? _lastAmbientLocal;
    static DateTime _countDay;
    static int _ambientToday;
    static int _draftsToday;
    static readonly Dictionary<string, (DateTime? Expires, string? CallbackId)> Live = new();

    public static bool Running => _timer is not null;

    /// <summary>Starts the loop. Idempotent.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), Interval);
        }
        SalesStore.Changed += Soon;
        NotesStore.Changed += Soon;
        CallbackStore.Changed += Soon;
        NudgeHub.Dismissed += OnDismissed;
        Log.Write("Agentic: nudge engine started");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        SalesStore.Changed -= Soon;
        NotesStore.Changed -= Soon;
        CallbackStore.Changed -= Soon;
        NudgeHub.Dismissed -= OnDismissed;
    }

    /// <summary>Tell the engine the call state changed (Shell forwards CallEngine.StateChanged).
    /// Going on a call hides ambient nudges at once.</summary>
    public static void OnCallStateChanged(bool onCall)
    {
        if (onCall && !_wasOnCall)
        {
            foreach (var n in NudgeHub.Active.Where(n => n.Kind != NudgeKind.Reminder)) NudgeHub.Dismiss(n.Id);
        }
        if (!onCall && _wasOnCall) _lastCallEndedLocal = DateTime.Now;
        _wasOnCall = onCall;
        if (!onCall) Soon();
    }

    static void Soon()
    {
        lock (Gate) _timer?.Change(TimeSpan.FromSeconds(4), Interval);
    }

    static void OnDismissed(string id)
    {
        bool mine;
        lock (Gate) mine = Live.Remove(id);
        if (mine) AgenticState.Mutate(s => s.Dismissed[id] = DateTime.UtcNow);
    }

    /// <summary>One pass. Public so the UI can force a refresh (e.g. after focus ends).</summary>
    public static async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var onCall = SafeOnCall();
            if (onCall != _wasOnCall) OnCallStateChanged(onCall);
            var now = DateTime.Now;
            if (now.Date != _countDay) { _countDay = now.Date; _ambientToday = 0; _draftsToday = 0; }

            var snapshot = WorkSnapshot.Load(now);
            Retire(snapshot);
            if (onCall) return;

            var state = AgenticState.Load();
            var extras = Extras(snapshot, state);
            var candidates = NudgeRules.Evaluate(snapshot, extras);
            var picked = NudgeGovernor.Select(candidates, new GovernorInput(
                now, onCall, _lastCallEndedLocal, Focus.IsOn(DateTime.UtcNow), state.NudgesEnabled,
                state.Shown, state.Dismissed, _ambientToday, _lastAmbientLocal, snapshot.TimeRules));

            foreach (var c in picked) Raise(c);
            if (picked.Count > 0)
                AgenticState.Mutate(s => { foreach (var c in picked) s.Shown[c.Key] = DateTime.UtcNow; });

            await PrefetchDraftAsync(snapshot, state);
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: nudge tick failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    static bool SafeOnCall()
    {
        try { return AgenticHost.IsOnCall(); }
        catch { return false; }
    }

    static NudgeExtras Extras(WorkSnapshot s, AgenticState state)
    {
        int proposals = 0;
        var key = "";
        try
        {
            var list = TemplateLearning.Proposals(s.Templates, TemplateLearningStore.Sends(), TemplateLearningStore.Decided());
            proposals = list.Count;
            key = Hash(string.Join("|", list.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal)));
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: template proposals failed: {ex.Message}");
        }
        RehearsalStatus? rehearsal = null;
        try { rehearsal = RehearsalSettings.Load().LastStatus; }
        catch { }
        var tips = s.Now.Hour is >= 10 and < 13 ? NudgeRules.CoachTipsNow(s.Now) : Array.Empty<string>();
        var drafts = state.Drafts.Where(d => DateTime.UtcNow - d.CreatedUtc < FollowUpDrafts.MaxAge).Select(d => d.ClientKey).ToHashSet();
        return new NudgeExtras(proposals, key, rehearsal, tips, drafts);
    }

    static void Raise(NudgeCandidate c)
    {
        var callbackId = c.Action?.Kind == NudgeActionKind.SnoozeCallback ? c.Action.Arg
            : c.Key.StartsWith("promise:", StringComparison.Ordinal) ? c.Key.Split(':')[1] : null;
        lock (Gate) Live[c.Key] = (c.ExpiresLocal, callbackId);
        if (c.Kind != NudgeKind.Celebration && !c.UserReminder)
        {
            _ambientToday++;
            _lastAmbientLocal = DateTime.Now;
        }
        var act = c.Action is null ? null : ActFor(c);
        NudgeHub.Raise(new Nudge(c.Key, c.Kind, c.Text, c.Action?.Label, act, DateTime.Now));
        Log.Write($"Agentic: nudge {c.Key}");
    }

    /// <summary>Drops nudges past their expiry and promise nudges whose callback was handled.</summary>
    static void Retire(WorkSnapshot s)
    {
        List<string> stale;
        lock (Gate)
        {
            stale = Live.Where(p =>
                    (p.Value.Expires is { } e && e < s.Now)
                    || (p.Value.CallbackId is { } id && !s.Callbacks.Any(c => c.Id == id && c.IsActive)))
                .Select(p => p.Key).ToList();
            foreach (var k in stale) Live.Remove(k);
        }
        foreach (var k in stale) NudgeHub.Dismiss(k);
    }

    static Func<Task> ActFor(NudgeCandidate c) => async () =>
    {
        try
        {
            await RunActionAsync(c.Action!);
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: nudge action failed: {ex.Message}");
        }
        NudgeHub.Dismiss(c.Key);
    };

    internal static async Task RunActionAsync(NudgeAction a)
    {
        switch (a.Kind)
        {
            case NudgeActionKind.OpenPage:
                AgenticHost.OpenPage?.Invoke(a.Arg ?? "today", a.Arg2);
                break;
            case NudgeActionKind.ShowText:
                AgenticHost.ShowText?.Invoke(a.Arg ?? "Palon", a.Arg2 ?? "");
                break;
            case NudgeActionKind.NextSteps:
            {
                var s = WorkSnapshot.Load(DateTime.Now);
                var plan = NextSteps.Plan(s);
                AgenticHost.ShowText?.Invoke("מה עכשיו", plan.Count == 0 ? "אין כרגע משהו פתוח." : string.Join("\n", plan.Select(p => "• " + p.ToLine(s.Now))));
                break;
            }
            case NudgeActionKind.ClientSummary:
            {
                var s = WorkSnapshot.Load(DateTime.Now);
                if (s.FindClient(a.Arg) is { } card) AgenticHost.ShowText?.Invoke(card.Name, ClientSummaries.Build(s, card).ToText());
                break;
            }
            case NudgeActionKind.CopyTemplate:
            {
                var template = TemplatesStore.Load().FirstOrDefault(t => t.Id == a.Arg);
                if (template is null) break;
                var s = WorkSnapshot.Load(DateTime.Now);
                var card = a.Arg2 is null ? null : s.FindClient(a.Arg2);
                var filled = TemplateFill.Fill(template, TemplateFill.FirstName(card?.Name ?? a.Arg2));
                if (AgenticHost.CopyText(filled)) TemplateLearningStore.LogCopy(template, card?.Name ?? a.Arg2, card?.Phone, filled, null, "nudge");
                break;
            }
            case NudgeActionKind.DraftFollowUp:
            {
                var s = WorkSnapshot.Load(DateTime.Now);
                if (s.FindClient(a.Arg) is not { } card) break;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var (text, error) = await FollowUpDrafts.GetOrCreateAsync(s, card, null, cts.Token);
                if (text is not null) AgenticHost.CopyText(text);
                AgenticHost.ShowText?.Invoke($"הודעה ל{card.Name}", text ?? error ?? "לא הצלחתי לנסח.");
                break;
            }
            case NudgeActionKind.SnoozeCallback:
            {
                if (a.Arg is not { } id) break;
                var before = CallbackStore.Load().FirstOrDefault(c => c.Id == id);
                if (before is null) break;
                CallbackStore.Mutate(id, c => CallbackPlanner.Snooze(c, SnoozeKind.OneHour, DateTime.Now));
                CommandRouter.OfferUndo("החזרה נדחתה בשעה", () =>
                {
                    CallbackStore.Update(before);
                    return Task.FromResult("הדחייה בוטלה");
                });
                break;
            }
        }
    }

    /// <summary>When idle, drafts one follow-up for the warmest quiet lead so the nudge can say "I drafted it".</summary>
    static async Task PrefetchDraftAsync(WorkSnapshot s, AgenticState state)
    {
        if (_draftsToday >= MaxDraftsPerDay || !AiChat.HasKey || SafeOnCall() || Focus.IsOn(DateTime.UtcNow) || !state.NudgesEnabled) return;
        if (!CallbackPlanner.IsWorkDay(s.Now.DayOfWeek) || s.Now.Hour < 9 || s.Now.Hour >= 18) return;
        if (_lastCallEndedLocal is { } ended && s.Now - ended < TimeSpan.FromMinutes(2)) return;
        var lead = Leads.Silent(s).Take(NudgeRules.MaxRevivesPerTick)
            .FirstOrDefault(l => FollowUpDrafts.Cached(l.Client.Key, l.LastNote.Id) is null
                                 && !state.Shown.ContainsKey($"revive:{l.Client.Key}:{l.LastNote.Id}"));
        if (lead is null) return;
        _draftsToday++;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (text, _) = await FollowUpDrafts.GetOrCreateAsync(s, lead.Client, null, cts.Token);
        if (text is not null) Log.Write($"Agentic: pre-drafted a follow-up for a quiet lead ({text.Length} chars)");
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
}
