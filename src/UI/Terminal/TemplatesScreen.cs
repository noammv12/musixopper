using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Templates: the user's WhatsApp texts. Pick one, type the client's name
/// and see exactly where it lands (highlighted), copy in one tap. Add,
/// edit and delete in a simple sheet.
/// </summary>
sealed class TemplatesScreen : TerminalScreen
{
    readonly StackPanel _list = new();
    readonly TextBlock _title;
    readonly TextBlock _preview;
    readonly TextBox _name;
    readonly FrameworkElement _header;
    readonly FrameworkElement _previewCard;
    readonly StackPanel _proposals = new() { Margin = new Thickness(0, Display.Gap, 0, 0) };
    readonly Border _badge;
    readonly TextBlock _badgeText = Kit.T("", 12, Tone.OnPrimary, FontWeights.SemiBold);
    List<TemplateSend> _sends = new();
    List<(string ClientName, DateTime Date)> _deals = new();
    List<(DateTime StartedUtc, string? Number)> _calls = new();
    List<MessageTemplate> _templates = new();
    string? _current;

    public TemplatesScreen(TerminalWindow host) : base(host)
    {
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Header("תבניות", "השם של הלקוח נכנס לבד. לחיצה אחת ומועתק", out _);
        _badge = new Border
        {
            Height = 26, CornerRadius = new CornerRadius(13), Background = Tone.Primary, Padding = new Thickness(11, 0, 11, 0),
            Child = _badgeText, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "הצעות של Palon לתבניות",
        };
        _badgeText.VerticalAlignment = VerticalAlignment.Center;
        Kit.Clickable(_badge, () => _proposals.BringIntoView());
        var free = Kit.Pill("הודעה חופשית", PillKind.Ghost, () => Compose(null, _name!.Text.Trim()));
        free.ToolTip = "לכתוב הודעה בלי תבנית. אם תחזור על אותה הודעה, Palon יציע לשמור אותה כתבנית";
        var add = Kit.Pill("תבנית חדשה", PillKind.Secondary, () => Editor(null));
        var ends = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        _badge.Margin = new Thickness(0, 0, 10, 0);
        free.Margin = new Thickness(0, 0, 8, 0);
        ends.Children.Add(_badge);
        ends.Children.Add(free);
        ends.Children.Add(add);
        _header = Kit.Bar(title, ends);
        page.Children.Add(_header);
        _proposals.Visibility = Visibility.Collapsed;
        Grid.SetRow(_proposals, 1);
        page.Children.Add(_proposals);

        _list.Width = 340;
        _list.VerticalAlignment = VerticalAlignment.Top;

        _title = Kit.T("", Display.Sheet, Tone.Text, FontWeights.SemiBold);
        var (nameHost, name) = Kit.Input("בלי שם", 14);
        _name = name;
        nameHost.Width = 110;
        var nameFrame = new Border
        {
            Height = 40, CornerRadius = new CornerRadius(20), Background = Tone.FillSoft, BorderBrush = Tone.Hairline,
            BorderThickness = new Thickness(1), Padding = new Thickness(14, 0, 14, 0), Cursor = System.Windows.Input.Cursors.IBeam,
        };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = Kit.T("שם", 12.5, Tone.Muted);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 8, 0);
        nameRow.Children.Add(label);
        nameRow.Children.Add(nameHost);
        nameFrame.Child = nameRow;
        nameFrame.MouseLeftButtonDown += (_, _) => _name.Focus();
        _name.GotKeyboardFocus += (_, _) => nameFrame.BorderBrush = Tone.AccentLine;
        _name.LostKeyboardFocus += (_, _) => nameFrame.BorderBrush = Tone.Hairline;
        _name.TextChanged += (_, _) => ShowPreview();

        var edit = Kit.IconButton(Icons.Edit, 40, () => Editor(Current), PillKind.Secondary, 16, Tone.Text);
        edit.ToolTip = "עריכה";
        edit.Margin = new Thickness(14, 0, 0, 0);
        nameFrame.Margin = new Thickness(10, 0, 0, 0);
        var tweak = Kit.Pill("שנה והעתק", PillKind.Secondary, () => { if (Current is { } c) Compose(c, _name.Text.Trim()); }, height: 40, fontSize: 14);
        tweak.ToolTip = "לשנות את הטקסט רק לשליחה הזאת. אם תשנה אותו דבר כמה פעמים, Palon יציע לעדכן את התבנית";
        tweak.Margin = new Thickness(10, 0, 0, 0);
        var copy = Kit.Pill("העתק", PillKind.Primary, CopyCurrent, height: 40, fontSize: 14);
        copy.Margin = new Thickness(10, 0, 0, 0);
        var head = Kit.Bar(_title, edit, nameFrame, tweak, copy);

