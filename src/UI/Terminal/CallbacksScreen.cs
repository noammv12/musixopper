using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Callbacks: type who to call back, tap a time. Filter by overdue / today
/// / tomorrow; each group is a glass card of rows with check-off, and on
/// hover the number to copy and a one-hour snooze. Promises Palon heard on
/// calls wait on top for a ✓ or ✕.
/// </summary>
sealed class CallbacksScreen : TerminalScreen
{
    static readonly string[] Filters = { "הכל", "באיחור", "היום", "מחר" };

    readonly StackPanel _page = Stack();
    readonly StackPanel _body = Stack();
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBox _quick;
    readonly TextBlock _sub;
    readonly FrameworkElement _header;
    readonly Border _add;
    readonly Border _filter;
    int _filterIndex;

    public CallbacksScreen(TerminalWindow host) : base(host)
    {
        MaxWidth = 860;
        _header = Header("חזרות", "", out _sub);
        Add(_page, _header);

        var (frame, box) = Kit.Field("למי לחזור? שם, ואז זמן", 17, 30, 0, leading: Kit.Icon(Icons.Plus, 20, Tone.Accent), background: System.Windows.Media.Brushes.Transparent);
        frame.BorderThickness = new Thickness(0);
        frame.Padding = new Thickness(0);
        _quick = box;
        _quick.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddFromText();
        };
        var addCol = new StackPanel();
        addCol.Children.Add(frame);
        addCol.Children.Add(_chips);
        _add = Kit.Glass(26, new Thickness(18, 16, 18, 16));
        _add.Child = addCol;
        _quick.GotKeyboardFocus += (_, _) => _add.BorderBrush = Tone.AccentLine;
        _quick.LostKeyboardFocus += (_, _) => _add.BorderBrush = Tone.GlassRim;
        Add(_page, _add);

