using System.Windows;
using System.Windows.Controls;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Today: Palon's greeting and one-line brief, the target ring, expected
/// pay, what's next up, and the recent calls with one-tap Salesforce
/// summary and template copies. A gentle month-start card asks for the
/// new target when there isn't one yet.
/// </summary>
sealed class TodayScreen : TerminalScreen
{
    readonly PalonAvatar _avatar = new() { Width = 128, Height = 128, Mood = PalonMood.Idle };

    public TodayScreen(TerminalWindow host) : base(host)
    {
    }

    public override string Title => "היום";

    public void Cheer() => _avatar.Cheer();

    public override void Render(TermData d, bool entrance)
    {
        Children.Clear();
        var grid = FourColumns();
        var row = 0;
        var cards = new List<FrameworkElement>();

        if (SalesStats.ShouldRemindToSetTarget(d.Book, d.Now))
        {
            var remind = TargetReminder(d);
            Place(grid, remind, row++, 0, 4);
            cards.Add(remind);
        }

        var hello = Greeting(d);
        var ring = Ring(d);
        var pay = Pay(d);
        Place(grid, hello, row, 0, 2);
        Place(grid, ring, row, 2);
        Place(grid, pay, row++, 3);
        var next = NextUp(d);
        var calls = RecentCalls(d);
        Place(grid, next, row, 0, 2);
        Place(grid, calls, row, 2, 2);
        cards.AddRange(new[] { hello, ring, pay, next, calls });

        Children.Add(grid);
        if (entrance) Kit.Rise(cards);
    }

