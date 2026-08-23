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
        panel.Children.Add(Ui.Title("Call stats"));
        var subtitle = Ui.Small("Answered calls only; talk time is time on the line.");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(subtitle);

        _statsList = new StackPanel();
        panel.Children.Add(_statsList);

        _recapLink = Ui.Link("✨ Recap my day", Font.Small);
        _recapLink.Margin = new Thickness(2, Space.Section, 2, 0);
        _recapLink.MouseLeftButtonUp += async (_, _) => await RunRecapAsync();
        panel.Children.Add(_recapLink);

        _recapHost = new StackPanel();
        panel.Children.Add(_recapHost);

        var done = Ui.PrimaryButton("Done");
        done.Margin = Ui.Top(Space.Section);
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
        var cap = Ui.Caption(caption);
        cap.Margin = Ui.Top(Space.Section);
        _statsList.Children.Add(cap);
        if (calls.Count == 0)
        {
            var empty = Ui.EmptyState("No calls yet.");
            empty.Margin = Ui.Top(Space.Tight); // section-level: tighter than a whole-panel empty
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
        var row = new Grid { Margin = Ui.Top(Space.Tight) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Ui.Small(label));
        var value_ = Ui.Text(value, Font.Body, "TextPrimaryBrush", FontWeights.SemiBold);
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
            Ui.Flash(_recapLink, "Needs a Gemini or DeepSeek key — see Notes & dictation", "✨ Recap my day", Motion.Linger);
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
                Ui.Flash(_recapLink, "Recap failed — see log", "✨ Recap my day", Motion.Linger);
                return;
            }
            _recapLink.Text = "✨ Recap my day";
            RenderAiDraft(_recapHost, recap!, null, nested: false);
        }
        finally
        {
            _recapBusy = false;
        }
    }

    public void ShowStats()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowStats);
            return;
        }
        RebuildStats();
        Ui.StaggerIn(_statsList); // every other panel's list arrives this way too
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_statsPanel);
    }
}
