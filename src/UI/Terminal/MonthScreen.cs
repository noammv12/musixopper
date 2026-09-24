using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// The month: target ring with the pace math, expected vs. confirmed pay,
/// deposits, the per-work-day chart, source/tier/affiliate breakdowns,
/// three things Palon noticed, and the deals table with approval switches.
/// Sheets handle new deals, new months, approvals, import and history.
/// </summary>
sealed class MonthScreen : TerminalScreen
{
    int _year = DateTime.Now.Year;
    int _month = DateTime.Now.Month;
    bool _showAll;

    public MonthScreen(TerminalWindow host) : base(host)
    {
    }

    public override string Title => "החודש";

    /// <summary>Shows another month (from the history sheet).</summary>
    public void View(int year, int month)
    {
        _year = year;
        _month = month;
        _showAll = false;
    }

    public override void Render(TermData d, bool entrance)
    {
        Children.Clear();
        var book = _year == d.Now.Year && _month == d.Now.Month ? d.Book : SalesStore.Get(_year, _month);
        var isCurrent = _year == d.Now.Year && _month == d.Now.Month;
        var asOf = isCurrent ? d.Now : new DateTime(_year, _month, DateTime.DaysInMonth(_year, _month), 23, 59, 0);
        var grid = FourColumns();
        var cards = new List<FrameworkElement>();

        var header = HeaderRow(book, d, asOf);
        Place(grid, header, 0, 0, 4);
        cards.Add(header);

        if (book is null)
        {
            var empty = Kit.Glass(padding: new Thickness(30));
            var col = new StackPanel();
            col.Children.Add(Kit.T($"{He.Month(_month)} עוד לא נפתח", Display.Section, Tone.Text, FontWeights.SemiBold));
            var hint = Kit.T("הכנס את היעד שקיבלת, או ייבא את הגיליון מאקסל.", Display.Body, Tone.Muted);
            hint.Margin = new Thickness(0, 6, 0, 18);
            col.Children.Add(hint);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Kit.Pill($"פתח את {He.Month(_month)}", PillKind.Primary, () => Sheets.NewMonth(Host, _year, _month), height: 42, fontSize: 14));
            var imp = Kit.Pill("ייבוא מאקסל", PillKind.Secondary, () => Sheets.Import(Host, _year, _month), height: 42, fontSize: 14);
            imp.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(imp);
            col.Children.Add(actions);
            empty.Child = col;
            Place(grid, empty, 1, 0, 4);
            cards.Add(empty);
            Children.Add(grid);
            if (entrance) Kit.Rise(cards);
            return;
        }

        var s = SalesStats.Compute(book, d.Rules, asOf);
        var ring = RingCard(s);
        var pay = PayCard(s, d.Rules);
        var deps = DepositsCard(s);
        Place(grid, ring, 1, 0, 2);
        Place(grid, pay, 1, 2);
        Place(grid, deps, 1, 3);
        var chart = ChartCard(book, s, asOf);
        Place(grid, chart, 2, 0, 4);

        var src = Breakdown("מקור", new[] { "Affiliate", "PPC", "Organic", "Referral" }
            .Select(k => (MonthView.SourceLabel(k), s.BySource.FirstOrDefault(b => b.Key == k)?.Count ?? 0, (Brush)Tone.Accent)).ToList());
        var tiers = Breakdown("דרגה", new[] { "Margin", "Bronze", "Silver", "Gold", "VIP" }
            .Select(k => (k, s.ByTier.FirstOrDefault(b => b.Key == k)?.Count ?? 0, (Brush)Tone.Tier(k))).ToList());
        var affKeys = s.ByAffiliate.Where(a => a.Key.Length > 0).ToList();
        var affRows = affKeys.Take(4).Select(a => (a.Key, a.Count, (Brush)Tone.B("#FFC7C7CC"))).ToList();
        if (affKeys.Count > 4) affRows.Add(($"עוד {affKeys.Count - 4} שותפים", affKeys.Skip(4).Sum(a => a.Count), Tone.B("#40FFFFFF")));
        var affs = Breakdown("שותפים", affRows);
        var noticed = Noticed(book, s, d.Rules);
        Place(grid, src, 3, 0);
        Place(grid, tiers, 3, 1);
        Place(grid, affs, 3, 2);
        Place(grid, noticed, 3, 3);
        var table = DealsTable(book, d.Rules);
        Place(grid, table, 4, 0, 4);
        cards.AddRange(new[] { ring, pay, deps, chart, src, tiers, affs, noticed, table });

