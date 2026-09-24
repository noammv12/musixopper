using System.IO;
using System.IO.Compression;
using Palon.Sales;
using Xunit;

namespace Palon.Tests;

public class SalesTests
{
    // day, region, sheet tier, source, amount, affiliate — the real September 2026 sheet.
    const string SeptemberRows =
        "1 פרו Margin Affiliate 1000 OMRI INVEST;1 פרו Bronze Affiliate 3000;2 פרו Margin Affiliate 1000 אינסיידרס;" +
        "2 ישראל Bronze Organic 12000;3 פרו Bronze PPC 3000;6 פרו Margin Affiliate 1000;6 ישראל Margin PPC 300;" +
        "6 פרו Margin Affiliate 1000 אינסיידרס;6 פרו Gold Affiliate 25000 SOLO INVESTING;7 פרו Bronze Affiliate 3000 DVIR SHALOM;" +
        "7 ישראל Margin Affiliate 700 רז גמליאל;7 פרו Margin PPC 1000;8 ישראל Margin PPC 300;8 פרו Margin Affiliate 1000 אינסיידרס;" +
        "8 פרו Margin Affiliate 1000 אינסיידרס;8 פרו Margin Affiliate 1000 אינסיידרס;9 פרו Margin PPC 1000;" +
        "9 ישראל Margin Affiliate 300 סייקלס;10 פרו Gold Affiliate 49000;14 פרו Bronze PPC 3000;" +
        "14 פרו Margin Affiliate 1000 Katalan Investment;14 פרו Silver Affiliate 16000 OMRI INVEST;" +
        "14 פרו Bronze Affiliate 6000 OMRI INVEST;14 ישראל Bronze PPC 1600;14 פרו Bronze Affiliate 3000 DANIEL DAMARI;" +
        "15 פרו Margin Affiliate 2000 MICHA;15 פרו Bronze Affiliate 3000 SKILLS;16 פרו Silver Affiliate 10000 DAVID ARIEL;" +
        "17 פרו Bronze Affiliate 3000 OMRI INVEST;17 פרו Margin Organic 1000;17 פרו Bronze Affiliate 3000 DAVID ARIEL;" +
        "17 פרו Margin Affiliate 1000 DAVID ARIEL;17 פרו Silver Affiliate 10000 DAVID ARIEL;22 פרו Silver Affiliate 10000 DAVID ARIEL;" +
        "23 פרו Margin Affiliate 2000 DAVID ARIEL;23 פרו Bronze Affiliate 3000 אינסיידרס;23 ישראל Margin Organic 1500;" +
        "23 פרו Margin Affiliate 1000 DAVID ARIEL;23 פרו Margin Affiliate 2000 MICHA;24 פרו Silver Affiliate 10000 DVIR SHALOM";

    static List<string[]> SeptemberSheet()
    {
        var rows = new List<string[]> { new[] { "Client Name", "תאריך", "ישראל/פרו", "CLUB", "Source", "סכום הפקדה", "Approved", "FTD Bonus", "הערה" } };
        int n = 0;
        foreach (var item in SeptemberRows.Split(';'))
        {
            var p = item.Split(' ', 6);
            n++;
            rows.Add(new[] { $"Client {n}", $"{p[0]}/9/2026", p[1], p[2], p[3], p[4], "", "", p.Length > 5 ? p[5] : "" });
        }
        return rows;
    }

    static MonthBook September()
    {
        var result = SalesImport.ParseRows(SeptemberSheet(), 2026, 9);
        Assert.Empty(result.Warnings);
        return new MonthBook { Year = 2026, Month = 9, Target = 55, Deals = result.Deals };
    }

    static Deal D(DealRegion r, DealSource s, decimal amount, bool approved = false, int day = 1) =>
        new() { Region = r, Source = s, Amount = amount, Approved = approved, Date = new DateTime(2026, 9, day) };

