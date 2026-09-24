using System.Globalization;

namespace Palon.Sales;

/// <summary>
/// Israeli work days: Sun–Thu minus public holidays on those days, computed
/// from the Hebrew calendar so it works for any year. Chol HaMoed and holiday
/// eves count as work days.
///
/// Holidays: Rosh Hashana (1–2 Tishrei), Yom Kippur (10 Tishrei), Sukkot
/// (15 Tishrei), Shemini Atzeret/Simchat Torah (22 Tishrei), Pesach first and
/// seventh day (15, 21 Nisan), Shavuot (6 Sivan), Independence Day.
/// Independence Day assumption (the 2004+ Knesset rule): 5 Iyar; if it falls
/// on Friday or Saturday it moves back to Thursday (4 or 3 Iyar); if on
/// Monday it moves to Tuesday (6 Iyar). Diaspora second days are not holidays.
/// </summary>
static class WorkCalendar
{
    static readonly HebrewCalendar Hebrew = new();

    public static bool IsWorkWeekday(DayOfWeek d) => d is not (DayOfWeek.Friday or DayOfWeek.Saturday);

    public static bool IsWorkDay(DateTime date) =>
        IsWorkWeekday(date.DayOfWeek) && HolidayName(date) is null;

    /// <summary>The holiday's name if <paramref name="date"/> is an Israeli public holiday, else null.</summary>
    public static string? HolidayName(DateTime date)
    {
        date = date.Date;
        int hy = Hebrew.GetYear(date), hm = Hebrew.GetMonth(date), hd = Hebrew.GetDayOfMonth(date);
        // .NET numbers Hebrew months from Tishrei = 1; a leap year inserts
        // Adar II as month 7, pushing Nisan from 7 to 8.
        int nisan = Hebrew.IsLeapYear(hy) ? 8 : 7;
        if (hm == 1)
        {
            switch (hd)
            {
                case 1: case 2: return "Rosh Hashana";
                case 10: return "Yom Kippur";
                case 15: return "Sukkot";
                case 22: return "Shemini Atzeret";
            }
        }
        if (hm == nisan && (hd == 15 || hd == 21)) return "Pesach";
        if (hm == nisan + 2 && hd == 6) return "Shavuot";
        if (date == IndependenceDay(hy)) return "Independence Day";
        return null;
    }

    /// <summary>Observed Yom HaAtzmaut for Hebrew year <paramref name="hebrewYear"/>.</summary>
    public static DateTime IndependenceDay(int hebrewYear)
    {
        int iyar = (Hebrew.IsLeapYear(hebrewYear) ? 8 : 7) + 1;
        var day = Hebrew.ToDateTime(hebrewYear, iyar, 5, 0, 0, 0, 0);
        return day.DayOfWeek switch
        {
            DayOfWeek.Friday => day.AddDays(-1),
            DayOfWeek.Saturday => day.AddDays(-2),
            DayOfWeek.Monday => day.AddDays(1),
            _ => day,
        };
    }

    public static IEnumerable<DateTime> WorkDates(int year, int month)
    {
        int days = DateTime.DaysInMonth(year, month);
        for (int d = 1; d <= days; d++)
        {
            var date = new DateTime(year, month, d);
            if (IsWorkDay(date)) yield return date;
        }
    }

    public static int WorkDaysInMonth(int year, int month) => WorkDates(year, month).Count();

    /// <summary>Work days in the month on or before <paramref name="asOf"/> (inclusive).</summary>
    public static int WorkDaysElapsed(int year, int month, DateTime asOf) =>
        WorkDates(year, month).Count(d => d <= asOf.Date);
}
