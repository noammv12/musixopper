using System.Windows;
using System.Windows.Controls;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

enum TerminalPage { Today, Callbacks, Month, Clients, Templates, Coaching, Memory }

/// <summary>
/// Everything a screen reads, loaded once per render from the stores on
/// disk (small JSON files — milliseconds). Screens never cache store state
/// between renders, so what's shown is always what's saved.
/// </summary>
sealed class TermData
{
    public DateTime Now { get; private init; }
    public List<Callback> Callbacks { get; private init; } = new();
    public List<CallNote> Notes { get; private init; } = new();
    public BonusRules Rules { get; private init; } = BonusRules.Default();
    /// <summary>The current calendar month's book (null until one exists).</summary>
    public MonthBook? Book { get; private init; }
    public MonthStats? Stats { get; private init; }
    public CallbackCounts Counts { get; private init; }

    public static TermData Load()
    {
        var now = DateTime.Now;
        var callbacks = CallbackStore.Load();
        var rules = SalesStore.LoadRules();
        var book = SalesStore.Get(now.Year, now.Month);
        return new TermData
        {
            Now = now,
            Callbacks = callbacks,
            Notes = NotesStore.Load(),
            Rules = rules,
            Book = book,
            Stats = book is null ? null : SalesStats.Compute(book, rules, now),
            Counts = CallbackPlanner.Counts(callbacks, now),
        };
    }
}

/// <summary>
/// One Terminal screen. Keeps its inputs (search boxes, quick-add) alive
/// across renders so typing is never interrupted; Render rebuilds only the
/// data-driven parts. <c>entrance</c> plays the staggered rise — on
/// navigation only, never on a data refresh.
/// </summary>
abstract class TerminalScreen : Grid
{
    protected TerminalWindow Host { get; }

    protected TerminalScreen(TerminalWindow host)
    {
        Host = host;
    }

    public abstract string Title { get; }

    public abstract void Render(TermData data, bool entrance);

    /// <summary>A column of cards with the page's 20px rhythm.</summary>
    protected static StackPanel Stack(double gap = Display.Gap) => new() { Tag = gap };

    protected static void Add(StackPanel stack, FrameworkElement el)
    {
        if (stack.Children.Count > 0) el.Margin = new Thickness(el.Margin.Left, el.Margin.Top + (double)stack.Tag, el.Margin.Right, el.Margin.Bottom);
        stack.Children.Add(el);
    }

    /// <summary>The page header every screen but Today opens with.</summary>
    protected static StackPanel Header(string title, string subtitle, out TextBlock sub)
    {
        var h = new StackPanel();
        h.Children.Add(Kit.T(title, Display.Hero, Tone.Text, FontWeights.SemiBold));
        sub = Kit.T(subtitle, Display.Body, Tone.Muted);
        sub.Margin = new Thickness(0, 4, 0, 0);
        h.Children.Add(sub);
        return h;
    }

    /// <summary>Four equal columns with 20px gutters (the design's grid).</summary>
    protected static Grid FourColumns()
    {
        var g = new Grid();
        for (var i = 0; i < 4; i++)
        {
            if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Display.Gap) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        return g;
    }

    protected static void Place(Grid g, FrameworkElement el, int row, int col, int span = 1)
    {
        while (g.RowDefinitions.Count <= row * 2)
            g.RowDefinitions.Add(new RowDefinition { Height = g.RowDefinitions.Count % 2 == 1 ? new GridLength(Display.Gap) : GridLength.Auto });
        Grid.SetRow(el, row * 2);
        Grid.SetColumn(el, col * 2);
        Grid.SetColumnSpan(el, span * 2 - 1);
        g.Children.Add(el);
    }
}
