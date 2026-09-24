using Palon.Agent;
using Palon.Notes;
using Palon.Sales;

namespace Palon.Terminal;

enum ClientTone { Active, Deposited, Due, Overdue }

enum TimelineKind { Call, Callback, Deal }

sealed record TimelineEntry(DateTime WhenLocal, TimelineKind Kind, string Label, string Text, bool Muted = false);

sealed class ClientCard
{
    public string Key { get; init; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public DateTime LastLocal { get; set; }
    public List<CallNote> Calls { get; } = new();
    public List<Callback> Callbacks { get; } = new();
    public List<Deal> Deals { get; } = new();

    public ClientTone Tone(DateTime nowLocal)
    {
        if (Callbacks.Any(c => c.IsActive && c.DueAtUtc.ToLocalTime() < nowLocal)) return ClientTone.Overdue;
        if (Deals.Count > 0) return ClientTone.Deposited;
        if (Callbacks.Any(c => c.IsActive && c.DueAtUtc.ToLocalTime().Date <= nowLocal.Date.AddDays(1))) return ClientTone.Due;
        return ClientTone.Active;
    }

    public Callback? NextCallback => Callbacks.Where(c => c.IsActive).OrderBy(c => c.DueAtUtc).FirstOrDefault();

    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || Name.Any(char.IsDigit)) return "#";
            return string.Concat(words.Take(2).Select(w => w[0]));
        }
    }

    /// <summary>"פרו · Silver · $10,000 · DVIR SHALOM" when there's a deal.</summary>
    public string Meta
    {
        get
        {
            var d = Deals.OrderByDescending(x => x.Date).FirstOrDefault();
            if (d is null) return Phone is not null && Name != Phone ? Phone : "";
            var parts = new List<string> { SalesLabels.Region(d.Region), d.TierLabel, "$" + He.N(d.Amount) };
            if (!string.IsNullOrWhiteSpace(d.Affiliate)) parts.Add(d.Affiliate!.Trim());
            return string.Join(" · ", parts);
        }
    }

    public List<string> Stats(BonusRules rules)
    {
        var list = new List<string> { Calls.Count == 1 ? "שיחה אחת" : $"{Calls.Count} שיחות" };
        var minutes = Calls.Sum(c => c.DurationSec) / 60;
        if (Calls.Count > 0) list.Add(minutes < 1 ? "פחות מדקה" : $"{minutes} דק׳ בשיחות");
        list.Add(Deals.Count == 0 ? "עוד לא הפקיד" : $"בונוס ₪{He.N(Deals.Sum(rules.FtdBonus))}");
        return list;
    }

    /// <summary>Newest first: calls (with their summary), callbacks, deposits.</summary>
    public List<TimelineEntry> Timeline(BonusRules rules)
    {
        var list = new List<TimelineEntry>();
        foreach (var n in Calls)
            list.Add(new TimelineEntry(n.StartedUtc.ToLocalTime(), TimelineKind.Call, $"שיחה · {He.Duration(n.DurationSec)}", CallSummary.Line(n)));
        foreach (var c in Callbacks)
        {
            var label = c.Status switch
            {
                CallbackStatus.Done => "חזרה · בוצעה",
                CallbackStatus.Cancelled => "חזרה · בוטלה",
                _ => "חזרה",
            };
            list.Add(new TimelineEntry(c.DueAtUtc.ToLocalTime(), TimelineKind.Callback, label,
                c.Note.Length > 0 ? c.Note : c.DisplayLabel, Muted: !c.IsActive));
        }
        foreach (var d in Deals)
        {
            var bonus = rules.FtdBonus(d);
            list.Add(new TimelineEntry(d.Date.Date, TimelineKind.Deal, "עסקה",
                $"הפקדה ${He.N(d.Amount)} · {d.TierLabel}" + (bonus > 0 ? $" · בונוס ₪{He.N(bonus)}" : "")));
        }
        return list.OrderByDescending(e => e.WhenLocal).ToList();
    }
}

/// <summary>
/// Everyone the user dealt with, stitched from the stores already on disk:
/// callbacks carry name↔phone, call notes carry the phone, deals carry the
/// name. People are merged by phone (PhoneMatch) and else by exact name
/// (case- and space-insensitive). No database of its own. Pure.
/// </summary>
static class ClientIndex
{
    public static List<ClientCard> Build(
        IEnumerable<CallNote> notes, IEnumerable<Callback> callbacks, IEnumerable<Deal> deals)
    {
        var cards = new List<ClientCard>();

        ClientCard? ByPhone(string? phone) =>
            string.IsNullOrEmpty(phone) ? null : cards.FirstOrDefault(c => c.Phone is not null && PhoneMatch.Same(c.Phone, phone));
        ClientCard? ByName(string? name)
        {
            var key = NameKey(name);
            return key.Length == 0 ? null : cards.FirstOrDefault(c => NameKey(c.Name) == key);
        }
        ClientCard Create(string name, string? phone)
        {
            var card = new ClientCard { Key = phone is not null ? "p:" + PhoneMatch.Digits(phone) : "n:" + NameKey(name), Name = name, Phone = phone };
            cards.Add(card);
            return card;
        }

        // Callbacks first: they're the only place a name meets a phone.
        foreach (var cb in callbacks)
        {
            var name = string.IsNullOrWhiteSpace(cb.Name) ? null : cb.Name!.Trim();
            if (name is null && !cb.HasPhone) continue; // a bare note isn't a person
            var card = ByPhone(cb.Phone) ?? ByName(name) ?? Create(name ?? cb.Phone!, cb.Phone);
            if (card.Phone is null && cb.HasPhone) card.Phone = cb.Phone;
            if (name is not null && card.Name == card.Phone) card.Name = name;
            card.Callbacks.Add(cb);
        }
        foreach (var note in notes)
        {
            if (string.IsNullOrEmpty(note.Number)) continue;
            var card = ByPhone(note.Number) ?? Create(note.Number!, note.Number);
            card.Calls.Add(note);
        }
        foreach (var deal in deals)
        {
            if (string.IsNullOrWhiteSpace(deal.ClientName)) continue;
            var card = ByName(deal.ClientName) ?? Create(deal.ClientName.Trim(), null);
            card.Deals.Add(deal);
        }

        foreach (var card in cards)
        {
            var times = card.Calls.Select(n => n.StartedUtc.ToLocalTime())
                .Concat(card.Callbacks.Select(c => (c.CompletedUtc ?? c.CreatedUtc).ToLocalTime()))
                .Concat(card.Deals.Select(d => d.Date));
            card.LastLocal = times.DefaultIfEmpty(DateTime.MinValue).Max();
        }
        return cards.OrderByDescending(c => c.LastLocal).ToList();
    }

    /// <summary>Matches name or phone digits; empty query = everyone.</summary>
    public static List<ClientCard> Search(IEnumerable<ClientCard> cards, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return cards.ToList();
        var digits = PhoneMatch.Digits(q);
        return cards.Where(c =>
            c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (digits.Length >= 3 && c.Phone is not null && PhoneMatch.Digits(c.Phone).Contains(digits))).ToList();
    }

    /// <summary>The name to show for a call note: whoever has that phone, else null.</summary>
    public static string? NameForPhone(IEnumerable<Callback> callbacks, string? phone) =>
        string.IsNullOrEmpty(phone) ? null
        : callbacks.Where(c => !string.IsNullOrWhiteSpace(c.Name) && PhoneMatch.Same(c.Phone, phone))
                   .Select(c => c.Name!.Trim()).FirstOrDefault();

    static string NameKey(string? name) =>
        string.Join(' ', (name ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
