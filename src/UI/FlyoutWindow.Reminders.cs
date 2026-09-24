using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Palon.UI;

/// <summary>The flyout's Reminders panel: quick-add + open callbacks, grouped.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _remindersPanel;
    TextBox _reminderUrlBox = null!;
    TextBox _reminderLabelBox = null!;
    TextBox _reminderNameBox = null!;
    TextBox _reminderPhoneBox = null!;
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

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Title("Reminders"));
        var subtitle = Ui.Small("Who to call and what about (a link is optional), pick a time — Palon pops it above the taskbar when it's time to call.");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(subtitle);

        var whoCaption = Ui.Caption("Who (optional — name, phone)");
        whoCaption.Margin = new Thickness(2, Space.Section, 0, 0);
        panel.Children.Add(whoCaption);
        var whoRow = new Grid { Margin = Ui.Top(Space.Tight) };
        whoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        whoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _reminderNameBox = Ui.TextBox("");
        _reminderNameBox.MaxLength = 60;
        whoRow.Children.Add(_reminderNameBox);
        _reminderPhoneBox = Ui.TextBox("");
        _reminderPhoneBox.MaxLength = 32;
        _reminderPhoneBox.Margin = Ui.Left(Space.Row);
        Grid.SetColumn(_reminderPhoneBox, 1);
        whoRow.Children.Add(_reminderPhoneBox);
        panel.Children.Add(whoRow);

        var urlCaption = Ui.Caption("Link (optional — CRM, WhatsApp, anything)");
        urlCaption.Margin = new Thickness(2, Space.Row, 0, 0);
        panel.Children.Add(urlCaption);
        _reminderUrlBox = Ui.TextBox("");
        _reminderUrlBox.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_reminderUrlBox);

        var labelCaption = Ui.Caption("Note (what to do / what was discussed)");
        labelCaption.Margin = new Thickness(2, Space.Row, 0, 0);
        panel.Children.Add(labelCaption);
        _reminderLabelBox = Ui.TextBox("");
        _reminderLabelBox.MaxLength = 500;
        _reminderLabelBox.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_reminderLabelBox);

        var chipRow = new WrapPanel { Margin = Ui.Top(Space.Row) };
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
        _reminderCustomTimeBox.Margin = Ui.Top(Space.Row);
        _reminderCustomTimeBox.Visibility = Visibility.Collapsed;
        panel.Children.Add(_reminderCustomTimeBox);

        var add = Ui.PrimaryButton("Set reminder");
        add.Margin = Ui.Top(Space.Section);
        add.MouseLeftButtonUp += (_, _) => AddReminder();
        panel.Children.Add(add);

        _reminderStatus = Ui.Small("");
        _reminderStatus.Margin = new Thickness(2, Space.Tight, 2, 0);
        _reminderStatus.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_reminderStatus);

        panel.Children.Add(Ui.Divider(12, 4));

        _reminderList = new StackPanel();
        var scroll = Ui.ChainWheel(Ui.ThinScroll(new ScrollViewer
        {
            MaxHeight = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _reminderList,
        }));
        panel.Children.Add(scroll);

        var done = Ui.PrimaryButton("Done");
        done.Margin = Ui.Top(Space.Section);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        SelectTimeChip(_timeSelection);
        // Keep the list live while dock/scheduler actions mutate the store.
        CallbackStore.Changed += () => Dispatcher.InvokeAsync(() =>
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
            FontSize = Font.Small,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(Radius.Card),
            Padding = Pad.Chip,
            Margin = new Thickness(0, 0, Space.Row, Space.Row),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = label,
        };
        Ui.SetNoDrag(chip, true); // press-slide picks the time, not the window position
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
        var phone = _reminderPhoneBox.Text.Trim();
        if (phone.Length > 0 && CallbackStore.NormalizePhone(phone) is null)
        {
            ShowReminderStatus("That doesn't look like a phone number.");
            return;
        }
        if (!CallbackStore.IsValid(url, _reminderLabelBox.Text, _reminderNameBox.Text, phone))
        {
            ShowReminderStatus(url.Length > 0
                ? "That doesn't look like a link — paste a full http(s) address."
                : "Add a name, number or note so you'll know who to call.");
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

        if (CallbackStore.Add(_reminderLabelBox.Text, dueUtc, _reminderNameBox.Text, phone, url) is null)
        {
            ShowReminderStatus("Couldn't save the reminder (too many pending?).");
            return;
        }
        _reminderUrlBox.Text = "";
        _reminderLabelBox.Text = "";
        _reminderNameBox.Text = "";
        _reminderPhoneBox.Text = "";
        ShowReminderStatus("Reminder set ✓");
        RebuildReminderList();
    }

    void ShowReminderStatus(string text)
    {
        _reminderStatus.Text = text;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Linger) };
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
        var groups = CallbackPlanner.Group(CallbackStore.Load(), DateTime.Now);

        if (groups.Count == 0)
        {
            _reminderList.Children.Add(Ui.EmptyState("Nothing pending — set one and it pops above the taskbar on time."));
            return;
        }

        foreach (var (group, items) in groups)
        {
            var header = Ui.Caption($"{CallbackPlanner.GroupTitle(group)} · {items.Count}");
            header.Margin = new Thickness(2, Space.Row, 0, 0);
            _reminderList.Children.Add(header);
            foreach (var reminder in items) _reminderList.Children.Add(ReminderRow(reminder));
        }
    }

    /// <summary>One callback: "name · note", due, ✓ done, ✕ cancel.</summary>
    Grid ReminderRow(Callback reminder)
    {
        var row = new Grid { Margin = Ui.Top(Space.Row) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = Ui.AlignByScript(Ui.Text(reminder.DisplayLine, Font.Body, "TextPrimaryBrush", FontWeights.SemiBold));
        label.TextTrimming = TextTrimming.CharacterEllipsis; // RTL flow puts the ellipsis at the run's logical end
        label.VerticalAlignment = VerticalAlignment.Center;
        if (reminder.HasPhone) label.ToolTip = reminder.Phone;
        row.Children.Add(label);

        var due = Ui.Small(FormatDue(reminder.DueAtUtc));
        due.VerticalAlignment = VerticalAlignment.Center;
        due.Margin = Ui.Left(Space.Row);
        Grid.SetColumn(due, 1);
        row.Children.Add(due);

        var done = Ui.Link("✓", Font.Small);
        done.ToolTip = "Done";
        done.Margin = Ui.Left(Space.Row);
        done.VerticalAlignment = VerticalAlignment.Center;
        done.MouseLeftButtonUp += (_, _) =>
        {
            CallbackStore.Mutate(reminder.Id, c => CallbackPlanner.MarkDone(c, DateTime.UtcNow));
            RebuildReminderList();
        };
        Grid.SetColumn(done, 2);
        row.Children.Add(done);

        var cancel = Ui.Link("✕", Font.Small);
        cancel.ToolTip = "Cancel";
        cancel.Margin = Ui.Left(Space.Row);
        cancel.VerticalAlignment = VerticalAlignment.Center;
        cancel.MouseLeftButtonUp += (_, _) =>
        {
            CallbackStore.Mutate(reminder.Id, c => CallbackPlanner.Cancel(c, DateTime.UtcNow));
            RebuildReminderList();
        };
        Grid.SetColumn(cancel, 3);
        row.Children.Add(cancel);

        return row;
    }

    static string FormatDue(DateTime dueUtc)
    {
        var delta = dueUtc - DateTime.UtcNow;
        if (delta < -TimeSpan.FromMinutes(1)) return dueUtc.ToLocalTime().ToString("ddd HH:mm"); // overdue: when it was
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
        Ui.StaggerIn(_reminderList);
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_remindersPanel);
    }
}
