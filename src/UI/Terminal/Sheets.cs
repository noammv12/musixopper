using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// The modal glass sheets: new deal (live tier and bonus preview), new
/// month (target + auto work days), end-of-month approvals, import from
/// the Excel sheet, previous months, export. Each builds its body and hands
/// it to <see cref="TerminalWindow.ShowSheet"/>.
/// </summary>
static class Sheets
{
    static Grid Head(string title, string? subtitle, Action close, params UIElement[] extra)
    {
        var text = new StackPanel();
        text.Children.Add(Kit.T(title, Display.Sheet, Tone.Text, FontWeights.SemiBold));
        if (subtitle is not null)
        {
            var sub = Kit.T(subtitle, 13.5, Tone.MutedSoft, wrap: true);
            sub.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(sub);
        }
        var items = extra.ToList();
        var x = Kit.IconButton(Icons.Close, 34, close);
        x.Margin = new Thickness(8, 0, 0, 0);
        items.Add(x);
        var bar = Kit.Bar(text, items.ToArray());
        text.VerticalAlignment = VerticalAlignment.Top;
        x.VerticalAlignment = VerticalAlignment.Top;
        return bar;
    }

    static StackPanel Body() => new();

    static void Gap(StackPanel sp, UIElement el, double gap = 18)
    {
        if (sp.Children.Count > 0 && el is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + gap, fe.Margin.Right, fe.Margin.Bottom);
        sp.Children.Add(el);
    }

    static StackPanel Caption(string label, UIElement content)
    {
        var sp = new StackPanel();
        sp.Children.Add(Kit.T(label, 12, Tone.Muted));
        if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(content);
        return sp;
    }

    static decimal ParseAmount(string text)
    {
        var digits = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---- new deal ------------------------------------------------------------------

    public static void NewDeal(TerminalWindow host, int? year = null, int? month = null)
    {
        var now = DateTime.Now;
        var y = year ?? now.Year;
        var m = month ?? now.Month;
        var rules = SalesStore.LoadRules();
        var existing = SalesStore.Get(y, m);
        var count = existing?.Deals.Count ?? 0;
        var target = existing?.Target;
        var region = DealRegion.Pro;
        var source = DealSource.Affiliate;
        Action close = () => { };

        var body = Body();
        Gap(body, Head("עסקה חדשה", $"ההפקדה ה-{count + 1} שלך ב{He.Month(m)}", () => close()));

        var (nameFrame, name) = Kit.LabeledField("שם הלקוח", "לדוגמה: דני לוי");
        var (amountFrame, amount) = Kit.LabeledField("סכום הפקדה ($)", "3000", ltr: true);
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.Children.Add(nameFrame);
        Grid.SetColumn(amountFrame, 2);
        pair.Children.Add(amountFrame);
        Gap(body, pair);

        var (affFrame, aff) = Kit.LabeledField("שותף (לא חובה)", "DAVID ARIEL", 15);
        var why = Kit.T("", 13.5, Tone.TextSoft, wrap: true);
        var tierText = Kit.T("—", 12.5, Tone.Muted);
        var bonus = Kit.Num("₪0", 34, Tone.Text, FontWeights.Light);
        var save = Kit.Pill("שמור עסקה", PillKind.Primary, () => Save(), height: 48, fontSize: 15);

        void Update()
        {
            var amt = ParseAmount(amount.Text);
            var deal = new Deal { Amount = amt, Region = region, Source = source };
            var tb = target is int t && t > 0 && count + 1 >= t ? rules.TargetBonusPerDealIls : 0;
            var total = amt > 0 ? rules.FtdBonus(deal) + tb : 0;
            bonus.Text = "₪" + He.N(total);
            var tier = region == DealRegion.Pro ? deal.TierLabel : "ישראל";
            tierText.Text = amt > 0 ? tier : "—";
            tierText.Foreground = amt > 0 && region == DealRegion.Pro ? Tone.Tier(tier) : Tone.Muted;
            why.Text = amt > 0
                ? (region == DealRegion.Pro ? $"פרו · {tier}" : "ישראל") + $" · {SalesLabels.Source(source)}" + (tb > 0 ? $" · כולל ₪{tb} בונוס יעד" : "")
                : "הכנס סכום, והדרגה והבונוס יחושבו לבד";
            save.Opacity = amt > 0 ? 1 : 0.35;
        }

        Gap(body, Caption("חשבון", Kit.Segmented(new[] { "פרו", "ישראל" }, 0, i => { region = i == 0 ? DealRegion.Pro : DealRegion.Israel; Update(); }, stretch: true, height: 34, fontSize: 14)));
        var sources = new[] { DealSource.Affiliate, DealSource.PPC, DealSource.Organic, DealSource.Referral };
        Gap(body, Caption("מקור", Kit.Segmented(sources.Select(SalesLabels.Source).ToList(), 0, i => { source = sources[i]; Update(); }, stretch: true, height: 34)));
        Gap(body, affFrame);

        var preview = new Border
        {
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(20, 18, 20, 18),
            Background = Tone.B("#FF0F1014"),
            BorderBrush = Tone.AccentLine,
            BorderThickness = new Thickness(1),
        };
        var left = new StackPanel();
        left.Children.Add(Kit.T("Palon חישב", 12, Tone.Muted));
        why.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(why);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(999), BorderBrush = Tone.B("#1FFFFFFF"), BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 4, 12, 4), Child = tierText, Margin = new Thickness(18, 0, 18, 0),
        };
        preview.Child = Kit.Bar(left, chip, bonus);
        Gap(body, preview);
        Gap(body, save);

