using System.Windows;
using System.Windows.Controls;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Calls (a side sheet): the recent calls with one-tap Salesforce summary
/// copy, a fitting template and the Salesforce log — plus the month-start
/// card asking for the new target when there isn't one yet. What Today's
/// lower half used to hold before Now took over the main screen.
/// </summary>
sealed class CallsScreen : TerminalScreen
{
    public CallsScreen(TerminalWindow host) : base(host)
    {
    }

    public override string Title => "שיחות";

    public override void Render(TermData d, bool entrance)
    {
        Children.Clear();
        var stack = Stack();
        Add(stack, Header("שיחות", "הסיכומים נכתבים לבד — העתק, תבנית או Salesforce בלחיצה", out _));
        if (SalesStats.ShouldRemindToSetTarget(d.Book, d.Now)) Add(stack, TargetReminder(d));
        Add(stack, RecentCalls(d));
        Children.Add(stack);
        if (entrance) Kit.Rise(stack.Children.OfType<FrameworkElement>());
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

    Border RecentCalls(TermData d)
    {
        var card = Kit.Glass(padding: new Thickness(14, 22, 14, 12));
        var col = new StackPanel();
        var head = Kit.Bar(Kit.SectionTitle("שיחות אחרונות"), Kit.T("הסיכומים נכתבים לבד", Display.Tiny, Tone.Muted));
        head.Margin = new Thickness(12, 0, 12, 10);
        col.Children.Add(head);

        var templates = TemplatesStore.Load();
        var notes = d.Notes.OrderByDescending(n => n.StartedUtc).Take(20).ToList();
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
            {
                var filled = TemplateFill.Fill(tpl, first);
                Host.CopyWithToast(filled, first.Length > 0 ? $"{tpl.Title} הועתקה עם השם {first}" : $"{tpl.Title} הועתקה");
                TemplateLearningStore.LogCopy(tpl, who, note.Number, filled, null, "calls");
            }, height: 30, fontSize: 12.5);
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
