using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bridget.UI;

/// <summary>The flyout's Commands panel: one-click launches Bridget can also run by voice.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _commandsPanel;
    StackPanel _commandList = null!;
    TextBlock _addCommandLink = null!;
    List<BridgetCommand> _commands = new();
    int _editingCommand = -1;

    StackPanel BuildCommandsPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Commands", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Your one-click launches — a link, an app, a folder. Once Ask Bridget is set up, saying “open Salesforce” runs them too.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        _commandList = new StackPanel();
        var scroll = new ScrollViewer
        {
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _commandList,
            Margin = new Thickness(0, 2, 0, 0),
        };
        panel.Children.Add(scroll);

        _addCommandLink = Ui.Link("+ Add command", 11.5);
        _addCommandLink.Margin = new Thickness(2, 10, 2, 0);
        _addCommandLink.MouseLeftButtonUp += (_, _) =>
        {
            if (_commands.Count >= CommandStore.MaxCommands) return;
            _commands.Add(new BridgetCommand(Guid.NewGuid().ToString("n"), "", ""));
            _editingCommand = _commands.Count - 1;
            RebuildCommandList();
        };
        panel.Children.Add(_addCommandLink);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 12, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        return panel;
    }

    void RebuildCommandList()
    {
        _commandList.Children.Clear();
        if (_commands.Count == 0)
        {
            var empty = Ui.Text("Nothing yet. Add your go-to places — Salesforce, WhatsApp Web, your CRM — and open them in one click (or by asking).", 11, "TextSecondaryBrush");
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(2, 8, 0, 0);
            _commandList.Children.Add(empty);
        }
        for (var i = 0; i < _commands.Count; i++)
            _commandList.Children.Add(BuildCommandCard(i));
        _addCommandLink.Visibility =
            _commands.Count >= CommandStore.MaxCommands ? Visibility.Collapsed : Visibility.Visible;
    }

    Border BuildCommandCard(int index)
    {
        var command = _commands[index];
        var stack = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var c = 0; c < 4; c++)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = Ui.Text(command.Label.Length > 0 ? command.Label : "(untitled)", 12, "TextPrimaryBrush", FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);

        TextBlock Link(string text, int column, Action onClick, bool enabled = true)
        {
            var link = Ui.Text(text, 11, enabled ? "AccentBrush" : "TextSecondaryBrush", FontWeights.SemiBold);
            link.Margin = new Thickness(10, 0, 0, 0);
            link.VerticalAlignment = VerticalAlignment.Center;
            if (enabled)
            {
                link.Cursor = Cursors.Hand;
                link.MouseLeftButtonUp += (_, _) => onClick();
            }
            Grid.SetColumn(link, column);
            header.Children.Add(link);
            return link;
        }

        TextBlock? run = null;
        run = Link("Run", 1, () =>
        {
            run!.Text = CommandStore.Execute(command) ? "✓" : "failed";
            var revert = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                run.Text = "Run";
            };
            revert.Start();
        }, enabled: command.Target.Length > 0);
        Link("▲", 2, () => MoveCommand(index, -1), enabled: index > 0);
        Link("▼", 3, () => MoveCommand(index, +1), enabled: index < _commands.Count - 1);
        if (_editingCommand != index)
            Link("Edit", 4, () =>
            {
                _editingCommand = index;
                RebuildCommandList();
            });
        stack.Children.Add(header);

        if (_editingCommand == index)
        {
            var nameHint = Ui.Text("Name (what you'd say)", 10, "TextSecondaryBrush");
            nameHint.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(nameHint);
            var labelBox = Ui.TextBox(command.Label);
            labelBox.MaxLength = CommandStore.MaxLabelLength;
            labelBox.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(labelBox);

            var targetHint = Ui.Text("Link, app path or folder", 10, "TextSecondaryBrush");
            targetHint.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(targetHint);
            var targetBox = Ui.TextBox(command.Target);
            targetBox.MaxLength = CommandStore.MaxTargetLength;
            targetBox.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(targetBox);

            var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var delete = Ui.Text("Delete", 11, "TextSecondaryBrush", FontWeights.SemiBold);
            delete.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            delete.Cursor = Cursors.Hand;
            delete.MouseLeftButtonUp += (_, _) =>
            {
                _commands.RemoveAt(index);
                _editingCommand = -1;
                CommitCommands();
            };
            buttons.Children.Add(delete);

            var cancel = Ui.Text("Cancel", 11, "TextSecondaryBrush", FontWeights.SemiBold);
            cancel.Cursor = Cursors.Hand;
            cancel.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(cancel, 2);
            cancel.MouseLeftButtonUp += (_, _) =>
            {
                if (command.Label.Length == 0 && command.Target.Length == 0) _commands.RemoveAt(index);
                _editingCommand = -1;
                RebuildCommandList();
            };
            buttons.Children.Add(cancel);

            var save = Ui.Text("Save", 11, "AccentBrush", FontWeights.SemiBold);
            save.Cursor = Cursors.Hand;
            Grid.SetColumn(save, 3);
            save.MouseLeftButtonUp += (_, _) =>
            {
                _commands[index] = command with { Label = labelBox.Text.Trim(), Target = targetBox.Text.Trim() };
                _editingCommand = -1;
                CommitCommands();
            };
            buttons.Children.Add(save);

            stack.Children.Add(buttons);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        return card;
    }

    void MoveCommand(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _commands.Count) return;
        (_commands[index], _commands[target]) = (_commands[target], _commands[index]);
        _editingCommand = -1;
        CommitCommands();
    }

    void CommitCommands()
    {
        // On a failed write keep the in-memory edit visible for the session
        // instead of silently reverting to the on-disk state.
        if (CommandStore.Save(_commands)) _commands = CommandStore.Load();
        RebuildCommandList();
    }

    public void ShowCommands()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowCommands);
            return;
        }
        _commands = CommandStore.Load();
        _editingCommand = -1;
        RebuildCommandList();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_commandsPanel);
    }
}
