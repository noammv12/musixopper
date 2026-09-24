using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palon.Notes;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// The callback row shared by Today's "Next up" and the Callbacks screen:
/// a check that fills green with a popping tick, the name struck through,
/// the row dimming — then the store write, with Undo on the toast. Done
/// rows stay visible (dimmed) until the page is left, so a mis-click is
/// one tap to reverse right where it happened.
/// </summary>
static class CallbackRows
{
    /// <summary>Callbacks checked off this session — still shown, dimmed.</summary>
    public static readonly HashSet<string> RecentlyDone = new();

    /// <summary>Active callbacks plus the ones just checked off, due-ordered.</summary>
    public static List<Callback> Visible(IEnumerable<Callback> all) =>
        all.Where(c => c.IsActive || (c.Status == CallbackStatus.Done && RecentlyDone.Contains(c.Id)))
           .OrderBy(c => c.DueAtUtc).ToList();

    public static Border Build(TerminalWindow host, Callback cb, DateTime now, bool full, IReadOnlyList<CallNote>? notes = null)
    {
        var done = cb.Status == CallbackStatus.Done;
        var due = cb.DueAtUtc.ToLocalTime();
        var overdue = !done && due < now;

        var check = new CheckDot(done, full ? 28 : 26);
        var name = Kit.Auto(Kit.T(cb.DisplayLabel, full ? 15.5 : Display.Body, Tone.Text, full ? FontWeights.SemiBold : FontWeights.Medium));
        name.TextDecorations = done ? Strike : null;
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(name);

        // "Palon heard: '…'" — the words from the call that made this callback.
        if (full && cb.Source == CallbackSource.AutoCall && notes?.FirstOrDefault(n => n.Id == cb.CallNoteId)?.ProposedCallback is { Phrase.Length: > 0 } heard)
        {
            var chip = new Border
            {
                Background = Tone.AccentSoft, CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 2),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = Kit.T($"שמעתי: ״{heard.Phrase}״", 12, Tone.Accent),
            };
            nameRow.Children.Add(chip);
        }

        var noteText = cb.Name is not null || cb.HasPhone ? FirstLine(cb.Note) : "";
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
        text.Children.Add(nameRow);
        if (noteText.Length > 0)
        {
            var note = Kit.T(noteText, full ? 13.5 : Display.Tiny, Tone.Muted);
            note.Margin = new Thickness(0, full ? 3 : 1, 0, 0);
            text.Children.Add(note);
        }

        var when = Kit.Mono(He.When(due, now), full ? 13 : 12.5, overdue ? Tone.RedText : Tone.MutedSoft);
        when.VerticalAlignment = VerticalAlignment.Center;
        when.MinWidth = full ? 86 : 0;
        when.TextAlignment = TextAlignment.Left;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(check);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var row = Kit.ListRow(g, new Thickness(12, full ? 12 : 11, 12, full ? 12 : 11));
        row.Opacity = done ? 0.42 : 1;

        if (full && !done)
        {
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            if (cb.HasPhone)
            {
                var phone = Kit.Pill("", PillKind.Secondary, () => host.CopyWithToast(cb.Phone!, $"המספר של {cb.DisplayLabel} הועתק"),
                    height: 32, content: Kit.Mono(cb.Phone!, 12.5, Tone.Text));
                tools.Children.Add(phone);
            }
            var snooze = Kit.Pill("עוד שעה", PillKind.Secondary, () =>
            {
                var before = cb;
                CallbackStore.Mutate(cb.Id, c => CallbackPlanner.Snooze(c, SnoozeKind.OneHour, DateTime.Now));
                host.Toast($"נדחה בשעה · {cb.DisplayLabel}", "בטל", () =>
                {
                    CallbackStore.Update(before);
                });
            }, height: 32, fontSize: 12.5);
            snooze.Margin = new Thickness(tools.Children.Count > 0 ? 6 : 0, 0, 0, 0);
            tools.Children.Add(snooze);
            Grid.SetColumn(tools, 2);
            g.Children.Add(tools);
            Kit.Reveal(row, tools);
        }
        Grid.SetColumn(when, 3);
        g.Children.Add(when);

        check.Toggled += on =>
        {
            name.TextDecorations = on ? Strike : null;
            row.BeginAnimation(UIElement.OpacityProperty, Feel.To(on ? 0.42 : 1, 400));
            when.Foreground = on ? Tone.MutedSoft : overdue ? Tone.RedText : Tone.MutedSoft;
            if (on)
            {
                RecentlyDone.Add(cb.Id);
                host.Quietly(() => CallbackStore.Mutate(cb.Id, c => CallbackPlanner.MarkDone(c, DateTime.UtcNow)));
                host.Cheer();
                host.Toast($"בוצע · {cb.DisplayLabel}", "בטל", () =>
                {
                    RecentlyDone.Remove(cb.Id);
                    CallbackStore.Mutate(cb.Id, CallbackPlanner.Reopen);
                });
            }
            else
            {
                RecentlyDone.Remove(cb.Id);
                host.Quietly(() => CallbackStore.Mutate(cb.Id, CallbackPlanner.Reopen));
            }
        };
        return row;
    }

    static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }

    static readonly TextDecorationCollection Strike = MakeStrike();

    static TextDecorationCollection MakeStrike()
    {
        var d = new TextDecoration(TextDecorationLocation.Strikethrough, new Pen(Tone.B("#66FFFFFF"), 1), 0,
            TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended);
        var c = new TextDecorationCollection { d };
        c.Freeze();
        return c;
    }
}
