using System.Windows;
using System.Windows.Controls;
using Palon.Memory;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Memory: what Palon remembers, in two strictly separate cards — "עליך"
/// (the profile that goes into every prompt, with pending suggestions to
/// approve) and "על לקוחות" (facts learned from calls; outdated ones are
/// kept as history, never silently deleted). A pause switch stops all
/// automatic remembering.
/// </summary>
sealed class MemoryScreen : TerminalScreen
{
    readonly FrameworkElement _header;
    readonly StackPanel _profileCol = new();
    readonly StackPanel _clientCol = new();
    readonly Border _profileCard;
    readonly Border _clientCard;
    readonly TextBox _add;
    readonly TextBox _search;
    readonly Border _pause;
    readonly TextBlock _subtitle;
    bool _history;

    public MemoryScreen(TerminalWindow host) : base(host)
    {
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = Header("זיכרון", "", out _subtitle);
        _pause = new Border();
        var changes = Kit.Pill("היסטוריה", PillKind.Secondary, ShowHistory);
        changes.Margin = new Thickness(10, 0, 0, 0);
        _header = Kit.Bar(title, _pause, changes);
        _pause.VerticalAlignment = VerticalAlignment.Bottom;
        changes.VerticalAlignment = VerticalAlignment.Bottom;
        page.Children.Add(_header);

        var (addFrame, add) = Kit.Field("הוסף משהו ש-Palon צריך לזכור עליך", 14.5, 44, 22, background: Tone.FillSoft);
        _add = add;
        _add.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            AddManual();
            e.Handled = true;
        };
        var addButton = Kit.Pill("הוסף", PillKind.Primary, AddManual, height: 44, fontSize: 14);
        addButton.Margin = new Thickness(10, 0, 0, 0);
        var addRow = new Grid { Margin = new Thickness(0, 16, 0, 6) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.Children.Add(addFrame);
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);

        var profileStack = new StackPanel();
        profileStack.Children.Add(CardTitle("עליך", "נכנס לכל שיחה עם Palon. כללי זמנים נאכפים גם בקביעת חזרות."));
        profileStack.Children.Add(addRow);
        profileStack.Children.Add(_profileCol);
        _profileCard = Kit.Glass(padding: new Thickness(28, 26, 28, 26));
        _profileCard.Child = profileStack;
        _profileCard.VerticalAlignment = VerticalAlignment.Top;

        var (searchFrame, search) = Kit.Field("חיפוש לפי לקוח או מילה", 14.5, 44, 22, leading: Kit.Icon(Icons.Search, 17, Tone.Muted), background: Tone.FillSoft);
        _search = search;
        _search.TextChanged += (_, _) => RenderClients();
        searchFrame.Margin = new Thickness(0, 16, 0, 6);
        var historySeg = Kit.Segmented(new[] { "בתוקף", "כולל היסטוריה" }, 0, i =>
        {
            _history = i == 1;
            RenderClients();
        }, height: 30, fontSize: 12.5);
        historySeg.Margin = new Thickness(0, 4, 0, 8);
        var clientStack = new StackPanel();
        clientStack.Children.Add(CardTitle("על לקוחות", "עובדות שעלו בשיחות. מידע שהתיישן נשמר כהיסטוריה."));
        clientStack.Children.Add(searchFrame);
        clientStack.Children.Add(historySeg);
        clientStack.Children.Add(_clientCol);
        _clientCard = Kit.Glass(padding: new Thickness(28, 26, 28, 26));
        _clientCard.Child = clientStack;
        _clientCard.VerticalAlignment = VerticalAlignment.Top;