        _filter = Kit.Segmented(Filters, 0, i =>
        {
            _filterIndex = i;
            host.Refresh();
        });
        Add(_page, _filter);
        Add(_page, _body);
        Children.Add(_page);
    }

    public override string Title => "חזרות";

    public override void Render(TermData d, bool entrance)
    {
        var counts = d.Counts;
        _sub.Text = counts.Badge == 0 ? "אין חזרות פתוחות להיום"
            : $"{counts.Badge} פתוחות להיום" + (counts.Overdue > 0 ? $" · {counts.Overdue} באיחור" : "");

        RenderChips(d.Now);
        _body.Children.Clear();
        var cards = new List<FrameworkElement>();

        var proposals = d.Notes.Where(n => n.ProposedCallback is { IsPending: true }).OrderByDescending(n => n.StartedUtc).ToList();
        if (proposals.Count > 0 && _filterIndex == 0)
        {
            var p = Proposals(proposals, d);
            Add(_body, p);
            cards.Add(p);
        }

        var visible = CallbackRows.Visible(d.Callbacks);
        var groups = visible
            .GroupBy(c => c.Status == CallbackStatus.Done
                ? CallbackPlanner.GroupOf(c.DueAtUtc.ToLocalTime() < d.Now ? d.Now : c.DueAtUtc.ToLocalTime(), d.Now)
                : CallbackPlanner.GroupOf(c.DueAtUtc.ToLocalTime(), d.Now))
            .OrderBy(g => g.Key)
            .Where(g => _filterIndex switch
            {
                1 => g.Key == CallbackGroup.Overdue,
                2 => g.Key == CallbackGroup.Today,
                3 => g.Key == CallbackGroup.Tomorrow,
                _ => true,
            }).ToList();

        if (groups.Count == 0)
        {
            var empty = Kit.T(_filterIndex == 0 ? "אין חזרות פתוחות. כתוב שם למעלה ובחר זמן." : "אין כאן כלום.", Display.Body, Tone.Muted);
            empty.Margin = new Thickness(4, 8, 0, 0);
            Add(_body, empty);
        }
        foreach (var g in groups)
        {
            var card = Kit.Glass(Display.ListRadius, new Thickness(12, 18, 12, 10));
            var col = new StackPanel();
            var title = Kit.T(GroupTitle(g.Key), Display.Body, g.Key == CallbackGroup.Overdue ? Tone.RedText : Tone.Text, FontWeights.SemiBold);
            var n = Kit.Num(g.Count(c => c.IsActive).ToString(), Display.Meta, Tone.Muted);
            n.Margin = new Thickness(10, 0, 0, 0);
            n.VerticalAlignment = VerticalAlignment.Center;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
            head.Children.Add(title);
            head.Children.Add(n);
            col.Children.Add(head);
            foreach (var cb in g) col.Children.Add(CallbackRows.Build(Host, cb, d.Now, full: true, d.Notes));
            card.Child = col;
            Add(_body, card);
            cards.Add(card);
        }

        if (entrance)
        {
            var all = new List<FrameworkElement> { _header, _add, _filter };
            all.AddRange(cards);
            Kit.Rise(all);
        }
    }

    static string GroupTitle(CallbackGroup g) => g switch
    {
        CallbackGroup.Overdue => "באיחור",
        CallbackGroup.Today => "היום",
        CallbackGroup.Tomorrow => "מחר",
        CallbackGroup.LaterThisWeek => "בהמשך השבוע",
        _ => "בהמשך",
    };

    void RenderChips(DateTime now)
    {
        _chips.Children.Clear();
        foreach (var pick in CallbackPlanner.QuickPicks(now))
        {
            var label = pick.Key switch
            {
                "in1h" => "בעוד שעה",
                "tonight" => "הערב",
                "tomorrow" => pick.DueLocal.Date == now.Date.AddDays(1) ? "מחר" : He.Day(pick.DueLocal.DayOfWeek),
                "sunday" => "ראשון",
                _ => pick.Label,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Kit.T(label, 13.5, Tone.Text, FontWeights.Medium));
            var time = Kit.Mono(He.Clock(pick.DueLocal), 12, Tone.MutedSoft);
            time.Margin = new Thickness(8, 0, 0, 0);
            time.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(time);
            var chip = Kit.Pill("", PillKind.Secondary, () => AddAt(pick.DueLocal), height: 36, content: content);
            chip.Margin = new Thickness(_chips.Children.Count > 0 ? 8 : 0, 0, 0, 0);
            if (!pick.Enabled)
            {
                chip.Opacity = 0.35;
                chip.IsHitTestVisible = false;
            }
            _chips.Children.Add(chip);
        }
    }

    /// <summary>Enter: "דני מחר ב-11" → Palon reads the time out of the text.</summary>
    void AddFromText()
    {
        var text = _quick.Text.Trim();
        if (text.Length == 0) return;
        if (TimePhrase.TryResolve(text, DateTime.Now, out var when, out _) && when > DateTime.Now)
        {
            AddAt(when);
            return;
        }
        Host.Toast("בחר זמן מהכפתורים, או כתוב אותו (מחר ב-11)");
    }

    void AddAt(DateTime dueLocal)
    {
        var text = _quick.Text.Trim();
        var digits = Agent.PhoneMatch.Digits(text);
        var isPhone = digits.Length >= 7 && digits.Length >= text.Count(char.IsLetterOrDigit) - 1;
        string name = text.Length == 0 ? "לקוח חדש" : text;
        var cb = CallbackStore.Add("", dueLocal.ToUniversalTime(),
            name: isPhone ? null : name, phone: isPhone ? text : null);
        if (cb is null)
        {
            Host.Toast("לא הצלחתי לשמור את החזרה");
            return;
        }
        _quick.Text = "";
        Host.Toast($"נקבעה חזרה · {cb.DisplayLabel} · {He.When(dueLocal, DateTime.Now)}", "בטל", () => CallbackStore.Remove(cb.Id));
    }

    Border Proposals(List<CallNote> notes, TermData d)
    {
        var card = Kit.Glass(Display.ListRadius, new Thickness(12, 18, 12, 10));
        card.BorderBrush = Tone.AccentLine;
        var col = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 8) };
        head.Children.Add(Kit.T("Palon שמע בשיחות", Display.Body, Tone.Text, FontWeights.SemiBold));
        col.Children.Add(head);
        foreach (var note in notes)
        {
            var p = note.ProposedCallback!;
            var who = ClientIndex.NameForPhone(d.Callbacks, note.Number) ?? note.Number ?? "שיחה";
            var when = p.WhenUtc.ToLocalTime();
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Kit.Auto(Kit.T(who, 15.5, Tone.Text, FontWeights.SemiBold)));
            var heard = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            heard.Children.Add(Kit.T($"שמעתי: ״{p.Phrase}״ →", 13.5, Tone.Muted));
            var at = Kit.Mono(He.When(when, d.Now), 13, Tone.Accent);
            at.Margin = new Thickness(6, 0, 0, 0);
            heard.Children.Add(at);
            text.Children.Add(heard);
            g.Children.Add(text);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(Kit.IconButton(Icons.Check, 34, () =>
            {
                var cb = CallbackProposals.Accept(note);
                if (cb is null) Host.Toast("לא הצלחתי לשמור את החזרה");
                else
                {
                    Host.Toast($"נקבעה חזרה · {who} · {He.When(when, DateTime.Now)}");
                    Host.Cheer();
                }
            }, PillKind.Secondary, 15, Tone.GreenText));
            var dismiss = Kit.IconButton(Icons.Close, 34, () => CallbackProposals.Dismiss(note), PillKind.Secondary, 14);
            dismiss.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(dismiss);
            Grid.SetColumn(buttons, 1);
            g.Children.Add(buttons);
            col.Children.Add(Kit.ListRow(g, new Thickness(12)));
        }
        card.Child = col;
        return card;
    }
}
