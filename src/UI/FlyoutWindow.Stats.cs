using System.Windows;
using System.Windows.Controls;
using Palon.Notes;

namespace Palon.UI;

/// <summary>The flyout's call-stats panel plus the ambient "Today:" line.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _statsPanel;
    TextBlock _statsLine = null!;
    StackPanel _statsList = null!;
    TextBlock _recapLink = null!;
    StackPanel _recapHost = null!;
    bool _recapBusy;

    StackPanel BuildStatsPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Text("Call stats", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Answered calls only; talk time is time on the line.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        _statsList = new StackPanel();
        panel.Children.Add(_statsList);

        _recapLink = Ui.Link("✨ Recap my day", 11);
        _recapLink.Margin = new Thickness(2, 14, 2, 0);
        _recapLink.MouseLeftButtonUp += async (_, _) => await RunRecapAsync();
        panel.Children.Add(_recapLink);

        _recapHost = new StackPanel();
        panel.Children.Add(_recapHost);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 14, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        CallStatsStore.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            UpdateStatsLine();
            if (panel.Visibility == Visibility.Visible) RebuildStats();
        });
        UpdateStatsLine(); // the main panel (and its stats line) is built before this panel
        return panel;
    }

    /// <summary>The one-line "Today: 14 calls · 1h 12m" under the main status row.</summary>
    void UpdateStatsLine()
    {
        var today = TodayCalls(CallStatsStore.Load());
        if (today.Count == 0)
        {
            _statsLine.Visibility = Visibility.Collapsed;
            return;
        }
        _statsLine.Visibility = Visibility.Visible;
        _statsLine.Text = $"Today: {today.Count} call{(today.Count == 1 ? "" : "s")}" +
                          $" · {FormatTalkTime(today.Sum(c => (long)c.DurationSec))}";
    }

    void RebuildStats()
    {
        _statsList.Children.Clear();
        var all = CallStatsStore.Load();
        var weekCutoff = DateTime.Now.Date.AddDays(-6); // rolling 7 days incl. today
        AddStatSection("TODAY", TodayCalls(all));
        AddStatSection("LAST 7 DAYS", all.Where(c => c.StartedUtc.ToLocalTime().Date >= weekCutoff).ToList());
    }

    static List<CallRecord> TodayCalls(List<CallRecord> all) =>
        all.Where(c => c.StartedUtc.ToLocalTime().Date == DateTime.Now.Date).ToList();

    void AddStatSection(string caption, List<CallRecord> calls)
    {
        var cap = Ui.Text(caption, 10, "TextSecondaryBrush", FontWeights.SemiBold);
        cap.Margin = new Thickness(0, 14, 0, 0);
        _statsList.Children.Add(cap);
        if (calls.Count == 0)
        {
            var empty = Ui.Text("No calls yet.", 11, "TextSecondaryBrush");
            empty.Margin = new Thickness(0, 6, 0, 0);
            _statsList.Children.Add(empty);
            return;
        }
        var total = calls.Sum(c => (long)c.DurationSec);
        AddStatRow("Calls", calls.Count.ToString());
        AddStatRow("Talk time", FormatTalkTime(total));
        AddStatRow("Average call", FormatTalkTime(total / calls.Count));
    }

    void AddStatRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Ui.Text(label, 11.5, "TextSecondaryBrush"));
        var value_ = Ui.Text(value, 11.5, "TextPrimaryBrush", FontWeights.SemiBold);
        Grid.SetColumn(value_, 1);
        row.Children.Add(value_);
        _statsList.Children.Add(row);
    }

    static string FormatTalkTime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }

    async Task RunRecapAsync()
    {
        if (_recapBusy) return;
        if (!AiChat.HasKey)
        {
            _recapLink.Text = "✨ Recap my day — needs a Gemini or DeepSeek key";
            return;
        }
        _recapBusy = true;
        _recapLink.Text = "Writing your recap…";
        try
        {
            var data = await Task.Run(DailyRecap.BuildData);
            var recap = await AiChat.RecapAsync(data, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(recap))
            {
                _recapLink.Text = "✨ Recap my day (failed — see log)";
                return;
            }
            _recapLink.Text = "✨ Recap my day";
            RenderRecap(recap!);
        }
        finally
        {
            _recapBusy = false;
        }
    }

    void RenderRecap(string text)
    {
        _recapHost.Children.Clear();
        var stack = new StackPanel();
        var body = Ui.Text(text, 11, "TextPrimaryBrush");
        body.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(body);
        var copy = Ui.Link("Copy", 10.5);
        copy.Margin = new Thickness(0, 6, 0, 0);
        copy.MouseLeftButtonUp += (_, _) =>
        {
            if (SnippetPaster.TrySetClipboard(text)) Ui.Flash(copy, "Copied ✓", "Copy");
        };
        stack.Children.Add(copy);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        _recapHost.Children.Add(card);
    }

    public void ShowStats()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowStats);
            return;
        }
        RebuildStats();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_statsPanel);
    }
}