        var body = new Grid { Margin = new Thickness(0, Display.Gap, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Display.Gap) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_profileCard);
        Grid.SetColumn(_clientCard, 2);
        body.Children.Add(_clientCard);
        Grid.SetRow(body, 1);
        page.Children.Add(body);
        Children.Add(page);
    }

    public override string Title => "זיכרון";

    static StackPanel CardTitle(string title, string hint)
    {
        var s = new StackPanel();
        s.Children.Add(Kit.SectionTitle(title));
        var h = Kit.T(hint, 13, Tone.Muted, wrap: true);
        h.Margin = new Thickness(0, 4, 0, 0);
        s.Children.Add(h);
        return s;
    }

    public override void Render(TermData d, bool entrance)
    {
        var profile = MemoryStore.Profile;
        _subtitle.Text = profile.Paused
            ? "הזיכרון מושהה — Palon לא לומד כלום לבד עד שתחזיר אותו"
            : "מה Palon זוכר — מוצפן ונשאר רק במחשב הזה";
        _pause.Child = Kit.Segmented(new[] { "זוכר", "אל תזכור" }, profile.Paused ? 1 : 0, i =>
        {
            MemoryStore.SetPaused(i == 1);
            Host.Toast(i == 1 ? "הזיכרון הושהה. בקשות מפורשות עדיין נשמרות" : "Palon זוכר שוב");
        }, height: 34, fontSize: 13);
        RenderProfile(profile);
        RenderClients();
        if (entrance) Kit.Rise(new[] { _header, _profileCard, _clientCard });
    }

    // ---- "About you" ----------------------------------------------------------------

    static string SectionName(ProfileSection s) => s switch
    {
        ProfileSection.Rules => "כללים",
        ProfileSection.Preferences => "העדפות",
        ProfileSection.Style => "סגנון עבודה",
        _ => "אנשים",
    };

    static string SourceName(string source) => source switch
    {
        "explicit" => "ביקשת לזכור",
        "correction" => "מתיקון שלך",
        "suggestion" => "הצעה שאישרת",
        _ => "הוספת ידנית",
    };

    void RenderProfile(ProfileData profile)
    {
        _profileCol.Children.Clear();

        if (profile.Suggestions.Count > 0)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(18), Background = Tone.AccentSoft, BorderBrush = Tone.AccentLine,
                BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 8), Margin = new Thickness(0, 10, 0, 4),
            };
            var col = new StackPanel();
            col.Children.Add(Kit.T($"Palon שם לב ל-{profile.Suggestions.Count} דברים. לשמור?", 13.5, Tone.Accent, FontWeights.SemiBold));
            foreach (var s in profile.Suggestions)
            {
                var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                text.Children.Add(Kit.Auto(Kit.T(s.Text, 14.5, Tone.Text, wrap: true)));
                if (s.Quote is { Length: > 0 } q) text.Children.Add(Kit.T($"״{q}״ · {SectionName(s.Section)}", 12, Tone.Muted, wrap: true));
                var id = s.Id;
                var yes = Kit.Pill("שמור", PillKind.Primary, () =>
                {
                    MemoryStore.MutateProfile(p => ProfileBook.Approve(p, id, DateTime.UtcNow));
                    Host.Toast("נשמר בזיכרון");
                }, height: 32, fontSize: 13);
                var no = Kit.Pill("לא", PillKind.Ghost, () => MemoryStore.MutateProfile(p => ProfileBook.Reject(p, id)), height: 32, fontSize: 13);
                no.Margin = new Thickness(6, 0, 0, 0);
                var row = new Grid { Margin = new Thickness(0, 10, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(text);
                var actions = Kit.Row(0, yes, no);
                actions.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                col.Children.Add(row);
            }
            box.Child = col;
            _profileCol.Children.Add(box);
        }

        if (profile.Items.Count == 0)
        {
            var empty = Kit.T("עוד אין כלום. אמור ל-Palon ״תזכור ש…״ או הוסף כאן.", Display.Body, Tone.Muted, wrap: true);
            empty.Margin = new Thickness(0, 14, 0, 0);
            _profileCol.Children.Add(empty);
            return;
        }

        foreach (var section in Enum.GetValues<ProfileSection>())
        {
            var items = profile.Items.Where(i => i.Section == section)
                .OrderByDescending(i => i.Pinned).ThenByDescending(i => i.UpdatedUtc).ToList();
            if (items.Count == 0) continue;
            var head = Kit.T(SectionName(section), 12.5, Tone.Muted, FontWeights.SemiBold);
            head.Margin = new Thickness(12, 16, 0, 2);
            _profileCol.Children.Add(head);
            foreach (var item in items) _profileCol.Children.Add(ProfileRow(item));
        }
    }

    Border ProfileRow(ProfileItem item)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var body = Kit.T(item.Text, 14.5, Tone.Text, wrap: true);
        body.LineHeight = 22;
        text.Children.Add(Kit.Auto(body));
        var meta = $"{SourceName(item.Source)} · {He.DayMonth(item.UpdatedUtc.ToLocalTime())}";
        if (item.Pinned) meta = "מוצמד · " + meta;
        var metaRow = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        metaRow.Children.Add(Kit.T(meta, 12, Tone.Muted));
        if (item.Rule is { } rule)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(999), Background = Tone.FillSoft, Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(8, 0, 0, 0), Child = Kit.T("נאכף · " + rule.Describe(), 11.5, Tone.TextSoft),
            };
            metaRow.Children.Add(badge);
        }
        text.Children.Add(metaRow);

        var id = item.Id;
        var pin = Kit.Pill(item.Pinned ? "בטל הצמדה" : "הצמד", PillKind.Ghost,
            () => MemoryStore.MutateProfile(p => ProfileBook.SetPinned(p, id, !item.Pinned, DateTime.UtcNow)), height: 30, fontSize: 12.5);
        var edit = Kit.IconButton(Icons.Edit, 30, () => EditProfile(item), icon: 14);
        edit.ToolTip = "עריכה";
        var del = Kit.IconButton(Icons.Close, 30, () => DeleteProfile(item), icon: 14);
        del.ToolTip = "מחיקה";
        var actions = Kit.Row(2, pin, edit, del);
        actions.VerticalAlignment = VerticalAlignment.Top;

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(text);
        Grid.SetColumn(actions, 1);
        g.Children.Add(actions);
        var row = Kit.ListRow(g, new Thickness(12, 10, 10, 10));
        Kit.Reveal(row, actions);
        return row;
    }

    void AddManual()
    {
        var text = _add.Text.Trim();
        if (text.Length == 0)
        {
            _add.Focus();
            return;
        }
        var (item, added) = MemoryStore.MutateProfile(p => ProfileBook.Remember(p, text, ProfileSection.Preferences, "manual", "added on the Memory screen", DateTime.UtcNow));
        _add.Text = "";
        Host.Toast(!added ? "זה כבר בזיכרון" : item.Rule is { } r ? $"נשמר כלל: {r.Describe()}" : "נשמר בזיכרון");
    }

    void DeleteProfile(ProfileItem item)
    {
        var change = MemoryStore.MutateProfile(p =>
        {
            ProfileBook.Delete(p, item.Id, "deleted on the Memory screen", DateTime.UtcNow);
            return p.History.LastOrDefault()?.Id;
        });
        if (change is not null) Host.Toast("נמחק מהזיכרון", "בטל", () => MemoryStore.Undo(change));
    }

    /// <summary>The edit sheet: text, section and a reason (kept in the history).</summary>
    public void EditProfile(ProfileItem item)
    {
        Action close = () => { };
        var body = new StackPanel();
        body.Children.Add(Kit.Bar(Kit.T("עריכת זיכרון", Display.Sheet, Tone.Text, FontWeights.SemiBold), Kit.IconButton(Icons.Close, 34, () => close())));
        var (textFrame, text) = Kit.LabeledField("מה לזכור", "", 15, multiline: true, height: 110);
        text.Text = item.Text;
        var sections = Enum.GetValues<ProfileSection>();
        var section = Array.IndexOf(sections, item.Section);
        var seg = Kit.Segmented(sections.Select(SectionName).ToList(), section, i => section = i, stretch: true, height: 34, fontSize: 13.5);
        var (reasonFrame, reason) = Kit.LabeledField("למה (נשמר בהיסטוריה)", "לדוגמה: השתנה המנהל", 14);
        foreach (var el in new FrameworkElement[] { textFrame, seg, reasonFrame })
        {
            el.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(el);
        }
        var save = Kit.Pill("שמור", PillKind.Primary, () =>
        {
            if (text.Text.Trim().Length == 0)
            {
                text.Focus();
                return;
            }
            var why = reason.Text.Trim().Length > 0 ? reason.Text.Trim() : "edited on the Memory screen";
            var change = MemoryStore.MutateProfile(p =>
            {
                ProfileBook.Edit(p, item.Id, text.Text, why, DateTime.UtcNow, sections[section]);
                return p.History.LastOrDefault()?.Id;
            });
            close();
            if (change is not null) Host.Toast("הזיכרון עודכן", "בטל", () => MemoryStore.Undo(change));
        }, height: 44, fontSize: 14.5);
        save.Margin = new Thickness(0, 18, 0, 0);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(save);
        close = Host.ShowSheet(body, 560);
        text.Focus();
        text.CaretIndex = text.Text.Length;
    }

    /// <summary>Opens the editor for a line by id (from the "ערוך" toast action).</summary>
    public void EditById(string id)
    {
        if (MemoryStore.Profile.Items.FirstOrDefault(i => i.Id == id) is { } item) EditProfile(item);
    }

    void ShowHistory()
    {
        Action close = () => { };
        var body = new StackPanel();
        body.Children.Add(Kit.Bar(Kit.T("היסטוריית שינויים", Display.Sheet, Tone.Text, FontWeights.SemiBold), Kit.IconButton(Icons.Close, 34, () => close())));
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var history = MemoryStore.Profile.History.AsEnumerable().Reverse().Take(40).ToList();
        if (history.Count == 0) list.Children.Add(Kit.T("עוד אין שינויים.", Display.Body, Tone.Muted));
        foreach (var c in history)
        {
            var what = c.After?.Text ?? c.Before?.Text ?? "";
            var verb = c.Op switch
            {
                "add" => "נוסף", "edit" => "נערך", "delete" => "נמחק", "pin" => "הוצמד", "unpin" => "בוטלה הצמדה", "undo" => "בוטל", _ => c.Op,
            };
            var col = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            col.Children.Add(Kit.T($"{verb} · {He.DayMonth(c.Utc.ToLocalTime())} {He.Clock(c.Utc.ToLocalTime())} · {c.Reason}", 12, Tone.Muted, wrap: true));
            col.Children.Add(Kit.Auto(Kit.T(what, 14, Tone.Text, wrap: true)));
            var changeId = c.Id;
            var undo = Kit.Pill("בטל", PillKind.Ghost, () =>
            {
                MemoryStore.Undo(changeId);
                close();
                Host.Toast("השינוי בוטל");
            }, height: 30, fontSize: 12.5);
            undo.VerticalAlignment = VerticalAlignment.Top;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(col);
            if (c.Op != "undo")
            {
                Grid.SetColumn(undo, 1);
                g.Children.Add(undo);
            }
            var row = Kit.ListRow(g);
            if (c.Op != "undo") Kit.Reveal(row, undo);
            list.Children.Add(row);
        }
        var scroll = Ui.ChainWheel(Ui.ThinScroll(new ScrollViewer { Content = list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        body.Children.Add(scroll);
        close = Host.ShowSheet(body, 620);
    }

    // ---- client facts ---------------------------------------------------------------

    static string TypeName(string type) => type switch
    {
        "budget" => "תקציב",
        "experience" => "ניסיון",
        "family" => "משפחה",
        "preference" => "העדפה",
        "objection" => "התנגדות",
        "status" => "סטטוס",
        "contact" => "יצירת קשר",
        _ => "כללי",
    };

    void RenderClients()
    {
        _clientCol.Children.Clear();
        var hits = FactBook.Search(MemoryStore.Facts, _search.Text, _history);
        if (hits.Count == 0)
        {
            var empty = Kit.T(string.IsNullOrWhiteSpace(_search.Text)
                ? "עובדות על לקוחות יופיעו כאן אחרי שיחות עם סיכום."
                : "לא נמצא.", Display.Body, Tone.Muted, wrap: true);
            empty.Margin = new Thickness(0, 10, 0, 0);
            _clientCol.Children.Add(empty);
            return;
        }
        foreach (var group in hits.GroupBy(f => f.ClientKey).Take(30))
        {
            var first = group.First();
            var name = group.Select(f => f.ClientName).FirstOrDefault(n => n is not null) ?? first.Phone ?? "לקוח";
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 16, 0, 2) };
            head.Children.Add(Kit.Auto(Kit.T(name, 13.5, Tone.TextSoft, FontWeights.SemiBold)));
            if (first.Phone is { } phone && name != phone)
            {
                var p = Kit.Mono(phone, 12, Tone.Muted);
                p.Margin = new Thickness(8, 0, 0, 0);
                p.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(p);
            }
            _clientCol.Children.Add(head);
            foreach (var f in group.OrderByDescending(f => f.Pinned).ThenByDescending(f => f.IsActive).ThenByDescending(f => f.ValidAt))
                _clientCol.Children.Add(FactRow(f, Host));
        }
    }

    /// <summary>One fact row — shared with the Clients detail card.</summary>
    internal static Border FactRow(ClientFact f, TerminalWindow host)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var body = Kit.T(f.Text, 14.5, f.IsActive ? Tone.Text : Tone.Muted, wrap: true);
        body.LineHeight = 22;
        if (!f.IsActive) body.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(Kit.Auto(body));
        var source = f.Source.StartsWith("call:") ? "משיחה" : "ידני";
        var meta = $"{TypeName(f.Type)} · {source} · מ-{He.DayMonth(f.ValidAt.ToLocalTime())}";
        if (!f.IsActive) meta += f.InvalidAt is { } until ? $" · התיישן ב-{He.DayMonth(until.ToLocalTime())}" : " · תוקן";
        if (f.Pinned) meta = "מוצמד · " + meta;
        text.Children.Add(Kit.T(meta, 12, Tone.Muted));
        if (f.Quote is { Length: > 0 } q)
        {
            var quote = Kit.T($"״{q}״", 12, Tone.Faint, wrap: true);
            quote.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(Kit.Auto(quote));
        }

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(text);
        var id = f.Id;
        var buttons = new List<UIElement>();
        if (f.IsActive)
        {
            buttons.Add(Kit.Pill(f.Pinned ? "בטל הצמדה" : "הצמד", PillKind.Ghost,
                () => MemoryStore.MutateFacts(all => { if (all.FirstOrDefault(x => x.Id == id) is { } x) x.Pinned = !x.Pinned; return 0; }), height: 30, fontSize: 12.5));
            var edit = Kit.IconButton(Icons.Edit, 30, () => EditFact(f, host), icon: 14);
            edit.ToolTip = "תיקון";
            buttons.Add(edit);
            buttons.Add(Kit.Pill("התיישן", PillKind.Ghost, () =>
            {
                MemoryStore.MutateFacts(all => FactBook.Invalidate(all, id, DateTime.UtcNow));
                host.Toast("סומן כלא בתוקף — נשמר בהיסטוריה");
            }, height: 30, fontSize: 12.5));
        }
        var del = Kit.IconButton(Icons.Close, 30, () =>
        {
            var removed = MemoryStore.MutateFacts(all =>
            {
                var index = all.FindIndex(x => x.Id == id);
                if (index < 0) return ((int, ClientFact)?)null;
                var fact = all[index];
                all.RemoveAt(index);
                return (index, fact);
            });
            if (removed is { } r)
                host.Toast("נמחק לגמרי", "בטל", () => MemoryStore.MutateFacts(all => { all.Insert(Math.Min(r.Item1, all.Count), r.Item2); return 0; }));
        }, icon: 14);
        del.ToolTip = "מחיקה לגמרי";
        buttons.Add(del);
        var actions = Kit.Row(2, buttons.ToArray());
        actions.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 1);
        g.Children.Add(actions);
        var row = Kit.ListRow(g, new Thickness(12, 10, 10, 10));
        Kit.Reveal(row, actions);
        return row;
    }

    static void EditFact(ClientFact f, TerminalWindow host)
    {
        Action close = () => { };
        var body = new StackPanel();
        body.Children.Add(Kit.Bar(Kit.T("תיקון עובדה", Display.Sheet, Tone.Text, FontWeights.SemiBold), Kit.IconButton(Icons.Close, 34, () => close())));
        var (frame, text) = Kit.LabeledField("העובדה", "", 15, multiline: true, height: 110);
        text.Text = f.Text;
        frame.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(frame);
        var hint = Kit.T("הנוסח הקודם יישמר בהיסטוריה.", 12.5, Tone.Muted);
        hint.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(hint);
        var save = Kit.Pill("שמור", PillKind.Primary, () =>
        {
            if (text.Text.Trim().Length == 0) return;
            MemoryStore.MutateFacts(all => FactBook.Correct(all, f.Id, text.Text, DateTime.UtcNow));
            close();
            host.Toast("העובדה תוקנה");
        }, height: 44, fontSize: 14.5);
        save.Margin = new Thickness(0, 18, 0, 0);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(save);
        close = host.ShowSheet(body, 540);
        text.Focus();
    }
}