        _preview = new TextBlock
        {
            FontSize = 15, FontFamily = Font.Family, Foreground = Tone.B("#FFE5E5EA"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 26,
        };
        var previewBox = new Border
        {
            CornerRadius = new CornerRadius(22), Background = Tone.B("#4D000000"), BorderBrush = Tone.B("#0DFFFFFF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(24, 22, 24, 22), Margin = new Thickness(0, 18, 0, 0),
        };
        var scroll = Ui.ChainWheel(Ui.ThinScroll(new ScrollViewer { Content = _preview, MaxHeight = 540, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));
        previewBox.Child = scroll;
        var card = Kit.Glass(padding: new Thickness(28, 26, 28, 26));
        card.Child = new StackPanel { Children = { head, previewBox } };
        card.VerticalAlignment = VerticalAlignment.Top;
        _previewCard = card;

        var body = new Grid { Margin = new Thickness(0, Display.Gap, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Display.Gap) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_list);
        Grid.SetColumn(card, 2);
        body.Children.Add(card);
        Grid.SetRow(body, 2);
        page.Children.Add(body);
        Children.Add(page);
    }

    public override string Title => "תבניות";

    MessageTemplate? Current => _templates.FirstOrDefault(t => t.Id == _current) ?? _templates.FirstOrDefault();

    public override void Render(TermData d, bool entrance)
    {
        _templates = TemplatesStore.Load();
        _sends = TemplateLearningStore.Sends();
        _deals = Palon.Sales.SalesStore.ListMonths().SelectMany(m => m.Deals).Select(x => (x.ClientName, x.Date)).ToList();
        _calls = CallStatsStore.Load().Select(c => (c.StartedUtc, c.Number)).ToList();
        RenderProposals();
        if (_current is null || _templates.All(t => t.Id != _current)) _current = _templates.FirstOrDefault()?.Id;
        RenderList();
        ShowPreview();
        if (entrance)
        {
            var all = new List<FrameworkElement> { _header, _previewCard };
            all.AddRange(_list.Children.OfType<FrameworkElement>());
            Kit.Rise(all);
        }
    }

    readonly Dictionary<string, Border> _cards = new();

    void RenderList()
    {
        _list.Children.Clear();
        _cards.Clear();
        foreach (var t in _templates)
        {
            var top = Kit.Bar(Kit.T(t.Title, Display.Body, Tone.Text, FontWeights.SemiBold), Kit.T(t.Region, 11.5, Tone.Muted));
            var col = new StackPanel();
            col.Children.Add(top);
            var tag = Kit.T(t.Tag, Display.Tiny, Tone.Muted);
            tag.Margin = new Thickness(0, 5, 0, 0);
            col.Children.Add(tag);
            if (TemplateLearning.Stats(t.Id, _sends, _deals, _calls) is { } st)
            {
                var line = Kit.T($"{st.Uses} שליחות · {st.Deposits} הפקדות תוך 14 יום · {st.Rate:P0}", Display.Tiny, Tone.Body);
                line.ToolTip = $"הפקדה = עסקה לאותו לקוח עד 14 יום אחרי השליחה. {st.NextCalls} שליחות הובילו לשיחה נוספת. הערכה זהירה: לפחות {st.WilsonLower:P0}.";
                line.Margin = new Thickness(0, 5, 0, 0);
                col.Children.Add(line);
            }
            var card = Kit.Glass(22, new Thickness(18, 16, 18, 16), lift: true);
            card.Child = col;
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.Margin = new Thickness(0, _list.Children.Count > 0 ? 10 : 0, 0, 0);
            var id = t.Id;
            Kit.Clickable(card, () =>
            {
                _current = id;
                Paint();
                ShowPreview();
            });
            Kit.Press(card);
            _cards[id] = card;
            _list.Children.Add(card);
        }
        Paint();
        if (_templates.Count == 0) _list.Children.Add(Kit.T("אין תבניות. הוסף אחת.", Display.Body, Tone.Muted));
    }

    void Paint()
    {
        foreach (var (id, card) in _cards)
        {
            var on = id == Current?.Id;
            card.Background = on ? Tone.Track : Tone.Glass;
            card.BorderBrush = on ? Tone.AccentLine : Tone.GlassRim;
        }
    }

    void ShowPreview()
    {
        var t = Current;
        _preview.Inlines.Clear();
        if (t is null)
        {
            _title.Text = "";
            return;
        }
        _title.Text = t.Title;
        var parts = TemplateFill.Parts(t, _name.Text);
        _preview.FlowDirection = Bidi.HasRtl(parts.Full) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (parts.Pre.Length > 0) _preview.Inlines.Add(new Run(parts.Pre));
        if (parts.Name.Length > 0) _preview.Inlines.Add(new Run(parts.Name) { Foreground = Tone.Text, Background = Tone.AccentSoft, FontWeight = FontWeights.Medium });
        _preview.Inlines.Add(new Run(parts.Post));
    }

    void CopyCurrent()
    {
        if (Current is not { } t) return;
        var first = _name.Text.Trim();
        var text = TemplateFill.Fill(t, first);
        Host.CopyWithToast(text, first.Length > 0 ? $"התבנית הועתקה עם השם {first}" : "התבנית הועתקה");
        TemplateLearningStore.LogCopy(t, first, null, text, null, "templates");
    }

    /// <summary>Change the text for this one send (or write a free message),
    /// then copy. The final text is logged so Palon can notice repeats.</summary>
    void Compose(MessageTemplate? template, string name)
    {
        var filled = template is null ? "" : TemplateFill.Fill(template, name);
        Action close = () => { };
        var body = new StackPanel();
        var x = Kit.IconButton(Icons.Close, 34, () => close());
        body.Children.Add(Kit.Bar(Kit.T(template is null ? "הודעה חופשית" : $"{template.Title} · לשליחה הזאת", Display.Sheet, Tone.Text, FontWeights.SemiBold), x));
        var (frame, box) = Kit.LabeledField("הטקסט", "מה לשלוח ללקוח", 14, multiline: true, height: 260);
        box.Text = filled;
        frame.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(frame);
        var hint = Kit.T(template is null
            ? "Palon זוכר הודעות שחוזרות על עצמן ויציע להפוך אותן לתבנית."
            : "התבנית עצמה לא משתנה. אם תשנה אותו דבר 3 פעמים, Palon יציע לעדכן אותה.", 12.5, Tone.Muted, wrap: true);
        hint.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(hint);
        var copy = Kit.Pill("העתק", PillKind.Primary, () =>
        {
            var final = box.Text.Replace("\r\n", "\n");
            if (final.Trim().Length == 0) { box.Focus(); return; }
            Host.CopyWithToast(final, "ההודעה הועתקה");
            TemplateLearningStore.LogCopy(template, name, null, template is null ? final : filled, final, template is null ? "free" : "templates-edit");
            close();
        }, height: 44, fontSize: 14.5);
        copy.Margin = new Thickness(0, 18, 0, 0);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(copy);
        close = Host.ShowSheet(body, 600);
        box.Focus();
    }

    // ---- Palon's suggestions ------------------------------------------------------

    void RenderProposals()
    {
        _proposals.Children.Clear();
        var list = TemplateLearning.Proposals(_templates, _sends, TemplateLearningStore.Decided());
        _badge.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _badgeText.Text = list.Count == 1 ? "הצעה אחת מ-Palon" : $"{list.Count} הצעות מ-Palon";
        _proposals.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in list.Take(3))
        {
            var card = ProposalCard(p);
            if (_proposals.Children.Count > 0) card.Margin = new Thickness(0, 10, 0, 0);
            _proposals.Children.Add(card);
        }
    }

    Border ProposalCard(TemplateProposal p)
    {
        var target = p.TemplateId is null ? null : _templates.FirstOrDefault(t => t.Id == p.TemplateId);
        var card = Kit.Glass(22, new Thickness(22, 18, 22, 18));
        var col = new StackPanel();
        var say = p.Kind == ProposalKind.Edit
            ? $"שמתי לב שאתה משנה את \"{target?.Title}\" באותה צורה — {p.Count} פעמים. לעדכן את התבנית?"
            : $"שלחת הודעה דומה {p.Count} פעמים בלי תבנית. לשמור אותה כתבנית חדשה?";
        col.Children.Add(Kit.T(say, Display.Body, Tone.Text, FontWeights.SemiBold, wrap: true));
        var sub = Kit.T(p.Kind == ProposalKind.Edit ? "ככה זה ייראה (אדום יוצא, ירוק נכנס). שום דבר לא משתנה בלי אישור שלך." : "ככה היא תיראה. השם של הלקוח ייכנס לבד.", Display.Tiny, Tone.Muted, wrap: true);
        sub.Margin = new Thickness(0, 4, 0, 10);
        col.Children.Add(sub);

        var diff = new TextBlock { FontSize = 13.5, FontFamily = Font.Family, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Foreground = Tone.Body };
        diff.FlowDirection = Bidi.HasRtl(p.After) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var lines = TemplateLearning.DiffLines(p.Before, p.After);
        var first = true;
        for (var i = 0; i < lines.Count; i++)
        {
            var (op, text) = lines[i];
            if (op == TemplateLearning.LineOp.Same && p.Kind == ProposalKind.Edit && !Near(lines, i)) continue;
            if (!first) diff.Inlines.Add(new LineBreak());
            first = false;
            var run = new Run(text.Length == 0 ? " " : text);
            if (op == TemplateLearning.LineOp.Removed) { run.Foreground = Tone.RedText; run.TextDecorations = TextDecorations.Strikethrough; }
            else if (op == TemplateLearning.LineOp.Added && p.Kind == ProposalKind.Edit) { run.Foreground = Tone.GreenText; }
            diff.Inlines.Add(run);
        }
        var well = new Border
        {
            CornerRadius = new CornerRadius(16), Background = Tone.B("#4D000000"), Padding = new Thickness(16, 12, 16, 12),
            Child = Ui.ChainWheel(Ui.ThinScroll(new ScrollViewer { Content = diff, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto })),
        };
        col.Children.Add(well);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Kit.Pill(p.Kind == ProposalKind.Edit ? "עדכן תבנית" : "שמור כתבנית", PillKind.Primary, () => Accept(p, target, edit: false), height: 36, fontSize: 13.5));
        var edit = Kit.Pill("ערוך קודם", PillKind.Secondary, () => Accept(p, target, edit: true), height: 36, fontSize: 13.5);
        edit.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(edit);
        var no = Kit.Pill("לא, תודה", PillKind.Ghost, () =>
        {
            if (!TemplateLearningStore.Decide(p.Key, accepted: false)) return;
            Host.Toast("בסדר, לא אציע את זה שוב", "בטל", () => TemplateLearningStore.Undecide(p.Key));
        }, height: 36, fontSize: 13.5);
        no.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(no);
        col.Children.Add(actions);
        card.Child = col;
        return card;
    }

