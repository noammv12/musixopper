using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// Clients: everyone the user talked to, stitched from call notes,
/// callbacks and deals (ClientIndex — no database of its own). Search on
/// the right, a virtualized list, and a card with the whole story:
/// stats, the next callback, and a timeline with copy on every entry.
/// </summary>
sealed class ClientsScreen : TerminalScreen
{
    readonly Grid _page = new();
    readonly TextBox _search;
    readonly FrameworkElement _header;
    readonly Border _listCard;
    readonly Border _detail;
    readonly VirtualList _list;
    List<ClientCard> _all = new();
    BonusRules _rules = BonusRules.Default();
    DateTime _now;
    string? _selected;

    public ClientsScreen(TerminalWindow host) : base(host)
    {
        _page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = Header("לקוחות", "כל מי שדיברת איתו, במקום אחד", out _);
        var (frame, box) = Kit.Field("חיפוש", 14.5, 44, 22, leading: Kit.Icon(Icons.Search, 17, Tone.Muted), background: Tone.Glass);
        frame.Width = 320;
        frame.BorderBrush = Tone.GlassRim;
        _search = box;
        _search.TextChanged += (_, _) => Filter();
        var header = Kit.Bar(title, frame);
        title.VerticalAlignment = VerticalAlignment.Bottom;
        frame.VerticalAlignment = VerticalAlignment.Bottom;
        _header = header;
        _page.Children.Add(header);

        _list = new VirtualList(BuildListRow) { MaxHeight = 620 };
        _listCard = Kit.Glass(Display.ListRadius, new Thickness(10));
        _listCard.Width = 340;
        _listCard.VerticalAlignment = VerticalAlignment.Top;
        _listCard.Child = _list;
        _detail = Kit.Glass(padding: new Thickness(30, 28, 30, 28));
        _detail.VerticalAlignment = VerticalAlignment.Top;

        var body = new Grid { Margin = new Thickness(0, Display.Gap, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Display.Gap) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_listCard);
        Grid.SetColumn(_detail, 2);
        body.Children.Add(_detail);
        Grid.SetRow(body, 1);
        _page.Children.Add(body);
        Children.Add(_page);
    }

    public override string Title => "לקוחות";

    public override void Render(TermData d, bool entrance)
    {
        _now = d.Now;
        _rules = d.Rules;
        var deals = SalesStore.ListMonths().SelectMany(m => m.Deals);
        _all = ClientIndex.Build(d.Notes, d.Callbacks, deals);
        if (_selected is null || _all.All(c => c.Key != _selected)) _selected = _all.FirstOrDefault()?.Key;
        Filter();
        if (entrance) Kit.Rise(new[] { _header, _listCard, _detail });
    }

    void Filter()
    {
        var shown = ClientIndex.Search(_all, _search.Text);
        _selection.Clear();
        _list.ItemsSource = shown;
        RenderDetail(_all.FirstOrDefault(c => c.Key == _selected));
    }

    FrameworkElement BuildListRow(object o)
    {
        var c = (ClientCard)o;
        var selected = c.Key == _selected;
        var avatar = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Tone.Fill,
            Child = new TextBlock { Text = c.Initials, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Tone.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Font.Family },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        text.Children.Add(Kit.Auto(Kit.T(c.Name, 14.5, Tone.Text, FontWeights.Medium)));
        text.Children.Add(Kit.T(c.LastLocal == DateTime.MinValue ? "" : He.Ago(c.LastLocal, _now), Display.Tiny, Tone.Muted));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var dot = Kit.Dot(ToneBrush(c.Tone(_now)));
        Grid.SetColumn(dot, 2);
        g.Children.Add(dot);
        var sel = new Border { CornerRadius = new CornerRadius(Display.RowRadius), Background = Tone.Track, Opacity = selected ? 1 : 0 };
        _selection[c.Key] = sel;
        var row = new Border
        {
            CornerRadius = new CornerRadius(Display.RowRadius), Padding = new Thickness(12, 10, 12, 10), Child = g,
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
        };
        Kit.HoverFill(row, Tone.B("#00FFFFFF"), Tone.RowHover);
        Kit.Press(row, 0.98);
        Kit.Clickable(row, () => Select(c.Key));
        return new Grid { Children = { sel, row } };
    }

    readonly Dictionary<string, Border> _selection = new();

    void Select(string key)
    {
        _selected = key;
        foreach (var (k, b) in _selection) b.BeginAnimation(OpacityProperty, Feel.To(k == key ? 1 : 0, 250));
        RenderDetail(_all.FirstOrDefault(c => c.Key == key));
        if (_detail.Child is FrameworkElement fe) Kit.Rise(new[] { fe });
    }