        amount.TextChanged += (_, _) => Update();
        amount.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        Update();

        void Save()
        {
            var amt = ParseAmount(amount.Text);
            if (amt <= 0)
            {
                amount.Focus();
                return;
            }
            var book = SalesStore.Get(y, m) ?? new MonthBook { Year = y, Month = m };
            var date = y == now.Year && m == now.Month ? now.Date : new DateTime(y, m, DateTime.DaysInMonth(y, m));
            var deal = new Deal
            {
                ClientName = name.Text.Trim().Length > 0 ? name.Text.Trim() : "לקוח חדש",
                Amount = amt,
                Region = region,
                Source = source,
                Affiliate = aff.Text.Trim().Length > 0 ? aff.Text.Trim() : null,
                Date = date,
                CreatedFrom = DealOrigin.Manual,
            };
            book.Deals.Add(deal);
            if (!SalesStore.SaveMonth(book))
            {
                host.Toast("לא הצלחתי לשמור — הקובץ נעול או פגום");
                return;
            }
            var n = book.Deals.Count;
            var tb = SalesStats.TargetBonus(n, book.Target, rules) - SalesStats.TargetBonus(n - 1, book.Target, rules);
            close();
            host.Toast($"נוספה עסקה · {n}{(book.Target is int t ? $"/{t}" : "")} · בונוס ₪{He.N(rules.FtdBonus(deal) + tb)}");
            host.Cheer();
        }

