using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Bridget.UI;

/// <summary>The flyout's Reminders panel: quick-add + pending list.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _remindersPanel;
    TextBox _reminderUrlBox = null!;
    TextBox _reminderLabelBox = null!;
    TextBox _reminderCustomTimeBox = null!;
    StackPanel _reminderList = null!;
    TextBlock _reminderStatus = null!;
    readonly List<Border> _timeChips = new();
    int _timeSelection = 1; // default "1h"

    static readonly (string Label, Func<DateTime?> Due)[] TimeChoices =
    {
        ("30m", () => DateTime.UtcNow.AddMinutes(30)),
        ("1h", () => DateTime.UtcNow.AddHours(1)),
        ("3h", () => DateTime.UtcNow.AddHours(3)),
        ("Tmrw 9:00", () => DateTime.Today.AddDays(1).AddHours(9).ToUniversalTime()),
        ("Custom", () => null),
    };

    StackPanel BuildRemindersPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Reminders", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Paste the lead's link, pick a time — Bridget pops it above the taskbar when it's time to call.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        var urlCaption = Ui.Text("Link (CRM, WhatsApp, anything)", 10.5, "TextSecondaryBrush", FontWeights.SemiBold);
        urlCaption.Margin = new Thickness(2, 12, 0, 0);
        panel.Children.Add(urlCaption);
        _reminderUrlBox = Ui.TextBox("");
        _reminderUrlBox.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_reminderUrlBox);

        var labelCaption = Ui.Text("Label (optional)", 10.5, "TextSecondaryBrush", FontWeights.SemiBold);
        labelCaption.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(labelCaption);
        _reminderLabelBox = Ui.TextBox("");
        _reminderLabelBox.MaxLength = 40;
        _reminderLabelBox.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_reminderLabelBox);

        var chipRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < TimeChoices.Length; i++)
        {
            var index = i;
            var chip = MakeTimeChip(TimeChoices[i].Label);
            chip.MouseLeftButtonUp += (_, _) => SelectTimeChip(index);
            _timeChips.Add(chip);
            chipRow.Children.Add(chip);
        }
        panel.Children.Add(chipRow);

        _reminderCustomTimeBox = Ui.TextBox("18:30");
        _reminderCustomTimeBox.Width = 64;
        _reminderCustomTimeBox.HorizontalAlignment = HorizontalAlignment.Left;
        _reminderCustomTimeBox.Margin = new Thickness(0, 8, 0, 0);
        _reminderCustomTimeBox.Visibility = Visibility.Collapsed;
        panel.Children.Add(_reminderCustomTimeBox);

        var add = Ui.PrimaryButton("Set reminder");
        add.Margin = new Thickness(0, 12, 0, 0);
        add.MouseLeftButtonUp += (_, _) => AddReminder();
        panel.Children.Add(add);

        _reminderStatus = Ui.Text("", 10.5, "TextSecondaryBrush");
        _reminderStatus.Margin = new Thickness(2, 6, 2, 0);
        _reminderStatus.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_reminderStatus);

        panel.Children.Add(Ui.Divider(12, 4));

        _reminderList = new StackPanel();
        var scroll = new ScrollViewer
        {
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _reminderList,
        };
        panel.Children.Add(scroll);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 12, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        SelectTimeChip(_timeSelection);
        // Keep the list live while dock/scheduler actions mutate the store.
        ReminderStore.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            if (_remindersPanel.Visibility == Visibility.Visible) RebuildReminderList();
        });
        return panel;
    }

    Border MakeTimeChip(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
        };
        return chip;
    }

    void SelectTimeChip(int index)
    {
        _timeSelection = index;
        for (var i = 0; i < _timeChips.Count; i++)
        {
            var chip = _timeChips[i];
            var label = (TextBlock)chip.Child;
            chip.SetResourceReference(Border.BackgroundProperty, i == index ? "AccentBrush" : "ControlFillBrush");
            label.SetResourceReference(TextBlock.ForegroundProperty, i == index ? "OnAccentBrush" : "TextPrimaryBrush");
        }
        _reminderCustomTimeBox.Visibility =
            TimeChoices[index].Label == "Custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddReminder()
    {
        var url = _reminderUrlBox.Text.Trim();
        if (!ReminderStore.IsValidUrl(url))
        {
            ShowReminderStatus("That doesn't look like a link — paste a full http(s) address.");
            return;
        }

        DateTime dueUtc;
        if (TimeChoices[_timeSelection].Due() is { } due)
        {
            dueUtc = due;
        }
        else
        {
            // Custom HH:mm — today if still ahead, otherwise tomorrow.
            if (!TimeSpan.TryParse(_reminderCustomTimeBox.Text.Trim(), out var time) ||
                time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            {
                ShowReminderStatus("Time should look like 14:30.");
                return;
            }
            var local = DateTime.Today + time;
            if (local <= DateTime.Now) local = local.AddDays(1);
            dueUtc = local.ToUniversalTime();
        }

        if (ReminderStore.Add(url, _reminderLabelBox.Text, dueUtc) is null)
        {
            ShowReminderStatus("Couldn't save the reminder (too many pending?).");
            return;
        }
        _reminderUrlBox.Text = "";
        _reminderLabelBox.Text = "";
        ShowReminderStatus("Reminder set ✓");
        RebuildReminderList();
    }

    void ShowReminderStatus(string text)
    {
        _reminderStatus.Text = text;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        clear.Tick += (_, _) =>
        {
            clear.Stop();
            if (_reminderStatus.Text == text) _reminderStatus.Text = "";
        };
        clear.Start();
    }

    void RebuildReminderList()
    {
        _reminderList.Children.Clear();
        var pending = ReminderStore.Load()
            .Where(r => r.State == ReminderState.Pending)
            .OrderBy(r => r.DueAtUtc)
            .ToList();

        if (pending.Count == 0)
        {
            var empty = Ui.Text("Nothing pending.", 11, "TextSecondaryBrush");
            empty.Margin = new Thickness(2, 8, 0, 0);
            _reminderList.Children.Add(empty);
            return;
        }

        foreach (var reminder in pending)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = Ui.Text(reminder.DisplayLabel, 12, "TextPrimaryBrush", FontWeights.SemiBold);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);

            var due = Ui.Text(FormatDue(reminder.DueAtUtc), 11, "TextSecondaryBrush");
            due.VerticalAlignment = VerticalAlignment.Center;
            due.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(due, 1);
            row.Children.Add(due);

            var delete = Ui.Link("✕", 11);
            delete.Margin = new Thickness(10, 0, 0, 0);
            delete.VerticalAlignment = VerticalAlignment.Center;
            delete.MouseLeftButtonUp += (_, _) =>
            {
                ReminderStore.Remove(reminder.Id);
                RebuildReminderList();
            };
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            _reminderList.Children.Add(row);
        }
    }

    static string FormatDue(DateTime dueUtc)
    {
        var delta = dueUtc - DateTime.UtcNow;
        if (delta < TimeSpan.Zero) return "now";
        if (delta < TimeSpan.FromMinutes(60)) return $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        if (delta < TimeSpan.FromHours(24)) return $"in {delta.TotalHours:0.#}h";
        return dueUtc.ToLocalTime().ToString("ddd HH:mm");
    }

    public void ShowReminders()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowReminders);
            return;
        }
        RebuildReminderList();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_remindersPanel);
    }
}