        Children.Add(grid);
        if (entrance) Kit.Rise(cards);
    }

    FrameworkElement HeaderRow(MonthBook? book, TermData d, DateTime asOf)
    {
        var title = new StackPanel();
        var name = He.Month(_month) + (_year != d.Now.Year ? $" {_year}" : "");
        title.Children.Add(Kit.T(name, Display.Hero, Tone.Text, FontWeights.SemiBold));
        if (book is not null)
        {
            var elapsed = SalesStats.ElapsedWorkDays(book, asOf);
            var sub = Kit.T($"{book.WorkDays} ימי עבודה · עברו {elapsed} · נותרו {Math.Max(0, book.WorkDays - elapsed)}", Display.Body, Tone.Muted);
            sub.Margin = new Thickness(0, 4, 0, 0);
            title.Children.Add(sub);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        void Btn(UIElement b)
        {
            if (actions.Children.Count > 0 && b is FrameworkElement fe) fe.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(b);
        }
        Btn(Kit.Pill("חודשים קודמים", PillKind.Secondary, () => Sheets.Months(Host, (y, m) =>
        {
            View(y, m);
            Host.Navigate(TerminalPage.Month, force: true);
        })));
        if (book is not null)
        {
            var ok = book.Deals.Count(x => x.Approved);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Kit.T("אישורים", 13.5, Tone.Text, FontWeights.Medium));
            var n = Kit.Num($"{ok}/{book.Deals.Count}", 13.5, Tone.Muted);
            n.Margin = new Thickness(6, 0, 0, 0);
            content.Children.Add(n);
            Btn(Kit.Pill("", PillKind.Secondary, () => Sheets.Approvals(Host, _year, _month), content: content));
            Btn(Kit.Pill("ייצוא", PillKind.Secondary, () => Sheets.Export(Host, book, d.Rules)));
            Btn(Kit.Pill("עסקה", PillKind.Secondary, () => Sheets.NewDeal(Host, _year, _month)));
        }
        var next = d.Now.AddMonths(1);
        var openCurrent = d.Book is null;
        Btn(Kit.Pill(openCurrent ? $"פתח את {He.Month(d.Now.Month)}" : "חודש חדש", PillKind.Primary,
            () => Sheets.NewMonth(Host, openCurrent ? d.Now.Year : next.Year, openCurrent ? d.Now.Month : next.Month)));

        var bar = Kit.Bar(title, actions);
        title.VerticalAlignment = VerticalAlignment.Bottom;
        return bar;
    }

    static FrameworkElement Stat(string label, UIElement value)
    {
        var sp = new StackPanel();
        sp.Children.Add(Kit.T(label, Display.Tiny, Tone.Muted));
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(value);
        return sp;
    }

    Border RingCard(MonthStats s)
    {
        var card = Kit.Glass(padding: new Thickness(28, 26, 28, 26), lift: true);
        var ring = new RingGauge(184, 12);
        var count = Kit.Num("0", Display.NumXL, Tone.Text, FontWeights.Light);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        Kit.CountUp(count, s.Count, v => He.N(v));
        var of = Kit.Num(s.Target is int t ? $"מתוך {t} · {Math.Round(s.PercentOfTarget ?? 0)}%" : "אין יעד", Display.Meta, Tone.Muted);
        of.FlowDirection = FlowDirection.RightToLeft;
        of.HorizontalAlignment = HorizontalAlignment.Center;
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(count);
        center.Children.Add(of);
        var dial = new Grid { Width = 184, Height = 184 };
        dial.Children.Add(ring);
        dial.Children.Add(center);
        if (s.Target is int target && target > 0) ring.SweepTo((double)s.Count / target);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
        var need = new StackPanel { Orientation = Orientation.Horizontal };
        need.Children.Add(Kit.Num(s.RequiredPerDay is double r ? He.R1(r) : "—", 26, Tone.Text, FontWeights.Medium));
        var perDay = Kit.T("ביום", 14, Tone.MutedSoft);
        perDay.Margin = new Thickness(6, 0, 0, 3);
        perDay.VerticalAlignment = VerticalAlignment.Bottom;
        need.Children.Add(perDay);
        side.Children.Add(Stat("נדרש מהיום", need));
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        pair.Children.Add(Stat("הקצב שלך", Kit.Num(He.R1(s.PacePerDay), 18, Tone.Text)));
        var projBrush = s.Target is int tt && s.Projection >= tt ? Tone.GreenText : s.Target is null ? Tone.Text : Tone.AmberText;
        var proj = Stat("תחזית", Kit.Num(He.N(s.Projection), 18, projBrush));
        ((FrameworkElement)proj).Margin = new Thickness(28, 0, 0, 0);
        pair.Children.Add(proj);
        side.Children.Add(pair);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(dial);
        Grid.SetColumn(side, 1);
        row.Children.Add(side);
        card.Child = row;
        return card;
    }

    static Grid Line(string label, string value, Brush brush)
    {
        var g = Kit.Bar(Kit.T(label, Display.Meta, brush), Kit.Num(value, Display.Meta, brush));
        g.Margin = new Thickness(0, 6, 0, 0);
        return g;
    }

    Border PayCard(MonthStats s, BonusRules rules)
    {
        var card = Kit.Glass(padding: new Thickness(26), lift: true);
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(Kit.T("שכר צפוי", Display.Meta, Tone.Muted));
        var amount = Kit.Num("", Display.NumM, Tone.Text, FontWeights.Light);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        amount.Margin = new Thickness(0, 10, 0, 0);
        Kit.CountUp(amount, s.ExpectedPayIls, v => "₪" + He.N(v));
        top.Children.Add(amount);
        g.Children.Add(top);
        var lines = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        lines.Children.Add(Line("בסיס", He.N(rules.BaseSalaryIls), Tone.MutedSoft));
        lines.Children.Add(Line("FTD", He.N(s.FtdIls), Tone.MutedSoft));
        lines.Children.Add(Line("בונוס יעד", He.N(s.TargetBonusIls), Tone.MutedSoft));
        lines.Children.Add(Line("מאושר", He.N(s.ConfirmedPayIls), Tone.GreenText));
        Grid.SetRow(lines, 2);
        g.Children.Add(lines);
        card.Child = g;
        return card;
    }

    Border DepositsCard(MonthStats s)
    {
        var card = Kit.Glass(padding: new Thickness(26), lift: true);
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(Kit.T("סך ההפקדות", Display.Meta, Tone.Muted));
        var sum = Kit.Num("", Display.NumM, Tone.Text, FontWeights.Light);
        sum.HorizontalAlignment = HorizontalAlignment.Right;
        sum.Margin = new Thickness(0, 10, 0, 0);
        Kit.CountUp(sum, (double)s.SumDeposits, v => "$" + He.N(v));
        top.Children.Add(sum);
        var avg = Kit.T($"ממוצע ${He.N(s.AvgDeposit)}", Display.Meta, Tone.Muted);
        avg.Margin = new Thickness(0, 10, 0, 0);
        top.Children.Add(avg);
        g.Children.Add(top);

        var pro = s.ByRegion.FirstOrDefault(b => b.Key == nameof(DealRegion.Pro))?.Count ?? 0;
        var il = s.ByRegion.FirstOrDefault(b => b.Key == nameof(DealRegion.Israel))?.Count ?? 0;
        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(Kit.Bar(Kit.T($"פרו {pro}", Display.Meta, Tone.MutedSoft), Kit.T($"ישראל {il}", Display.Meta, Tone.MutedSoft)));
        var split = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 0) };
        var total = Math.Max(1, pro + il);
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, pro), GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, il), GridUnitType.Star) });
        if (pro > 0) split.Children.Add(Kit.HBar(1, Tone.Accent, 150, 8));
        if (il > 0)
        {
            var b = Kit.HBar(1, Tone.Graphite, 240, 8);
            Grid.SetColumn(b, 2);
            split.Children.Add(b);
        }
        _ = total;
        bottom.Children.Add(split);
        Grid.SetRow(bottom, 2);
        g.Children.Add(bottom);
        card.Child = g;
        return card;
    }

    static FrameworkElement Legend(UIElement swatch, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (swatch is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(swatch);
        var t = Kit.T(label, Display.Tiny, Tone.Muted);
        t.Margin = new Thickness(7, 0, 0, 0);
        sp.Children.Add(t);
        return sp;
    }

    Border ChartCard(MonthBook book, MonthStats s, DateTime asOf)
    {
        var card = Kit.Glass(padding: new Thickness(28, 24, 28, 20));
        var col = new StackPanel();
        var dashLine = new System.Windows.Shapes.Line { X1 = 0, X2 = 16, Stroke = Tone.B("#59FFFFFF"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 } };
        var dashBox = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Tone.B("#66FFFFFF") };
        var head = Kit.Bar(Kit.SectionTitle("הפקדות לפי יום", 16),
            Legend(new Border { Width = 16, Height = 3, CornerRadius = new CornerRadius(2), Background = Tone.Accent }, "מצטבר"),
            Legend(dashLine, "קצב ליעד"),
            Legend(dashBox, "נדרש"));
        col.Children.Add(head);
        var chart = new DayChart(MonthView.Bars(book, s, asOf), s.Target) { Margin = new Thickness(0, 16, 0, 0) };
        chart.Grow();
        col.Children.Add(chart);
        card.Child = col;
        return card;
    }

    static Border Breakdown(string title, List<(string Label, int Count, Brush Color)> rows)
    {
        var card = Kit.Glass(Display.ListRadius, new Thickness(22));
        var col = new StackPanel();
        col.Children.Add(Kit.SectionTitle(title, Display.Body));
        var max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Count));
        var i = 0;
        foreach (var (label, count, color) in rows)
        {
            var labelText = Kit.Auto(Kit.T(label, Display.Meta, title == "דרגה" ? color : Tone.Text));
            var line = Kit.Bar(labelText, Kit.Num(count.ToString(), Display.Meta, Tone.Muted));
            line.Margin = new Thickness(0, 14, 0, 6);
            col.Children.Add(line);
            col.Children.Add(Kit.HBar((double)count / max, color, 250 + i++ * 70));
        }
        if (rows.Count == 0)
        {
            var none = Kit.T("אין עדיין", Display.Meta, Tone.Muted);
            none.Margin = new Thickness(0, 14, 0, 0);
            col.Children.Add(none);
        }
        card.Child = col;
        return card;
    }

    static Border Noticed(MonthBook book, MonthStats s, BonusRules rules)
    {
        var card = Kit.Glass(Display.ListRadius, new Thickness(22));
        var col = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new PalonAvatar { Width = 34, Height = 34, Mood = PalonMood.Idle });
        var t = Kit.SectionTitle("Palon שם לב", Display.Body);
        t.Margin = new Thickness(10, 0, 0, 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        col.Children.Add(head);
        var lines = MonthView.Insights(book, s, rules);
        if (lines.Count == 0) lines.Add("כשיהיו עסקאות, אספר לך מה בולט בחודש.");
        foreach (var line in lines)
        {
            var p = Kit.T(line, 13.5, Tone.TextSoft, wrap: true);
            p.LineHeight = 21.6;
            p.Margin = new Thickness(0, 12, 0, 0);
            col.Children.Add(p);
        }
        card.Child = col;
        return card;
    }

    static readonly double[] Columns = { 1.8, 0.6, 0.9, 1, 1.4, 0.9, 0.8 };

    Grid DealRow(Deal deal, BonusRules rules, int? target, int index, int count)
    {
        var g = new Grid();
        foreach (var w in Columns)
        {
            if (g.ColumnDefinitions.Count > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        }
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        var targetBonus = target is int t && t > 0 && index >= t - 1 ? rules.TargetBonusPerDealIls : 0;
        var tier = deal.TierLabel;
        var cells = new UIElement[]
        {
            Kit.Auto(Kit.T(deal.ClientName, 13.5, Tone.Text, FontWeights.Medium)),
            Kit.Mono(He.DayMonth(deal.Date), 12.5, Tone.Muted),
            Kit.T(tier, 12.5, Tone.Tier(tier)),
            Kit.T(SalesLabels.Source(deal.Source), 13.5, Tone.Muted),
            Kit.Auto(Kit.T(string.IsNullOrWhiteSpace(deal.Affiliate) ? "—" : deal.Affiliate!, 13.5, Tone.Muted)),
            Kit.Num("$" + He.N(deal.Amount), 13.5, Tone.Text),
            Kit.Num("₪" + He.N(rules.FtdBonus(deal) + targetBonus), 13.5, Tone.MutedSoft),
        };
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Center;
                if (fe.FlowDirection == FlowDirection.LeftToRight && fe is TextBlock tb) tb.HorizontalAlignment = HorizontalAlignment.Right;
            }
            Grid.SetColumn(cells[i], i * 2);
            g.Children.Add(cells[i]);
        }
        var sw = new PillSwitch(deal.Approved) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        sw.Toggled += on => SetApproved(deal.Id, on);
        Grid.SetColumn(sw, Columns.Length * 2);
        g.Children.Add(sw);
        var remove = Kit.IconButton(Icons.Close, 26, () => RemoveDeal(deal), icon: 12);
        Grid.SetColumn(remove, Columns.Length * 2 + 1);
        g.Children.Add(remove);
        _ = count;
        return g;
    }

    void SetApproved(string dealId, bool on)
    {
        var book = SalesStore.Get(_year, _month);
        if (book?.Deals.FirstOrDefault(x => x.Id == dealId) is not { } deal) return;
        deal.Approved = on;
        SalesStore.SaveMonth(book);
    }

    void RemoveDeal(Deal deal)
    {
        var book = SalesStore.Get(_year, _month);
        if (book is null) return;
        var index = book.Deals.FindIndex(x => x.Id == deal.Id);
        if (index < 0) return;
        var removed = book.Deals[index];
        book.Deals.RemoveAt(index);
        if (!SalesStore.SaveMonth(book)) return;
        var (y, m) = (_year, _month);
        Host.Toast($"העסקה של {removed.ClientName} נמחקה", "בטל", () =>
        {
            var again = SalesStore.Get(y, m);
            if (again is null) return;
            again.Deals.Insert(Math.Min(index, again.Deals.Count), removed);
            SalesStore.SaveMonth(again);
        });
    }

    Border DealsTable(MonthBook book, BonusRules rules)
    {
        var card = Kit.Glass(padding: new Thickness(14, 22, 14, 14));
        var col = new StackPanel();
        var head = Kit.Bar(Kit.SectionTitle("עסקאות", 16), Kit.T("דרגה ובונוס מחושבים לבד", Display.Tiny, Tone.Muted));
        head.Margin = new Thickness(12, 0, 12, 10);
        col.Children.Add(head);

        var ordered = SalesStats.Ordered(book.Deals);
        var indexed = ordered.Select((deal, i) => (deal, i)).Reverse().ToList();
        if (indexed.Count == 0)
        {
            var none = Kit.T("עוד אין עסקאות בחודש הזה.", 13.5, Tone.Muted);
            none.Margin = new Thickness(12, 4, 12, 8);
            col.Children.Add(none);
        }
        FrameworkElement Row(object o)
        {
            var (deal, i) = ((Deal, int))o;
            var row = Kit.ListRow(DealRow(deal, rules, book.Target, i, ordered.Count), new Thickness(12, 10, 12, 10));
            row.CornerRadius = new CornerRadius(14);
            var remove = ((Grid)row.Child).Children.OfType<Border>().Last();
            Kit.Reveal(row, remove);
            return row;
        }
        if (_showAll && indexed.Count > 6)
        {
            var list = new VirtualList(Row) { MaxHeight = 560, ItemsSource = indexed.Cast<object>().ToList() };
            col.Children.Add(list);
        }
        else
        {
            foreach (var item in indexed.Take(6)) col.Children.Add(Row(item));
        }
        if (indexed.Count > 6)
        {
            var toggle = Kit.Pill(_showAll ? "הצג פחות" : $"הצג את כל {indexed.Count} העסקאות", PillKind.Ghost, () =>
            {
                _showAll = !_showAll;
                Host.Refresh();
            }, height: 34, fontSize: 13);
            toggle.HorizontalAlignment = HorizontalAlignment.Center;
            toggle.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(toggle);
        }
        card.Child = col;
        return card;
    }
}
