using Palon.Sales;

namespace Palon.Terminal;

/// <summary>One work day in the month chart. Future days carry the
/// required-per-day as a dashed "needed" bar instead of a count.</summary>
sealed record DayBar(DateTime Date, int Count, int Cumulative, bool IsToday, bool IsFuture, double Needed);

/// <summary>Pure month read-outs the Terminal draws: the per-work-day
/// chart, the "Palon noticed" lines, Palon's one-line brief, and the
/// month-bound answers the Ask overlay can give without a model.</summary>
static class MonthView
{
    public static List<DayBar> Bars(MonthBook book, MonthStats stats, DateTime asOf)
    {
        var byDay = book.Deals.GroupBy(d => d.Date.Date).ToDictionary(g => g.Key, g => g.Count());
        var dates = WorkCalendar.WorkDates(book.Year, book.Month).ToList();
        // Deals logged on a non-work day still count — attach them to the
        // nearest earlier work day so the chart's total matches the ring.
        foreach (var (day, n) in byDay.ToList())
        {
            if (dates.Contains(day)) continue;
            var host = dates.LastOrDefault(d => d < day);
            if (host == default) host = dates.FirstOrDefault();
            if (host == default) continue;
            byDay[host] = byDay.GetValueOrDefault(host) + n;
        }
        var needed = stats.RequiredPerDay ?? 0;
        int cum = 0;
        var bars = new List<DayBar>();
        foreach (var d in dates)
        {
            var future = d > asOf.Date;
            var n = future ? 0 : byDay.GetValueOrDefault(d);
            cum += n;
            bars.Add(new DayBar(d, n, cum, d == asOf.Date, future, future ? needed : 0));
        }
        return bars;
    }

    /// <summary>
    /// Up to three plain observations, computed locally: where deposits
    /// come from (and the top affiliate), which source pays best per
    /// deposit, and the best day against what the target now needs.
    /// </summary>
    public static List<string> Insights(MonthBook book, MonthStats stats, BonusRules rules)
    {
        var lines = new List<string>();
        if (stats.Count == 0) return lines;

        var topSource = stats.BySource.FirstOrDefault();
        if (topSource is not null)
        {
            var pct = (int)Math.Round(100.0 * topSource.Count / stats.Count);
            var line = $"{pct}% מההפקדות שלך מגיעות מ-{SourceLabel(topSource.Key)}.";
            var topAff = stats.ByAffiliate.Where(a => a.Key.Length > 0).FirstOrDefault();
            if (topAff is not null && topAff.Count > 1) line += $" {topAff.Key} לבד הביא {topAff.Count}.";
            lines.Add(line);
        }

        var perSource = stats.BySource.Where(b => b.Count > 0)
            .Select(b => (b.Key, Avg: (double)b.FtdIls / b.Count)).OrderByDescending(x => x.Avg).ToList();
        if (perSource.Count >= 2 && perSource[0].Avg > perSource[^1].Avg)
        {
            var best = perSource[0];
            var main = perSource.FirstOrDefault(x => x.Key == topSource?.Key && x.Key != best.Key);
            var other = main.Key is null ? perSource[^1] : main;
            lines.Add($"{SourceLabel(best.Key)} הכי משתלם לבונוס: ₪{He.N(best.Avg)} בממוצע להפקדה, מול ₪{He.N(other.Avg)} ב-{SourceLabel(other.Key)}.");
        }

        if (stats.BestDay is DateTime bestDay && stats.BestDayCount > 1)
        {
            var fivePlus = book.Deals.GroupBy(d => d.Date.Date).Count(g => g.Count() >= 5);
            var line = $"השיא שלך החודש: {stats.BestDayCount} הפקדות ב-{He.DayMonth(bestDay)}.";
            if (fivePlus > 0) line += $" עשית 5 ומעלה ביום {He.Plural(fivePlus, "פעם אחת", "פעמים")}";
            if (stats.RequiredPerDay is double need && need > 0)
                line += (fivePlus > 0 ? ", והיעד" : " היעד") + $" דורש {He.R1(need)} ביום.";
            else if (fivePlus > 0) line += ".";
            lines.Add(line);
        }
        return lines.Take(3).ToList();
    }

    public static string SourceLabel(string key) =>
        Enum.TryParse<DealSource>(key, out var s) ? SalesLabels.Source(s) : key;