    static System.Windows.Media.Brush ToneBrush(ClientTone t) => t switch
    {
        ClientTone.Deposited => Tone.Green,
        ClientTone.Overdue => Tone.Red,
        ClientTone.Due => Tone.Amber,
        _ => Tone.Accent,
    };

    void RenderDetail(ClientCard? c)
    {
        if (c is null)
        {
            var empty = Kit.T(_all.Count == 0
                ? "לקוחות יופיעו כאן מהשיחות, מהחזרות ומהעסקאות שלך."
                : "לא נמצא לקוח.", Display.Body, Tone.Muted, wrap: true);
            _detail.Child = empty;
            return;
        }
        var col = new StackPanel();
        var avatar = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(30), Background = Tone.Fill,
            Child = new TextBlock { Text = c.Initials, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Tone.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Font.Family },
        };
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        who.Children.Add(Kit.Auto(Kit.T(c.Name, 26, Tone.Text, FontWeights.SemiBold)));
        var meta = c.Meta;
        if (meta.Length > 0)
        {
            var m = Kit.T(meta, 13.5, Tone.Muted);
            m.Margin = new Thickness(0, 3, 0, 0);
            who.Children.Add(m);
        }
        var start = new StackPanel { Orientation = Orientation.Horizontal };
        start.Children.Add(avatar);
        start.Children.Add(who);
        var head = c.Phone is { } phone
            ? Kit.Bar(start, Kit.Pill("", PillKind.Secondary, () => Host.CopyWithToast(phone, $"המספר של {c.Name} הועתק"), content: Kit.Mono(phone, 13, Tone.Text)))
            : Kit.Bar(start);
        col.Children.Add(head);

        var chips = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        foreach (var s in c.Stats(_rules)) chips.Children.Add(Chip(s, Tone.FillSoft, Tone.TextSoft));
        var next = c.NextCallback;
        chips.Children.Add(Chip(next is null ? "אין חזרה פתוחה" : $"{He.When(next.DueAtUtc.ToLocalTime(), _now)} · {next.DisplayLine}", Tone.AccentSoft, Tone.Accent));
        col.Children.Add(chips);

        // What Palon learned about this client on calls (active facts only;
        // history lives on the Memory screen).
        var facts = Palon.Memory.FactBook.ForClient(Palon.Memory.MemoryStore.Facts, c.Name, c.Phone)
            .Where(f => f.IsActive).OrderByDescending(f => f.Pinned).ThenByDescending(f => f.ValidAt).Take(8).ToList();
        if (facts.Count > 0)
        {
            var title = Kit.T("Palon זוכר", 12.5, Tone.Muted, FontWeights.SemiBold);
            title.Margin = new Thickness(12, 10, 0, 2);
            col.Children.Add(title);
            foreach (var f in facts) col.Children.Add(MemoryScreen.FactRow(f, Host));
        }

        var timeline = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var entries = c.Timeline(_rules);
        for (var i = 0; i < entries.Count; i++) timeline.Children.Add(TimelineRow(entries[i], i == entries.Count - 1));
        col.Children.Add(timeline);
        _detail.Child = col;
    }

    static Border Chip(string text, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg) => new()
    {
        CornerRadius = new CornerRadius(999), Background = bg, Padding = new Thickness(14, 8, 14, 8),
        Margin = new Thickness(0, 0, 10, 10), Child = Kit.T(text, 13.5, fg),
    };

    Border TimelineRow(TimelineEntry e, bool last)
    {
        var dotBrush = e.Kind switch
        {
            TimelineKind.Deal => Tone.Green,
            TimelineKind.Callback when !e.Muted => Tone.Amber,
            TimelineKind.Callback => Tone.Dim,
            _ => Tone.Accent,
        };
        var rail = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rail.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = dotBrush });
        if (!last)
        {
            var line = new Border { Width = 1, Background = Tone.Track, Margin = new Thickness(0, 6, 0, -12) };
            Grid.SetRow(line, 1);
            rail.Children.Add(line);
        }
        var text = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
        text.Children.Add(Kit.T($"{He.Ago(e.WhenLocal, _now)} · {e.Label}", Display.Tiny, Tone.Muted));
        var body = Kit.T(e.Text, 14.5, e.Muted ? Tone.Muted : Tone.B("#FFE5E5EA"), wrap: true);
        body.LineHeight = 23;
        body.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(Kit.Auto(body));
        var copy = Kit.Pill("העתק", PillKind.Secondary, () => Host.CopyWithToast(e.Text, "הועתק"), height: 32, fontSize: 12.5);
        copy.VerticalAlignment = VerticalAlignment.Top;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(rail);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Grid.SetColumn(copy, 2);
        g.Children.Add(copy);
        var row = Kit.ListRow(g, new Thickness(10, 12, 10, 12));
        row.CornerRadius = new CornerRadius(16);
        Kit.Reveal(row, copy);
        return row;
    }
}