    /// <summary>Unchanged lines shown only next to a change, to keep the card short.</summary>
    static bool Near(List<(TemplateLearning.LineOp Op, string Text)> lines, int i)
    {
        bool Changed(int k) => k >= 0 && k < lines.Count && lines[k].Op != TemplateLearning.LineOp.Same;
        return Changed(i - 1) || Changed(i + 1);
    }

    void Accept(TemplateProposal p, MessageTemplate? target, bool edit)
    {
        MessageTemplate t;
        if (p.Kind == ProposalKind.Edit)
        {
            if (target is null) return;
            t = target.Clone();
            t.Text = p.After;
        }
        else
        {
            var words = p.After.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(4);
            t = new MessageTemplate { Title = string.Join(" ", words), Tag = "הצעה של Palon", Region = "פרו", Mode = TemplateMode.Prepend, Text = p.After };
        }
        if (edit)
        {
            Editor(t, onSaved: () => TemplateLearningStore.Decide(p.Key, accepted: true), isNew: p.Kind == ProposalKind.NewTemplate);
            return;
        }
        if (!TemplatesStore.Save(t, "הצעה של Palon")) { Host.Toast("לא הצלחתי לשמור את התבנית"); return; }
        TemplateLearningStore.Decide(p.Key, accepted: true);
        _current = t.Id;
        var before = target;
        Host.Toast(p.Kind == ProposalKind.Edit ? "התבנית עודכנה" : "התבנית נשמרה", "בטל", () =>
        {
            TemplateLearningStore.Undecide(p.Key);
            if (before is not null) TemplatesStore.Save(before, "ביטול");
            else TemplatesStore.Remove(t.Id);
        });
    }