    /// <summary>Palon's one line on Today: open callbacks, then the target math.</summary>
    public static string Brief(CallbackCounts counts, MonthStats? stats)
    {
        var parts = new List<string>();
        if (counts.Badge > 0)
        {
            var open = counts.Badge == 1 ? "חזרה אחת פתוחה להיום" : $"{counts.Badge} חזרות פתוחות להיום";
            parts.Add(counts.Overdue > 0 ? $"יש לך {open}, {counts.Overdue} מהן באיחור." : $"יש לך {open}.");
        }
        else parts.Add("כל החזרות של היום בוצעו.");

        if (stats is { Target: int t } && t > 0)
        {
            if (stats.Remaining is 0) parts.Add("עברת את היעד. מעכשיו כל הפקדה מוסיפה ₪100.");
            else if (stats.RequiredPerDay is double need && stats.RemainingWorkDays > 0)
                parts.Add($"חסרים {stats.Remaining} חשבונות ליעד: {He.R1(need)} ביום ב-{stats.RemainingWorkDays} ימי העבודה שנותרו.");
            else parts.Add($"חסרים {stats.Remaining} חשבונות ליעד.");
        }
        return string.Join(" ", parts);
    }

    // ---- local answers (Ask overlay) ------------------------------------------

    public static string AnswerPace(MonthStats s)
    {
        if (s.Target is not int target) return "עוד אין יעד לחודש הזה. פתח את החודש ותכניס את היעד, ואחשב לך את הקצב.";
        if (s.Remaining is 0) return $"עברת את היעד: {s.Count}/{target}. מעכשיו כל הפקדה מוסיפה ₪100.";
        var text = $"נשארו {s.RemainingWorkDays} ימי עבודה ו-{s.Remaining} חשבונות";
        text += s.RequiredPerDay is double need ? $", כלומר {He.R1(need)} ביום." : ".";
        if (s.ElapsedWorkDays > 0)
            text += $" בקצב הנוכחי ({He.R1(s.PacePerDay)} ביום) תסיים בערך ב-{Math.Round(s.Projection)}.";
        return text;
    }

    public static string AnswerPay(MonthStats s, BonusRules rules)
    {
        var text = $"כרגע ₪{He.N(s.ExpectedPayIls)}: בסיס {He.N(rules.BaseSalaryIls)} ועוד ₪{He.N(s.FtdIls)} בונוסי FTD על {s.Count} הפקדות.";
        if (s.TargetBonusIls > 0) text += $" בונוס יעד: ₪{He.N(s.TargetBonusIls)}.";
        else if (s.Target is int t) text += $" מההפקדה ה-{t} כל הפקדה מוסיפה עוד ₪{rules.TargetBonusPerDealIls}.";
        text += $" מאושר עד עכשיו: ₪{He.N(s.ConfirmedPayIls)}.";
        return text;
    }

    /// <summary>This work week (Sunday → today) for the manager, as bullets.</summary>
    public static string AnswerWeek(MonthBook book, MonthStats s, DateTime asOf)
    {
        var start = asOf.Date.AddDays(-(int)asOf.DayOfWeek);
        var week = book.Deals.Where(d => d.Date.Date >= start && d.Date.Date <= asOf.Date).ToList();
        var lines = new List<string> { $"סיכום שבוע {He.DayMonth(start)}–{He.DayMonth(asOf)}" };
        lines.Add($"• {week.Count} הפקדות · ${He.N(week.Sum(d => d.Amount))}");
        if (week.Count > 0)
        {
            lines.Add("• דרגות: " + string.Join(", ", week.GroupBy(d => d.TierLabel).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
            lines.Add("• מקורות: " + string.Join(", ", week.GroupBy(d => d.Source).OrderByDescending(g => g.Count()).Select(g => $"{SalesLabels.Source(g.Key)} {g.Count()}")));
        }
        if (s.Target is int t)
        {
            var line = $"• מצב חודשי: {s.Count}/{t}";
            if (s.RequiredPerDay is double need && s.Remaining > 0) line += $", נדרש {He.R1(need)} ביום ב-{s.RemainingWorkDays} הימים שנותרו";
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    public static string AnswerLate(IEnumerable<Callback> callbacks, DateTime nowLocal)
    {
        var open = callbacks.Where(c => c.IsActive)
            .Where(c => c.DueAtUtc.ToLocalTime().Date <= nowLocal.Date)
            .OrderBy(c => c.DueAtUtc).ToList();
        if (open.Count == 0) return "אין חזרות פתוחות להיום. יפה מאוד.";
        var late = open.Count(c => c.DueAtUtc.ToLocalTime() < nowLocal);
        var head = $"יש {open.Count} חזרות פתוחות להיום" + (late > 0 ? $", {late} מהן באיחור" : "") + ":";
        return head + "\n" + string.Join("\n", open.Select(c =>
            $"• {c.DisplayLabel} ({He.When(c.DueAtUtc.ToLocalTime(), nowLocal)})" + (c.Note.Length > 0 && c.Note != c.DisplayLabel ? $": {c.Note.Split('\n')[0]}" : "")));
    }
}
