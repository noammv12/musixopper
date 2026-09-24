namespace Palon.Sales;

sealed record Breakdown(string Key, int Count, decimal Sum, int FtdIls);

sealed record MonthStats(
    int Count,
    int? Target,
    double? PercentOfTarget,
    int? Remaining,
    int WorkDays,
    int ElapsedWorkDays,
    int RemainingWorkDays,
    double PacePerDay,
    double? RequiredPerDay,
    double Projection,
    decimal SumDeposits,
    decimal AvgDeposit,
    double DepositsPerWorkDay,
    DateTime? BestDay,
    int BestDayCount,
    IReadOnlyList<Breakdown> BySource,
    IReadOnlyList<Breakdown> ByTier,
    IReadOnlyList<Breakdown> ByRegion,
    IReadOnlyList<Breakdown> ByAffiliate,
    int FtdIls,
    int TargetBonusIls,
    int ExpectedPayIls,
    int ConfirmedPayIls,
    int ApprovedCount);

/// <summary>Pure month statistics and pay math.</summary>
static class SalesStats
{
    /// <summary>Deals in deposit order (by date, then entry order — OrderBy is stable).</summary>
    public static List<Deal> Ordered(IEnumerable<Deal> deals) => deals.OrderBy(d => d.Date.Date).ToList();

    /// <summary>
    /// +bonus for every deposit from the target-th onward (the 55th of 55
    /// counts). No target = no bonus.
    /// </summary>
    public static int TargetBonus(int count, int? target, BonusRules rules) =>
        target is int t && t > 0 && count >= t ? (count - t + 1) * rules.TargetBonusPerDealIls : 0;

    public static int SumFtd(IEnumerable<Deal> deals, BonusRules rules) => deals.Sum(rules.FtdBonus);

    public static int Pay(IReadOnlyCollection<Deal> deals, int? target, BonusRules rules) =>
        rules.BaseSalaryIls + SumFtd(deals, rules) + TargetBonus(deals.Count, target, rules);

    /// <summary>Work days elapsed (inclusive of <paramref name="asOf"/>), capped by an override count.</summary>
    public static int ElapsedWorkDays(MonthBook book, DateTime asOf) =>
        Math.Min(book.WorkDays, WorkCalendar.WorkDaysElapsed(book.Year, book.Month, asOf));

    public static MonthStats Compute(MonthBook book, BonusRules rules, DateTime asOf)
    {
        var deals = Ordered(book.Deals);
        int count = deals.Count;
        int workDays = book.WorkDays;
        int elapsed = ElapsedWorkDays(book, asOf);
        int remainingDays = Math.Max(0, workDays - elapsed);
        double pace = elapsed > 0 ? (double)count / elapsed : 0;
        int? target = book.Target;
        int? remaining = target is int t ? Math.Max(0, t - count) : null;
        double? required = remaining is int r
            ? (r == 0 ? 0 : remainingDays > 0 ? (double)r / remainingDays : null)
            : null;
        decimal sum = deals.Sum(d => d.Amount);

        var best = deals.GroupBy(d => d.Date.Date)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .FirstOrDefault();

        List<Breakdown> By(Func<Deal, string> key) => deals
            .GroupBy(key)
            .Select(g => new Breakdown(g.Key, g.Count(), g.Sum(d => d.Amount), SumFtd(g, rules)))
            .OrderByDescending(b => b.Count).ThenByDescending(b => b.Sum)
            .ToList();

        var approved = deals.Where(d => d.Approved).ToList();
        int ftd = SumFtd(deals, rules);
        int targetBonus = TargetBonus(count, target, rules);

        return new MonthStats(
            Count: count,
            Target: target,
            PercentOfTarget: target is int tt && tt > 0 ? 100.0 * count / tt : null,
            Remaining: remaining,
            WorkDays: workDays,
            ElapsedWorkDays: elapsed,
            RemainingWorkDays: remainingDays,
            PacePerDay: pace,
            RequiredPerDay: required,
            Projection: count + pace * remainingDays,
            SumDeposits: sum,
            AvgDeposit: count > 0 ? sum / count : 0,
            DepositsPerWorkDay: workDays > 0 && elapsed > 0 ? pace : 0,
            BestDay: best?.Key,
            BestDayCount: best?.Count() ?? 0,
            BySource: By(d => d.Source.ToString()),
            ByTier: By(d => d.TierLabel),
            ByRegion: By(d => d.Region.ToString()),
            ByAffiliate: By(d => string.IsNullOrWhiteSpace(d.Affiliate) ? "" : d.Affiliate!.Trim()),
            FtdIls: ftd,
            TargetBonusIls: targetBonus,
            ExpectedPayIls: rules.BaseSalaryIls + ftd + targetBonus,
            ConfirmedPayIls: Pay(approved, target, rules),
            ApprovedCount: approved.Count);
    }

    /// <summary>
    /// Monthly nudge for later UI wiring: true on/after the 1st of the current
    /// month while that month has no target yet (a missing book counts as no target).
    /// </summary>
    public static bool ShouldRemindToSetTarget(MonthBook? currentMonth, DateTime now) =>
        currentMonth is null
            ? true
            : currentMonth.Year == now.Year && currentMonth.Month == now.Month && currentMonth.Target is null;
}