    void Editor(MessageTemplate? existing, Action? onSaved = null, bool isNew = false)
    {
        var t = existing is null ? new MessageTemplate() : existing.Clone();
        if (isNew) existing = null;
        Action close = () => { };
        var body = new StackPanel();
        var x = Kit.IconButton(Icons.Close, 34, () => close());
        body.Children.Add(Kit.Bar(Kit.T(existing is null ? "תבנית חדשה" : "עריכת תבנית", Display.Sheet, Tone.Text, FontWeights.SemiBold), x));

        var (titleFrame, title) = Kit.LabeledField("כותרת", "לדוגמה: פתיחה · קולמקס פרו", 15);
        title.Text = t.Title;
        var (tagFrame, tag) = Kit.LabeledField("מתי שולחים", "לדוגמה: אחרי שיחה על פרו", 15);
        tag.Text = t.Tag;
        var regions = new[] { "פרו", "ישראל" };
        var region = Math.Max(0, Array.IndexOf(regions, t.Region));
        var seg = Kit.Segmented(regions, region, i => region = i, stretch: true, height: 34, fontSize: 14);
        var (textFrame, text) = Kit.LabeledField("הטקסט", "הטקסט שנשלח ללקוח", 14, multiline: true, height: 220);
        text.Text = t.Text;
        var hint = Kit.T("השם של הלקוח יתווסף בשורה \"היי {שם}!\" מעל הטקסט.", 12.5, Tone.Muted, wrap: true);
        if (t.Mode == TemplateMode.Replace && t.Head.Length > 0) hint.Text = $"השם של הלקוח נכנס אחרי \"{t.Head.Trim()}\" בתחילת הטקסט.";

        foreach (var el in new FrameworkElement[] { titleFrame, tagFrame, seg, textFrame, hint })
        {
            el.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(el);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(Kit.Pill("שמור", PillKind.Primary, () =>
        {
            if (title.Text.Trim().Length == 0 || text.Text.Trim().Length == 0)
            {
                (title.Text.Trim().Length == 0 ? title : text).Focus();
                return;
            }
            t.Title = title.Text.Trim();
            t.Tag = tag.Text.Trim();
            t.Region = regions[region];
            t.Text = text.Text.Replace("\r\n", "\n");
            if (!TemplatesStore.Save(t))
            {
                Host.Toast("לא הצלחתי לשמור את התבנית");
                return;
            }
            _current = t.Id;
            onSaved?.Invoke();
            close();
            Host.Toast("התבנית נשמרה");
        }, height: 44, fontSize: 14.5));
        if (existing is not null)
        {
            var del = Kit.Pill("מחק", PillKind.Ghost, () =>
            {
                var index = _templates.FindIndex(x2 => x2.Id == existing.Id);
                if (!TemplatesStore.Remove(existing.Id)) return;
                close();
                Host.Toast($"{existing.Title} נמחקה", "בטל", () => TemplatesStore.Restore(existing, index));
            }, height: 44, fontSize: 14.5);
            ((TextBlock)del.Child).Foreground = Tone.RedText;
            del.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(del);
        }
        body.Children.Add(actions);
        if (existing is not null && t.History.Count > 0) body.Children.Add(HistoryBlock(t, () => close()));
        close = Host.ShowSheet(body, 600);
        title.Focus();
    }

    /// <summary>"Version N" and the earlier versions, each one tap to bring back.</summary>
    FrameworkElement HistoryBlock(MessageTemplate t, Action close)
    {
        var col = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        col.Children.Add(Kit.T($"גרסה {t.Version} · גרסאות קודמות", Display.Meta, Tone.Muted));
        foreach (var r in Enumerable.Reverse(t.History).Take(5))
        {
            var label = Kit.T($"גרסה {r.Version} · {r.SavedUtc.ToLocalTime():dd/MM HH:mm}{(r.Reason.Length > 0 ? " · " + r.Reason : "")}", 12.5, Tone.Body);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ToolTip = r.Text;
            var back = Kit.Pill("שחזר", PillKind.Ghost, () =>
            {
                var restored = t.Clone();
                restored.Title = r.Title;
                restored.Text = r.Text;
                if (!TemplatesStore.Save(restored, $"שחזור גרסה {r.Version}")) return;
                close();
                Host.Toast($"גרסה {r.Version} שוחזרה");
            }, height: 30, fontSize: 12.5);
            var row = Kit.Bar(label, back);
            row.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(row);
        }
        return col;
    }
}