    Border TargetReminder(TermData d)
    {
        var card = Kit.Glass(Display.ListRadius, new Thickness(22, 16, 18, 16));
        var bell = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Tone.AccentSoft,
            Child = Kit.Icon(Icons.Bell, 18, Tone.Accent),
        };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Kit.T("חודש חדש — מה היעד?", Display.Body, Tone.Text, FontWeights.SemiBold));
        text.Children.Add(Kit.T($"פתח את {He.Month(d.Now.Month)} עם היעד שקיבלת, וPalon יחשב קצב ושכר.", Display.Meta, Tone.Muted));
        var start = new StackPanel { Orientation = Orientation.Horizontal };
        start.Children.Add(bell);
        start.Children.Add(text);
        card.Child = Kit.Bar(start, Kit.Pill("להגדרת יעד", PillKind.Primary, () => Sheets.NewMonth(Host, d.Now.Year, d.Now.Month), height: 38));
        return card;
    }

    Border Greeting(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(30, 28, 30, 28), lift: true);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (_avatar.Parent is Panel old) old.Children.Remove(_avatar);
        _avatar.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_avatar);
        var words = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Kit.T(He.Greeting(d.Now, Settings.RepName), Display.Greeting, Tone.Text, FontWeights.SemiBold));
        var brief = Kit.T(MonthView.Brief(d.Counts, d.Stats), 15.5, Tone.Body, wrap: true);
        brief.LineHeight = 25;
        brief.Margin = new Thickness(0, 8, 0, 0);
        words.Children.Add(brief);
        Grid.SetColumn(words, 1);
        top.Children.Add(words);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Kit.Pill("לחזרות של היום", PillKind.Primary, () => Host.Navigate(TerminalPage.Callbacks), height: 42, fontSize: 14));
        var deal = Kit.Pill("עסקה חדשה", PillKind.Secondary, () => Sheets.NewDeal(Host), height: 42, fontSize: 14);
        deal.Margin = new Thickness(10, 0, 0, 0);
        actions.Children.Add(deal);

        var today = CallStatsStore.Load().Where(c => c.StartedUtc.ToLocalTime().Date == d.Now.Date).ToList();
        var talk = today.Sum(c => c.DurationSec);
        var kpi = Kit.T(today.Count == 0 ? "עוד אין שיחות היום"
            : $"{(today.Count == 1 ? "שיחה אחת" : $"{today.Count} שיחות")} · {talk / 3600}:{talk % 3600 / 60:00} בשיחות", Display.Meta, Tone.Muted);

        var col = new StackPanel();
        col.Children.Add(top);
        var bar = Kit.Bar(actions, kpi);
        bar.Margin = new Thickness(0, 22, 0, 0);
        col.Children.Add(bar);
        card.Child = col;
        return card;
    }

    Border Ring(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(24), lift: true);
        var ring = new RingGauge(150, 11);
        var count = Kit.Num("0", Display.NumL, Tone.Text, FontWeights.Light);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        var of = Kit.T("", 12, Tone.Muted);
        of.HorizontalAlignment = HorizontalAlignment.Center;
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(count);
        center.Children.Add(of);
        var dial = new Grid { Width = 150, Height = 150, HorizontalAlignment = HorizontalAlignment.Center };
        dial.Children.Add(ring);
        dial.Children.Add(center);

        var foot = Kit.T("", 13.5, Tone.Body);
        foot.HorizontalAlignment = HorizontalAlignment.Center;
        foot.Margin = new Thickness(0, 12, 0, 0);

        var s = d.Stats;
        var n = s?.Count ?? 0;
        Kit.CountUp(count, n, v => He.N(v));
        if (s?.Target is int t && t > 0)
        {
            of.Text = $"מתוך {t}";
            ring.SweepTo((double)n / t);
            foot.Text = s.Remaining == 0 ? "היעד הושג"
                : s.RequiredPerDay is double need ? $"{He.R1(need)} ביום · נותרו {s.RemainingWorkDays} ימים" : $"נותרו {s.RemainingWorkDays} ימים";
        }
        else
        {
            of.Text = "הפקדות החודש";
            foot.Text = "אין יעד עדיין";
        }
        var col = new StackPanel();
        col.Children.Add(dial);
        col.Children.Add(foot);
        card.Child = col;
        card.Cursor = System.Windows.Input.Cursors.Hand;
        Kit.Clickable(card, () => Host.Navigate(TerminalPage.Month));
        return card;
    }

    Border Pay(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(26), lift: true);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var s = d.Stats;
        var rules = d.Rules;
        var top = new StackPanel();
        top.Children.Add(Kit.T("שכר צפוי", Display.Meta, Tone.Muted));
        var amount = Kit.Num("", Display.NumM, Tone.Text, FontWeights.Light);
        amount.HorizontalAlignment = HorizontalAlignment.Right;
        amount.Margin = new Thickness(0, 8, 0, 0);
        Kit.CountUp(amount, s?.ExpectedPayIls ?? rules.BaseSalaryIls, v => "₪" + He.N(v));
        top.Children.Add(amount);
        var basis = Kit.T($"בסיס {He.N(rules.BaseSalaryIls)} · FTD ₪{He.N(s?.FtdIls ?? 0)}", Display.Meta, Tone.Muted);
        basis.Margin = new Thickness(0, 8, 0, 0);
        top.Children.Add(basis);
        grid.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        if (s?.Target is int t && t > 0)
        {
            bottom.Children.Add(Kit.HBar((double)s.Count / t, Tone.Accent, 200, 6));
            var left = s.Remaining ?? 0;
            var note = Kit.T(left > 0 ? $"עוד {left} עד בונוס היעד" : $"בבונוס יעד: +₪{rules.TargetBonusPerDealIls} לכל הפקדה", Display.Tiny, left > 0 ? Tone.Muted : Tone.GreenText);
            note.Margin = new Thickness(0, 8, 0, 0);
            bottom.Children.Add(note);
        }
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        card.Child = grid;
        return card;
    }

    Border NextUp(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(14, 22, 14, 12));
        var col = new StackPanel();
        var head = Kit.Bar(Kit.SectionTitle("הבא בתור"),
            Kit.Pill("הכל", PillKind.Ghost, () => Host.Navigate(TerminalPage.Callbacks), height: 30, fontSize: 13));
        head.Margin = new Thickness(12, 0, 0, 10);
        col.Children.Add(head);

        var items = CallbackRows.Visible(d.Callbacks)
            .Where(c => c.DueAtUtc.ToLocalTime().Date <= d.Now.Date)
            .Take(5).ToList();
        if (items.Count == 0)
        {
            var more = d.Callbacks.Where(c => c.IsActive).OrderBy(c => c.DueAtUtc).FirstOrDefault();
            col.Children.Add(Empty(more is null ? "אין חזרות פתוחות." : $"אין חזרות להיום. הבאה: {more.DisplayLabel}, {He.When(more.DueAtUtc.ToLocalTime(), d.Now)}."));
        }
        foreach (var cb in items) col.Children.Add(CallbackRows.Build(Host, cb, d.Now, full: false));
        card.Child = col;
        return card;
    }

    Border RecentCalls(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(14, 22, 14, 12));
        var col = new StackPanel();
        var head = Kit.Bar(Kit.SectionTitle("שיחות אחרונות"), Kit.T("הסיכומים נכתבים לבד", Display.Tiny, Tone.Muted));
        head.Margin = new Thickness(12, 0, 12, 10);
        col.Children.Add(head);

        var templates = TemplatesStore.Load();
        var notes = d.Notes.OrderByDescending(n => n.StartedUtc).Take(5).ToList();
        if (notes.Count == 0) col.Children.Add(Empty("שיחות שיוקלטו יופיעו כאן עם סיכום מוכן להדבקה."));
        foreach (var note in notes) col.Children.Add(CallRow(note, d, templates));
        card.Child = col;
        return card;
    }

    FrameworkElement CallRow(CallNote note, TermData d, List<Terminal.MessageTemplate> templates)
    {
        var started = note.StartedUtc.ToLocalTime();
        var who = ClientIndex.NameForPhone(d.Callbacks, note.Number);
        var shown = who ?? note.Number ?? "שיחה";
        var shortCall = note.DurationSec < 60;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Kit.Dot(shortCall ? Tone.AmberText : Tone.GreenText));

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(Kit.Auto(Kit.T(shown, Display.Body, Tone.Text, FontWeights.Medium)));
        var meta = Kit.Mono($"{He.Clock(started)} · {He.Duration(note.DurationSec)}", 12, Tone.Muted);
        meta.Margin = new Thickness(8, 0, 0, 0);
        meta.VerticalAlignment = VerticalAlignment.Center;
        line1.Children.Add(meta);
        text.Children.Add(line1);
        var line = Kit.T(CallSummary.Line(note), Display.Tiny, Tone.Muted);
        line.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(Kit.Auto(line));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Kit.Pill("סיכום", PillKind.Secondary, () =>
            Host.CopyWithToast(CallSummary.Format(note, who, started), "הסיכום הועתק · מוכן להדבקה ב-Salesforce"), height: 30, fontSize: 12.5));
        if (TemplateFill.Suggest(templates, (note.Summary ?? "") + "\n" + note.Transcript) is { } tpl)
        {
            var first = TemplateFill.FirstName(who);
            var t = Kit.Pill("תבנית", PillKind.Accent, () =>
                Host.CopyWithToast(TemplateFill.Fill(tpl, first), first.Length > 0 ? $"{tpl.Title} הועתקה עם השם {first}" : $"{tpl.Title} הועתקה"), height: 30, fontSize: 12.5);
            t.Margin = new Thickness(6, 0, 0, 0);
            t.ToolTip = tpl.Title;
            buttons.Children.Add(t);
        }
        var sf = Kit.Pill("ל-Salesforce", PillKind.Secondary, () => SalesforceSheets.LogCall(Host, note, null), height: 30, fontSize: 12.5);
        sf.Margin = new Thickness(6, 0, 0, 0);
        sf.ToolTip = "תיעוד השיחה ב-Salesforce — עם תצוגה מקדימה ואישור לפני שמירה";
        buttons.Children.Add(sf);
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);
        return Kit.ListRow(g);
    }

    static TextBlock Empty(string text)
    {
        var t = Kit.T(text, 13.5, Tone.Muted, wrap: true);
        t.Margin = new Thickness(12, 6, 12, 12);
        return t;
    }
}