        close = host.ShowSheet(body, 580);
        name.Focus();
    }

    // ---- new month ------------------------------------------------------------------

    public static void NewMonth(TerminalWindow host, int year, int month)
    {
        var existing = SalesStore.Get(year, month);
        var calendarDays = WorkCalendar.WorkDaysInMonth(year, month);
        var days = existing?.WorkDays ?? calendarDays;
        Action close = () => { };

        var prev = new DateTime(year, month, 1).AddMonths(-1);
        var prevBook = SalesStore.Get(prev.Year, prev.Month);
        var editing = existing is not null;
        var body = Body();
        Gap(body, Head(editing ? $"היעד ל{He.Month(month)}" : $"פתיחת {He.Month(month)} {year}",
            editing ? null : prevBook is not null ? $"{He.Month(prev.Month)} נשמר בארכיון עם כל הנתונים" : null, () => close()));

        var (targetHost, target) = Kit.Input("לדוגמה 55", 34, ltr: false);
        target.FontWeight = FontWeights.Light;
        if (existing?.Target is int t) target.Text = t.ToString(CultureInfo.InvariantCulture);
        var targetCol = new StackPanel();
        targetCol.Children.Add(Kit.T("היעד שקיבלת מהחברה (חשבונות)", 12.5, Tone.Muted));
        targetHost.Margin = new Thickness(0, 6, 0, 0);
        targetCol.Children.Add(targetHost);
        var targetFrame = new Border
        {
            CornerRadius = new CornerRadius(20), Background = Tone.FillSoft, BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 14), Child = targetCol, Cursor = Cursors.IBeam,
        };
        targetFrame.MouseLeftButtonDown += (_, _) => target.Focus();
        target.GotKeyboardFocus += (_, _) => targetFrame.BorderBrush = Tone.AccentLine;
        target.LostKeyboardFocus += (_, _) => targetFrame.BorderBrush = Tone.Hairline;
        Gap(body, targetFrame);

        var holidays = WorkCalendar.WorkDates(year, month).Count() < WorkdayCount(year, month)
            ? Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Select(dd => new DateTime(year, month, dd))
                .Where(dd => WorkCalendar.IsWorkWeekday(dd.DayOfWeek) && WorkCalendar.HolidayName(dd) is not null)
                .Select(dd => HolidayHe(WorkCalendar.HolidayName(dd)!)).Distinct().ToList()
            : new List<string>();
        var daysText = Kit.Num(days.ToString(), 24, Tone.Text);
        daysText.MinWidth = 34;
        daysText.TextAlignment = TextAlignment.Center;
        daysText.VerticalAlignment = VerticalAlignment.Center;
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Kit.T("ימי עבודה", 14.5, Tone.Text, FontWeights.Medium));
        info.Children.Add(Kit.T(holidays.Count == 0 ? $"ראשון עד חמישי. ב{He.Month(month)} אין חגים באמצע השבוע." : $"ראשון עד חמישי, בלי {string.Join(", ", holidays)}.", 12.5, Tone.MutedSoft, wrap: true));
        var down = Kit.IconButton(Icons.Minus, 34, () => { days = Math.Max(1, days - 1); daysText.Text = days.ToString(); }, PillKind.Secondary, 15, Tone.Text);
        var up = Kit.IconButton(Icons.Plus, 34, () => { days = Math.Min(23, days + 1); daysText.Text = days.ToString(); }, PillKind.Secondary, 15, Tone.Text);
        var stepper = new StackPanel { Orientation = Orientation.Horizontal };
        stepper.Children.Add(down);
        daysText.Margin = new Thickness(8, 0, 8, 0);
        stepper.Children.Add(daysText);
        stepper.Children.Add(up);
        var daysRow = new Border
        {
            CornerRadius = new CornerRadius(20), Background = Tone.FillSoft, Padding = new Thickness(18, 14, 18, 14),
            Child = Kit.Bar(info, stepper),
        };
        Gap(body, daysRow);

        var note = new StackPanel { Orientation = Orientation.Horizontal };
        note.Children.Add(Kit.Icon(Icons.Bell, 16, Tone.Accent));
        var noteText = Kit.T("Palon יזכיר לך ב-1 בכל חודש לעדכן את היעד.", 13, Tone.MutedSoft);
        noteText.Margin = new Thickness(10, 0, 0, 0);
        note.Children.Add(noteText);
        Gap(body, note);

        void Save()
        {
            int? targetValue = int.TryParse(new string(target.Text.Where(char.IsDigit).ToArray()), out var tv) && tv > 0 ? tv : null;
            var book = SalesStore.Get(year, month) ?? new MonthBook { Year = year, Month = month };
            book.Target = targetValue;
            book.WorkDaysOverride = days == calendarDays ? null : days;
            if (!SalesStore.SaveMonth(book))
            {
                host.Toast("לא הצלחתי לשמור — הקובץ נעול או פגום");
                return;
            }
            if (!editing && prevBook is { Archived: false })
            {
                prevBook.Archived = true;
                SalesStore.SaveMonth(prevBook);
            }
            close();
            host.Toast(editing ? $"היעד עודכן · {targetValue?.ToString() ?? "בלי יעד"}"
                : targetValue is int v ? $"{He.Month(month)} נפתח · יעד {v} · {days} ימי עבודה" : $"{He.Month(month)} נפתח · אפשר להוסיף יעד אחר כך");
        }
        target.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        Gap(body, Kit.Pill(editing ? "שמור" : $"פתח את {He.Month(month)}", PillKind.Primary, Save, height: 48, fontSize: 15));
        close = host.ShowSheet(body, 520);
        target.Focus();
    }

    static int WorkdayCount(int year, int month) =>
        Enumerable.Range(1, DateTime.DaysInMonth(year, month)).Count(d => WorkCalendar.IsWorkWeekday(new DateTime(year, month, d).DayOfWeek));

    static string HolidayHe(string name) => name switch
    {
        "Rosh Hashana" => "ראש השנה",
        "Yom Kippur" => "יום כיפור",
        "Sukkot" => "סוכות",
        "Shemini Atzeret" => "שמחת תורה",
        "Pesach" => "פסח",
        "Shavuot" => "שבועות",
        "Independence Day" => "יום העצמאות",
        _ => name,
    };

    // ---- approvals -------------------------------------------------------------------

    public static void Approvals(TerminalWindow host, int year, int month)
    {
        var book = SalesStore.Get(year, month);
        if (book is null) return;
        var rules = SalesStore.LoadRules();
        Action close = () => { };
        var sub = Kit.T("", 13.5, Tone.MutedSoft);
        var fill = new Border { Background = Tone.Green, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Tone.Track, Child = fill, Margin = new Thickness(8, 0, 8, 0) };
        var switches = new Dictionary<string, PillSwitch>();

        void Update(bool animate)
        {
            var s = SalesStats.Compute(book, rules, DateTime.Now);
            sub.Text = $"{s.ApprovedCount} מתוך {s.Count} אושרו · שכר מאושר ₪{He.N(s.ConfirmedPayIls)}";
            var w = s.Count == 0 ? 0 : (double)s.ApprovedCount / s.Count * Math.Max(0, track.ActualWidth);
            if (animate) fill.BeginAnimation(FrameworkElement.WidthProperty, Feel.To(w, 500));
            else fill.Width = w;
        }
        track.SizeChanged += (_, _) => Update(false);

        void Toggle(string id, bool on)
        {
            if (book.Deals.FirstOrDefault(x => x.Id == id) is not { } deal) return;
            deal.Approved = on;
            SalesStore.SaveMonth(book);
            Update(true);
        }

        var all = Kit.Pill("סמן הכל", PillKind.Secondary, () =>
        {
            foreach (var d in book.Deals) d.Approved = true;
            SalesStore.SaveMonth(book);
            foreach (var sw in switches.Values) sw.Set(true, animate: true);
            Update(true);
            host.Toast($"כל {book.Deals.Count} העסקאות סומנו כמאושרות");
            host.Cheer();
        }, height: 34, fontSize: 13);

        var ordered = SalesStats.Ordered(book.Deals);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = Head("בדיקת סוף חודש", null, () => close(), all);
        ((StackPanel)head.Children[0]).Children.Add(sub);
        head.Margin = new Thickness(8, 0, 0, 16);
        body.Children.Add(head);
        Grid.SetRow(track, 1);
        body.Children.Add(track);

        var list = new VirtualList(o =>
        {
            var (deal, index) = ((Deal, int))o;
            var tb = book.Target is int t && t > 0 && index >= t - 1 ? rules.TargetBonusPerDealIls : 0;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Kit.Auto(Kit.T(deal.ClientName, 14.5, Tone.Text, FontWeights.Medium)));
            text.Children.Add(Kit.T($"{He.DayMonth(deal.Date)} · {(deal.Region == DealRegion.Pro ? $"פרו · {deal.TierLabel}" : "ישראל")} · ${He.N(deal.Amount)}", 12.5, Tone.Muted));
            var sw = new PillSwitch(deal.Approved) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            sw.Toggled += on => Toggle(deal.Id, on);
            switches[deal.Id] = sw;
            var bonus = Kit.Num("₪" + He.N(rules.FtdBonus(deal) + tb), 13.5, Tone.MutedSoft);
            bonus.VerticalAlignment = VerticalAlignment.Center;
            var row = Kit.ListRow(Kit.Bar(text, bonus, sw), new Thickness(12, 10, 12, 10));
            row.CornerRadius = new CornerRadius(14);
            return row;
        })
        {
            ItemsSource = ordered.Select((d, i) => (object)(d, i)).ToList(),
            Margin = new Thickness(0, 16, 0, 0),
        };
        Grid.SetRow(list, 2);
        body.Children.Add(list);
        close = host.ShowSheet(body, 600, 780);
        Update(false);
    }

    // ---- import ----------------------------------------------------------------------

    public static void Import(TerminalWindow host, int year, int month)
    {
        var y = year;
        var m = month;
        Action close = () => { };
        ImportResult? result = null;
        string? file = null;

        var body = Body();
        Gap(body, Head("ייבוא מאקסל", "הגיליון החודשי: שם, תאריך, ישראל/פרו, CLUB, מקור, סכום, אושר, הערה.", () => close()));

        var summary = Kit.T("", 14, Tone.Text, wrap: true);
        var warnings = Kit.T("", 12.5, Tone.AmberText, wrap: true);
        var previewBox = new Border
        {
            CornerRadius = new CornerRadius(18), Background = Tone.FillSoft, Padding = new Thickness(18, 14, 18, 14),
            Child = new StackPanel { Children = { summary, warnings } }, Visibility = Visibility.Collapsed,
        };
        warnings.Margin = new Thickness(0, 6, 0, 0);

        var replace = Kit.Pill("החלף את החודש", PillKind.Primary, () => Apply(replaceAll: true), height: 46, fontSize: 14.5);
        var append = Kit.Pill("הוסף לחודש", PillKind.Secondary, () => Apply(replaceAll: false), height: 46, fontSize: 14.5);
        append.Margin = new Thickness(10, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0.35, IsHitTestVisible = false };
        buttons.Children.Add(replace);
        buttons.Children.Add(append);
        var monthText = Kit.T("", 17, Tone.Text, FontWeights.Medium);
        monthText.VerticalAlignment = VerticalAlignment.Center;
        monthText.MinWidth = 150;
        monthText.TextAlignment = TextAlignment.Center;
        void ShowMonth() => monthText.Text = $"{He.Month(m)} {y}";
        var prevBtn = Kit.IconButton("M9,6 L15,12 L9,18", 34, () => { var d = new DateTime(y, m, 1).AddMonths(-1); (y, m) = (d.Year, d.Month); ShowMonth(); Preview(); }, PillKind.Secondary, 15, Tone.Text);
        var nextBtn = Kit.IconButton("M15,6 L9,12 L15,18", 34, () => { var d = new DateTime(y, m, 1).AddMonths(1); (y, m) = (d.Year, d.Month); ShowMonth(); Preview(); }, PillKind.Secondary, 15, Tone.Text);
        var picker = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        picker.Children.Add(prevBtn);
        picker.Children.Add(monthText);
        picker.Children.Add(nextBtn);
        Gap(body, Caption("לאיזה חודש", picker));
        ShowMonth();

        var fileName = Kit.T("עוד לא נבחר קובץ", 13.5, Tone.MutedSoft);
        fileName.VerticalAlignment = VerticalAlignment.Center;
        var pick = Kit.Pill("בחר קובץ", PillKind.Secondary, () =>
        {
            var dialog = new OpenFileDialog { Filter = "גיליון (*.xlsx;*.csv)|*.xlsx;*.csv", Title = "ייבוא עסקאות" };
            if (dialog.ShowDialog(host) != true) return;
            file = dialog.FileName;
            fileName.Text = System.IO.Path.GetFileName(file);
            Preview();
        });
        Gap(body, Kit.Bar(fileName, pick));

        Gap(body, previewBox);
        Gap(body, buttons);

        void Preview()
        {
            if (file is null) return;
            try
            {
                result = SalesImport.FromFile(file, y, m);
            }
            catch (Exception ex)
            {
                Log.Write($"Import preview failed: {ex.Message}");
                result = null;
            }
            previewBox.Visibility = Visibility.Visible;
            if (result is null || result.Deals.Count == 0)
            {
                summary.Text = "לא מצאתי עסקאות בקובץ הזה.";
                warnings.Text = "";
                buttons.Opacity = 0.35;
                buttons.IsHitTestVisible = false;
                return;
            }
            var existing = SalesStore.Get(y, m)?.Deals.Count ?? 0;
            summary.Text = $"{result.Deals.Count} עסקאות · ${He.N(result.Deals.Sum(d => d.Amount))}" + (existing > 0 ? $" · בחודש כבר יש {existing}" : "");
            warnings.Text = result.Warnings.Count == 0 ? "" : string.Join("\n", result.Warnings.Take(3)) + (result.Warnings.Count > 3 ? $"\n+{result.Warnings.Count - 3} נוספות" : "");
            buttons.Opacity = 1;
            buttons.IsHitTestVisible = true;
        }

        void Apply(bool replaceAll)
        {
            if (result is null || result.Deals.Count == 0) return;
            var book = SalesStore.Get(y, m) ?? new MonthBook { Year = y, Month = m };
            if (replaceAll) book.Deals.Clear();
            book.Deals.AddRange(result.Deals);
            if (!SalesStore.SaveMonth(book))
            {
                host.Toast("לא הצלחתי לשמור — הקובץ נעול או פגום");
                return;
            }
            close();
            host.Toast($"יובאו {result.Deals.Count} עסקאות ל{He.Month(m)}");
        }

        close = host.ShowSheet(body, 560);
    }

    // ---- previous months ---------------------------------------------------------------

    public static void Months(TerminalWindow host, Action<int, int> view)
    {
        Action close = () => { };
        var rules = SalesStore.LoadRules();
        var months = SalesStore.ListMonths();
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var imp = Kit.Pill("ייבוא מאקסל", PillKind.Secondary, () => Import(host, DateTime.Now.Year, DateTime.Now.Month), height: 34, fontSize: 13);
        var head = Head("חודשים", months.Count == 0 ? "עוד אין חודשים שמורים." : null, () => close(), imp);
        head.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(head);
        var list = new VirtualList(o =>
        {
            var book = (MonthBook)o;
            var s = SalesStats.Compute(book, rules, DateTime.Now);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Kit.T($"{He.Month(book.Month)} {book.Year}", Display.Body, Tone.Text, FontWeights.Medium));
            text.Children.Add(Kit.T($"{s.Count}{(s.Target is int t ? $"/{t}" : "")} הפקדות · ${He.N(s.SumDeposits)}", 12.5, Tone.Muted));
            var pay = Kit.Num("₪" + He.N(s.ExpectedPayIls), 14, Tone.TextSoft);
            var row = Kit.ListRow(Kit.Bar(text, pay), new Thickness(14, 12, 14, 12));
            row.Cursor = Cursors.Hand;
            Kit.Clickable(row, () =>
            {
                close();
                view(book.Year, book.Month);
            });
            return row;
        })
        { ItemsSource = months };
        Grid.SetRow(list, 1);
        body.Children.Add(list);
        close = host.ShowSheet(body, 520, 640);
    }

    // ---- export ----------------------------------------------------------------------

    public static void Export(TerminalWindow host, MonthBook book, BonusRules rules)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"Palon-{book.Key}.csv",
            Filter = "CSV (Excel)|*.csv",
            Title = "ייצוא לאקסל",
        };
        if (dialog.ShowDialog(host) != true) return;
        host.Toast(SalesImport.ExportCsv(book, rules, dialog.FileName)
            ? $"{He.Month(book.Month)} נשמר · {System.IO.Path.GetFileName(dialog.FileName)}"
            : "הייצוא נכשל — ראה לוג");
    }
}