    [Fact]
    public void September_golden()
    {
        var book = September();
        var s = SalesStats.Compute(book, BonusRules.Default(), new DateTime(2026, 9, 24));
        Assert.Equal(40, s.Count);
        Assert.Equal(198_700m, s.SumDeposits);
        Assert.Equal(8_200, s.FtdIls);
        Assert.Equal(0, s.TargetBonusIls);
        Assert.Equal(18_200, s.ExpectedPayIls);
        Assert.Equal(10_000, s.ConfirmedPayIls); // nothing approved yet
        Assert.Equal(20, s.WorkDays);
        Assert.Equal(16, s.ElapsedWorkDays);
        Assert.Equal(4, s.RemainingWorkDays);
        Assert.Equal(2.5, s.PacePerDay, 6);
        Assert.Equal(3.75, s.RequiredPerDay!.Value, 6);
        Assert.Equal(50, s.Projection, 6);
        Assert.Equal(15, s.Remaining);
        Assert.Equal(4967.5m, s.AvgDeposit);
        Assert.Equal(new DateTime(2026, 9, 14), s.BestDay);
        Assert.Equal(6, s.BestDayCount);
        Assert.Equal(7, s.ByRegion.Single(b => b.Key == "Israel").Count);
        Assert.Equal(7, s.ByAffiliate.Single(b => b.Key == "DAVID ARIEL").Count);
        Assert.Equal(30, s.BySource.Single(b => b.Key == "Affiliate").Count);
    }

    [Fact]
    public void Import_keeps_sheet_tier_only_when_it_differs()
    {
        var deals = September().Deals;
        Assert.All(deals, d => Assert.Equal(DealOrigin.Import, d.CreatedFrom));
        // Israel 1,600 labelled Bronze by the sheet but Margin by amount.
        var il = deals.Single(d => d.Region == DealRegion.Israel && d.Amount == 1600);
        Assert.Equal("Bronze", il.TierLabel);
        Assert.Equal(DealTier.Margin, il.Tier);
        Assert.Null(deals.Single(d => d.Amount == 49000).TierOverride);
        Assert.Equal("OMRI INVEST", deals[0].Affiliate);
    }

    [Theory]
    [InlineData(2999, DealTier.Margin)]
    [InlineData(3000, DealTier.Bronze)]
    [InlineData(9999, DealTier.Bronze)]
    [InlineData(10000, DealTier.Silver)]
    [InlineData(25000, DealTier.Gold)]
    [InlineData(100000, DealTier.VIP)]
    public void Tier_thresholds(decimal amount, object tier) => Assert.Equal((DealTier)tier, Tiers.ForAmount(amount));

    [Theory]
    [InlineData(DealRegion.Israel, DealSource.PPC, 100, 600)]
    [InlineData(DealRegion.Israel, DealSource.Referral, 50000, 600)]
    [InlineData(DealRegion.Israel, DealSource.Affiliate, 100, 200)]
    [InlineData(DealRegion.Israel, DealSource.Organic, 50000, 200)]
    [InlineData(DealRegion.Pro, DealSource.PPC, 2999, 0)]
    [InlineData(DealRegion.Pro, DealSource.Affiliate, 2999, 0)]
    [InlineData(DealRegion.Pro, DealSource.Affiliate, 3000, 200)]
    [InlineData(DealRegion.Pro, DealSource.Organic, 9999, 200)]
    [InlineData(DealRegion.Pro, DealSource.PPC, 3000, 600)]
    [InlineData(DealRegion.Pro, DealSource.Referral, 9999, 600)]
    [InlineData(DealRegion.Pro, DealSource.Affiliate, 10000, 400)]
    [InlineData(DealRegion.Pro, DealSource.PPC, 10000, 800)]
    [InlineData(DealRegion.Pro, DealSource.Referral, 200000, 800)]
    public void Ftd_bonus_rules(object r, object s, decimal amount, int bonus) =>
        Assert.Equal(bonus, BonusRules.Default().FtdBonus(D((DealRegion)r, (DealSource)s, amount)));

    [Theory]
    [InlineData(54, 55, 0)]
    [InlineData(55, 55, 100)]  // the 55th itself earns it
    [InlineData(60, 55, 600)]
    [InlineData(60, null, 0)]
    public void Target_bonus_from_the_target_th_deposit(int count, int? target, int bonus) =>
        Assert.Equal(bonus, SalesStats.TargetBonus(count, target, BonusRules.Default()));

