namespace Palon;

enum CallbackGroup
{
    Overdue,
    Today,
    Tomorrow,
    LaterThisWeek,
    Later,
}

enum SnoozeKind
{
    TenMinutes,
    OneHour,
    Tomorrow,
}

/// <summary>One quick-pick time chip. Disabled chips stay in the row (stable
/// layout) but can't be chosen — e.g. "Tonight" after 19:00.</summary>
sealed record QuickPick(string Key, string Label, DateTime DueLocal, bool Enabled = true);

/// <summary>Badge numbers: still-owed callbacks due later today, and past due.</summary>
readonly record struct CallbackCounts(int DueToday, int Overdue)
{
    public int Badge => DueToday + Overdue;
}

/// <summary>
/// Pure callback logic — grouping, quick-pick times, snooze/done
/// transitions, badge counts. Everything takes "now" so it's testable and
/// all day math is local time. The user's work week is Israeli: Sunday to
/// Thursday, so "tomorrow" never lands on Friday or Saturday.
/// </summary>
static class CallbackPlanner
{
    /// <summary>Past due by more than this when it fires → shown as missed.</summary>
    public static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan TonightAt = TimeSpan.FromHours(19);
    public static readonly TimeSpan MorningAt = TimeSpan.FromHours(10);

    public static bool IsWorkDay(DayOfWeek day) => day is not (DayOfWeek.Friday or DayOfWeek.Saturday);

    public static bool IsMissed(Callback callback, DateTime nowUtc) => nowUtc - callback.DueAtUtc > MissedThreshold;

    // ---- grouping --------------------------------------------------------------

    public static CallbackGroup GroupOf(DateTime dueLocal, DateTime nowLocal)
    {
        if (dueLocal < nowLocal) return CallbackGroup.Overdue;
        var today = nowLocal.Date;
        if (dueLocal.Date == today) return CallbackGroup.Today;
        if (dueLocal.Date == today.AddDays(1)) return CallbackGroup.Tomorrow;
        // The week runs Sunday → Saturday; "later this week" stops before next Sunday.
        var nextSunday = today.AddDays(7 - (int)today.DayOfWeek);
        return dueLocal < nextSunday ? CallbackGroup.LaterThisWeek : CallbackGroup.Later;
    }

    /// <summary>Active callbacks bucketed for display, groups in order,
    /// each sorted by due time; empty groups are omitted.</summary>
    public static List<(CallbackGroup Group, List<Callback> Items)> Group(IEnumerable<Callback> callbacks, DateTime nowLocal) =>
        callbacks
            .Where(c => c.IsActive)
            .OrderBy(c => c.DueAtUtc)
            .GroupBy(c => GroupOf(c.DueAtUtc.ToLocalTime(), nowLocal))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, g.ToList()))
            .ToList();

    public static string GroupTitle(CallbackGroup group) => group switch
    {
        CallbackGroup.Overdue => "Overdue",
        CallbackGroup.Today => "Today",
        CallbackGroup.Tomorrow => "Tomorrow",
        CallbackGroup.LaterThisWeek => "Later this week",
        _ => "Later",
    };

    public static CallbackCounts Counts(IEnumerable<Callback> callbacks, DateTime nowLocal)
    {
        int today = 0, overdue = 0;
        foreach (var c in callbacks)
        {
            if (!c.IsActive) continue;
            var due = c.DueAtUtc.ToLocalTime();
            if (due < nowLocal) overdue++;
            else if (due.Date == nowLocal.Date) today++;
        }
        return new CallbackCounts(today, overdue);
    }

    // ---- quick picks -----------------------------------------------------------

    /// <summary>The next work-day morning after today (Fri/Sat skip to Sunday).</summary>
    public static DateTime NextWorkMorning(DateTime nowLocal)
    {
        var day = nowLocal.Date.AddDays(1);
        while (!IsWorkDay(day.DayOfWeek)) day = day.AddDays(1);
        return day + MorningAt;
    }

    /// <summary>Sunday 10:00 of the coming week (never today).</summary>
    public static DateTime NextSundayMorning(DateTime nowLocal)
    {
        var days = 7 - (int)nowLocal.DayOfWeek; // Sunday → 7, Saturday → 1
        return nowLocal.Date.AddDays(days) + MorningAt;
    }

    /// <summary>The four standard chips: in 1h, tonight 19:00, tomorrow
    /// 10:00, Sunday 10:00. "Tonight" is disabled once it's 19:00 or later;
    /// "Sunday" is disabled when "tomorrow" already resolves to it.</summary>
    public static List<QuickPick> QuickPicks(DateTime nowLocal)
    {
        var now = TrimSeconds(nowLocal);
        var tomorrow = NextWorkMorning(now);
        var sunday = NextSundayMorning(now);
        var tomorrowIsLiteral = tomorrow.Date == now.Date.AddDays(1);
        return new List<QuickPick>
        {
            new("in1h", "In 1h", now.AddHours(1)),
            new("tonight", "Tonight 19:00", now.Date + TonightAt, Enabled: now.TimeOfDay < TonightAt),
            new("tomorrow", tomorrowIsLiteral ? "Tomorrow 10:00" : $"{tomorrow:ddd} 10:00", tomorrow),
            new("sunday", "Sunday 10:00", sunday, Enabled: sunday != tomorrow),
        };
    }

    // ---- transitions -----------------------------------------------------------

    public static DateTime SnoozeTargetLocal(SnoozeKind kind, DateTime nowLocal) => kind switch
    {
        SnoozeKind.TenMinutes => TrimSeconds(nowLocal).AddMinutes(10),
        SnoozeKind.OneHour => TrimSeconds(nowLocal).AddHours(1),
        _ => NextWorkMorning(nowLocal),
    };

    public static Callback Snooze(Callback callback, DateTime newDueUtc) => callback with
    {
        DueAtUtc = newDueUtc,
        Status = CallbackStatus.Snoozed,
        SnoozeCount = callback.SnoozeCount + 1,
        CompletedUtc = null,
    };

    public static Callback Snooze(Callback callback, SnoozeKind kind, DateTime nowLocal) =>
        Snooze(callback, SnoozeTargetLocal(kind, nowLocal).ToUniversalTime());

    public static Callback MarkDone(Callback callback, DateTime nowUtc) =>
        callback with { Status = CallbackStatus.Done, CompletedUtc = nowUtc };

    public static Callback Cancel(Callback callback, DateTime nowUtc) =>
        callback with { Status = CallbackStatus.Cancelled, CompletedUtc = nowUtc };

    /// <summary>Undo done/cancel: back to owed, keeping its snooze history.</summary>
    public static Callback Reopen(Callback callback) => callback with
    {
        Status = callback.SnoozeCount > 0 ? CallbackStatus.Snoozed : CallbackStatus.Open,
        CompletedUtc = null,
    };

    static DateTime TrimSeconds(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Kind);
}
