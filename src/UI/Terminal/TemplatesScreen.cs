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
    List<MessageTemplate> _templates = new();
    string? _current;

    public TemplatesScreen(TerminalWindow host) : base(host)
    {
        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Header("תבניות", "השם של הלקוח נכנס לבד. לחיצה אחת ומועתק", out _);
        var add = Kit.Pill("תבנית חדשה", PillKind.Secondary, () => Editor(null));
        _header = Kit.Bar(title, add);
        add.VerticalAlignment = VerticalAlignment.Bottom;
        page.Children.Add(_header);

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
        var copy = Kit.Pill("העתק", PillKind.Primary, CopyCurrent, height: 40, fontSize: 14);
        copy.Margin = new Thickness(10, 0, 0, 0);
        var head = Kit.Bar(_title, edit, nameFrame, copy);

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
        Grid.SetRow(body, 1);
        page.Children.Add(body);
        Children.Add(page);
    }

    public override string Title => "תבניות";

    MessageTemplate? Current => _templates.FirstOrDefault(t => t.Id == _current) ?? _templates.FirstOrDefault();

    public override void Render(TermData d, bool entrance)
    {
        _templates = TemplatesStore.Load();
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
        Host.CopyWithToast(TemplateFill.Fill(t, first), first.Length > 0 ? $"התבנית הועתקה עם השם {first}" : "התבנית הועתקה");
    }

    void Editor(MessageTemplate? existing)
    {
        var t = existing is null ? new MessageTemplate() : new MessageTemplate
        {
            Id = existing.Id, Title = existing.Title, Tag = existing.Tag, Region = existing.Region,
            Mode = existing.Mode, Head = existing.Head, Text = existing.Text,
        };
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
        close = Host.ShowSheet(body, 600);
        title.Focus();
    }
}