    [Fact]
    public void Confirmed_pay_counts_only_approved_deals()
    {
        var book = new MonthBook
        {
            Year = 2026, Month = 9, Target = 2,
            Deals = { D(DealRegion.Pro, DealSource.PPC, 10000, approved: true), D(DealRegion.Israel, DealSource.PPC, 500), D(DealRegion.Pro, DealSource.Affiliate, 3000, approved: true) },
        };
        var s = SalesStats.Compute(book, BonusRules.Default(), new DateTime(2026, 9, 30));
        Assert.Equal(10_000 + 800 + 600 + 200 + 200, s.ExpectedPayIls); // 3 deals, target 2 → 2 × 100
        Assert.Equal(10_000 + 800 + 200 + 100, s.ConfirmedPayIls);      // 2 approved → reaches target
        Assert.Equal(2, s.ApprovedCount);
    }

    [Fact]
    public void Edited_rules_change_the_bonus()
    {
        var rules = BonusRules.Default();
        rules.BaseSalaryIls = 12_000;
        rules.Ftd.Single(r => r.Region == DealRegion.Pro && r.MinAmount == 10_000 && r.Sources.Contains(DealSource.PPC)).BonusIls = 1000;
        Assert.Equal(1000, rules.FtdBonus(D(DealRegion.Pro, DealSource.PPC, 20000)));
        Assert.Equal(13_000, SalesStats.Pay(new[] { D(DealRegion.Pro, DealSource.PPC, 20000) }, null, rules));
    }

    [Fact]
    public void September_2026_work_days()
    {
        int[] expected = { 1, 2, 3, 6, 7, 8, 9, 10, 14, 15, 16, 17, 20, 22, 23, 24, 27, 28, 29, 30 };
        Assert.Equal(expected, WorkCalendar.WorkDates(2026, 9).Select(d => d.Day).ToArray());
        Assert.Equal("Rosh Hashana", WorkCalendar.HolidayName(new DateTime(2026, 9, 13)));
        Assert.Equal("Yom Kippur", WorkCalendar.HolidayName(new DateTime(2026, 9, 21)));
    }

    [Fact]
    public void October_2026_has_21_work_days() => Assert.Equal(21, WorkCalendar.WorkDaysInMonth(2026, 10));

    [Theory]
    [InlineData(2024, 5, 14)] // 5 Iyar on Tuesday — stays
    [InlineData(2025, 5, 1)]  // 5 Iyar on Friday — moved to Thursday
    [InlineData(2026, 4, 22)] // 5 Iyar on Wednesday — stays
    [InlineData(2027, 5, 12)] // 5 Iyar on Wednesday
    [InlineData(2028, 5, 2)]  // 5 Iyar on Monday — postponed to Tuesday
    public void Independence_day_shift(int y, int m, int d) =>
        Assert.Equal("Independence Day", WorkCalendar.HolidayName(new DateTime(y, m, d)));

    [Fact]
    public void Pesach_and_shavuot_5786()
    {
        Assert.Equal("Pesach", WorkCalendar.HolidayName(new DateTime(2026, 4, 2)));  // 15 Nisan
        Assert.Equal("Pesach", WorkCalendar.HolidayName(new DateTime(2026, 4, 8)));  // 21 Nisan
        Assert.Null(WorkCalendar.HolidayName(new DateTime(2026, 4, 5)));             // Chol HaMoed works
        Assert.Null(WorkCalendar.HolidayName(new DateTime(2026, 4, 1)));             // eve works
        Assert.Equal("Shavuot", WorkCalendar.HolidayName(new DateTime(2026, 5, 22)));
    }

    [Fact]
    public void Leap_year_holidays_land_right()
    {
        // 5787 (2026–27) is a leap year: Pesach 22 Apr 2027, Shavuot 11 Jun 2027.
        Assert.Equal("Pesach", WorkCalendar.HolidayName(new DateTime(2027, 4, 22)));
        Assert.Equal("Shavuot", WorkCalendar.HolidayName(new DateTime(2027, 6, 11)));
    }

    [Fact]
    public void Work_day_override_wins()
    {
        var book = new MonthBook { Year = 2026, Month = 9, WorkDaysOverride = 18 };
        Assert.Equal(18, book.WorkDays);
        var s = SalesStats.Compute(book, BonusRules.Default(), new DateTime(2026, 9, 30));
        Assert.Equal(18, s.ElapsedWorkDays);
        Assert.Equal(0, s.RemainingWorkDays);
    }

