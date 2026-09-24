namespace Palon.Sales;

enum DealRegion { Pro, Israel }

enum DealSource { Affiliate, PPC, Organic, Referral }

enum DealOrigin { Manual, Call, Import }

enum DealTier { Margin, Bronze, Silver, Gold, VIP }

/// <summary>One first-time deposit (FTD) closed by the user.</summary>
sealed class Deal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientName { get; set; } = "";
    /// <summary>Calendar date of the deposit (time part ignored).</summary>
    public DateTime Date { get; set; }
    public DealRegion Region { get; set; } = DealRegion.Pro;
    public DealSource Source { get; set; } = DealSource.Affiliate;
    /// <summary>Deposit amount in USD.</summary>
    public decimal Amount { get; set; }
    public string? Affiliate { get; set; }
    public string? Note { get; set; }
    /// <summary>Confirmed by the company at month end.</summary>
    public bool Approved { get; set; }
    public DealOrigin CreatedFrom { get; set; } = DealOrigin.Manual;
    /// <summary>
    /// Stored tier label that wins over the amount-derived tier (the Israel
    /// desk's CLUB labels don't follow the Pro thresholds — unresolved).
    /// </summary>
    public string? TierOverride { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public DealTier Tier => Tiers.ForAmount(Amount);
    [System.Text.Json.Serialization.JsonIgnore] public string TierLabel => string.IsNullOrWhiteSpace(TierOverride) ? Tier.ToString() : TierOverride!;
}

/// <summary>A month's sales book: target, work days and the deals list.</summary>
sealed class MonthBook
{
    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>Deposit-count target set by the company; null until the user enters it.</summary>
    public int? Target { get; set; }
    /// <summary>User override for the month's work-day count; null = Israeli calendar.</summary>
    public int? WorkDaysOverride { get; set; }
    public List<Deal> Deals { get; set; } = new();
    public bool Archived { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string Key => $"{Year:D4}-{Month:D2}";
    [System.Text.Json.Serialization.JsonIgnore] public int WorkDays => WorkDaysOverride ?? WorkCalendar.WorkDaysInMonth(Year, Month);
}

static class Tiers
{
    public static DealTier ForAmount(decimal amount) => amount switch
    {
        >= 100_000 => DealTier.VIP,
        >= 25_000 => DealTier.Gold,
        >= 10_000 => DealTier.Silver,
        >= 3_000 => DealTier.Bronze,
        _ => DealTier.Margin,
    };
}

/// <summary>One FTD bonus row: applies to deals of Region and one of Sources at or above MinAmount.</summary>
sealed class FtdRule
{
    public DealRegion Region { get; set; }
    public decimal MinAmount { get; set; }
    public List<DealSource> Sources { get; set; } = new();
    public int BonusIls { get; set; }
}

/// <summary>
/// Pay rules, data-driven so they can be edited later. For a deal, the
/// matching rule (region + source) with the highest MinAmount not above
/// the deal amount wins; no match = 0.
/// </summary>
sealed class BonusRules
{
    public int BaseSalaryIls { get; set; } = 10_000;
    public int TargetBonusPerDealIls { get; set; } = 100;
    public List<FtdRule> Ftd { get; set; } = new();

    public static BonusRules Default()
    {
        var high = new List<DealSource> { DealSource.PPC, DealSource.Referral };
        var low = new List<DealSource> { DealSource.Affiliate, DealSource.Organic };
        FtdRule R(DealRegion r, decimal min, List<DealSource> s, int b) =>
            new() { Region = r, MinAmount = min, Sources = new(s), BonusIls = b };
        return new BonusRules
        {
            Ftd = new()
            {
                R(DealRegion.Israel, 0, high, 600),
                R(DealRegion.Israel, 0, low, 200),
                R(DealRegion.Pro, 3_000, low, 200),
                R(DealRegion.Pro, 3_000, high, 600),
                R(DealRegion.Pro, 10_000, low, 400),
                R(DealRegion.Pro, 10_000, high, 800),
            },
        };
    }

    public int FtdBonus(Deal deal) =>
        Ftd.Where(r => r.Region == deal.Region && r.Sources.Contains(deal.Source) && deal.Amount >= r.MinAmount)
           .OrderByDescending(r => r.MinAmount)
           .Select(r => r.BonusIls)
           .FirstOrDefault();
}

static class SalesLabels
{
    public static string Region(DealRegion r) => r == DealRegion.Israel ? "ישראל" : "פרו";

    public static string Source(DealSource s) => s == DealSource.Referral ? "חבר מביא חבר" : s.ToString();

    public static DealRegion? ParseRegion(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t is "ישראל" or "israel" or "il") return DealRegion.Israel;
        if (t is "פרו" or "pro") return DealRegion.Pro;
        return null;
    }

    public static DealSource? ParseSource(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "affiliate" or "אפיליאייט" => DealSource.Affiliate,
            "ppc" => DealSource.PPC,
            "organic" or "אורגני" => DealSource.Organic,
            "referral" or "rff" or "raf" or "חבר מביא חבר" => DealSource.Referral,
            _ => null,
        };
    }
}
