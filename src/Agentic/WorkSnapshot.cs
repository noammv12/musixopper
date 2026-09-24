using Palon.Agent;
using Palon.Memory;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.Agentic;

/// <summary>
/// Everything the agentic layer reasons over, loaded once from the stores on
/// disk (small files — milliseconds). All insight code takes a snapshot, never
/// the stores, so it is pure and testable with hand-built data.
/// </summary>
sealed class WorkSnapshot
{
    public DateTime Now { get; init; }
    public List<Callback> Callbacks { get; init; } = new();
    public List<CallNote> Notes { get; init; } = new();
    public List<CallRecord> Calls { get; init; } = new();
    public MonthBook? Month { get; init; }
    public BonusRules Rules { get; init; } = BonusRules.Default();
    public List<ClientFact> Facts { get; init; } = new();
    public IReadOnlyList<TimeRule> TimeRules { get; init; } = Array.Empty<TimeRule>();
    public List<MessageTemplate> Templates { get; init; } = new();
    public List<ClientCard> Clients { get; private set; } = new();

    MonthStats? _stats;
    public MonthStats? Stats => Month is null ? null : _stats ??= SalesStats.Compute(Month, Rules, Now);

    /// <summary>Loads the live stores. Never throws — a store that fails just reads empty.</summary>
    public static WorkSnapshot Load(DateTime nowLocal)
    {
        T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception ex) { Log.Write($"Agentic: snapshot read failed: {ex.Message}"); return fallback; }
        }
        return Build(new WorkSnapshot
        {
            Now = nowLocal,
            Callbacks = Safe(CallbackStore.Load, new List<Callback>()),
            Notes = Safe(NotesStore.Load, new List<CallNote>()),
            Calls = Safe(CallStatsStore.Load, new List<CallRecord>()),
            Month = Safe(() => SalesStore.Get(nowLocal.Year, nowLocal.Month), null),
            Rules = Safe(SalesStore.LoadRules, BonusRules.Default()),
            Facts = Safe(() => MemoryStore.Facts, new List<ClientFact>()),
            TimeRules = Safe(MemoryStore.Rules, (IReadOnlyList<TimeRule>)Array.Empty<TimeRule>()),
            Templates = Safe(TemplatesStore.Load, new List<MessageTemplate>()),
        });
    }

    /// <summary>Finishes a hand-built snapshot: stitches the client index and names phone-only
    /// clients from remembered facts.</summary>
    public static WorkSnapshot Build(WorkSnapshot s)
    {
        s.Clients = ClientIndex.Build(s.Notes, s.Callbacks, s.Month?.Deals ?? new List<Deal>());
        foreach (var card in s.Clients)
        {
            if (card.Phone is null || card.Name != card.Phone) continue;
            var name = FactBook.ForClient(s.Facts, null, card.Phone).Select(f => f.ClientName).LastOrDefault(n => !string.IsNullOrWhiteSpace(n));
            if (name is not null) card.Name = name.Trim();
        }
        return s;
    }

    /// <summary>Clients with a real name (not just a number), newest contact first.</summary>
    public IEnumerable<ClientCard> NamedClients => Clients.Where(c => c.Name.Length > 0 && !c.Name.Any(char.IsDigit));

    /// <summary>Best client match for a spoken/typed name or a phone; null when unknown.</summary>
    public ClientCard? FindClient(string? nameOrPhone)
    {
        var q = (nameOrPhone ?? "").Trim();
        if (q.Length == 0) return null;
        if (PhoneMatch.Digits(q).Length >= 7)
            return Clients.FirstOrDefault(c => c.Phone is not null && PhoneMatch.Same(c.Phone, q));
        var nq = HebrewText.Normalize(q);
        return Clients.FirstOrDefault(c => HebrewText.Normalize(c.Name) == nq)
               ?? Clients.FirstOrDefault(c => HebrewText.Normalize(c.Name).StartsWith(nq + " ", StringComparison.Ordinal))
               ?? Clients.FirstOrDefault(c => HebrewText.SameName(q, c.Name));
    }

    /// <summary>The latest note for a client (by phone), or null.</summary>
    public CallNote? LatestNote(ClientCard? card) =>
        card?.Calls.OrderByDescending(n => n.StartedUtc).FirstOrDefault();

    public List<ClientFact> FactsFor(ClientCard card) =>
        FactBook.ForClient(Facts, card.Name, card.Phone).Where(f => f.IsActive).ToList();

    public IReadOnlyList<TimeRule> RulesFor(ClientCard card) => Palon.Memory.TimeRules.For(TimeRules, card.Name, card.Phone);

    /// <summary>The client's name for a phone, else the phone.</summary>
    public string Who(string? phone) =>
        phone is null ? "לקוח" : Clients.FirstOrDefault(c => c.Phone is not null && PhoneMatch.Same(c.Phone, phone))?.Name ?? phone;
}