    [Fact]
    public void Remind_to_set_target()
    {
        var now = new DateTime(2026, 10, 1, 9, 0, 0);
        Assert.True(SalesStats.ShouldRemindToSetTarget(null, now));
        Assert.True(SalesStats.ShouldRemindToSetTarget(new MonthBook { Year = 2026, Month = 10 }, now));
        Assert.False(SalesStats.ShouldRemindToSetTarget(new MonthBook { Year = 2026, Month = 10, Target = 50 }, now));
    }

    [Fact]
    public void Csv_export_round_trips()
    {
        var book = September();
        book.Deals[0].Approved = true;
        var csv = SalesImport.ToCsv(book, BonusRules.Default());
        var back = SalesImport.ParseRows(SalesImport.ParseCsv(csv), 2026, 9);
        Assert.Empty(back.Warnings);
        Assert.Equal(40, back.Deals.Count);
        Assert.Equal(198_700m, back.Deals.Sum(d => d.Amount));
        Assert.True(back.Deals[0].Approved);
        Assert.Equal(DealSource.Referral, SalesLabels.ParseSource("חבר מביא חבר"));
    }

    [Fact]
    public void Import_stops_at_first_empty_name()
    {
        var rows = new List<string[]>
        {
            new[] { "h" },
            new[] { "Client 1", "3", "פרו", "", "PPC", "3,000" },
            new[] { "", "4", "פרו", "", "PPC", "5000" },
            new[] { "Client 3", "5", "פרו", "", "PPC", "5000" },
        };
        var r = SalesImport.ParseRows(rows, 2026, 9);
        Assert.Single(r.Deals);
        Assert.Equal(new DateTime(2026, 9, 3), r.Deals[0].Date);
        Assert.Equal(3000m, r.Deals[0].Amount);
    }

    [Fact]
    public void Xlsx_reader_reads_shared_strings_and_numbers()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "s.xlsx");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                void Put(string name, string xml)
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open());
                    w.Write(xml);
                }
                const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Put("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>Client Name</t></si><si><t>Client 1</t></si><si><t>ישראל</t></si><si><t>PPC</t></si></sst>");
                Put("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>" +
                    "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row>" +
                    "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>46273</v></c><c r=\"C2\" t=\"s\"><v>2</v></c>" +
                    "<c r=\"E2\" t=\"s\"><v>3</v></c><c r=\"F2\"><v>300</v></c><c r=\"G2\" t=\"b\"><v>1</v></c></row>" +
                    "</sheetData></worksheet>");
            }
            var r = SalesImport.FromFile(path, 2026, 9);
            var deal = Assert.Single(r.Deals);
            Assert.Equal(new DateTime(2026, 9, 8), deal.Date);
            Assert.Equal(DealRegion.Israel, deal.Region);
            Assert.Equal(DealSource.PPC, deal.Source);
            Assert.Equal(300m, deal.Amount);
            Assert.True(deal.Approved);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Store_round_trips_months_and_rules()
    {
        var dir = Directory.CreateTempSubdirectory();
        SalesStore.PathOverride = Path.Combine(dir.FullName, "sales.json");
        try
        {
            var book = SalesStore.Open(2026, 9);
            book.Target = 55;
            book.Deals.AddRange(September().Deals);
            Assert.True(SalesStore.SaveMonth(book));
            SalesStore.Open(2026, 10);
            var rules = BonusRules.Default();
            rules.BaseSalaryIls = 11_000;
            Assert.True(SalesStore.SaveRules(rules));

            var months = SalesStore.ListMonths();
            Assert.Equal(new[] { "2026-10", "2026-09" }, months.Select(m => m.Key));
            var sep = SalesStore.Get(2026, 9)!;
            Assert.Equal(55, sep.Target);
            Assert.Equal(40, sep.Deals.Count);
            Assert.Equal("Bronze", sep.Deals.Single(d => d.Amount == 1600).TierLabel);
            Assert.Equal(11_000, SalesStore.LoadRules().BaseSalaryIls);
            Assert.Equal(8_200, SalesStats.SumFtd(sep.Deals, SalesStore.LoadRules()));

            File.WriteAllText(SalesStore.PathOverride, "{ broken");
            Assert.False(SalesStore.SaveMonth(new MonthBook { Year = 2026, Month = 11 }));
            Assert.Equal("{ broken", File.ReadAllText(SalesStore.PathOverride)); // never clobbered
        }
        finally
        {
            SalesStore.PathOverride = null;
            dir.Delete(true);
        }
    }
}
