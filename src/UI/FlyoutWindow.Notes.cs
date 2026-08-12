using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Saley.Notes;

namespace Saley.UI;

/// <summary>The flyout's Call notes panel: opt-in, model download, key, list.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _notesPanel;
    PillSwitch _notesSwitch = null!;
    TextBlock _modelStatus = null!;
    TextBlock _modelAction = null!;
    Border _modelProgressTrack = null!;
    Border _modelProgressFill = null!;
    PasswordBox _keyBox = null!;
    TextBlock _keyStatus = null!;
    StackPanel _notesList = null!;

    StackPanel BuildNotesPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Call notes", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Records your calls, types them up, and writes 3 bullets + a next step. Recording is off until you turn it on; audio is deleted right after transcription.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        _notesSwitch = new PillSwitch(Settings.NotesEnabled);
        _notesSwitch.Toggled += on =>
        {
            Settings.NotesEnabled = on;
            if (on && !ModelStore.IsReady && !ModelStore.IsDownloading) ModelStore.StartDownload();
            UpdateModelRow();
        };
        var toggleRow = Ui.ToggleRow("Take notes on my calls", _notesSwitch);
        toggleRow.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(toggleRow);

        _modelStatus = Ui.Text("", 11, "TextSecondaryBrush");
        _modelStatus.TextWrapping = TextWrapping.Wrap;
        _modelStatus.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_modelStatus);

        _modelProgressFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _modelProgressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _modelProgressTrack = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _modelProgressFill,
        };
        _modelProgressTrack.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        panel.Children.Add(_modelProgressTrack);

        _modelAction = Ui.Link("Download model", 11);
        _modelAction.Margin = new Thickness(0, 6, 0, 0);
        _modelAction.MouseLeftButtonUp += (_, _) =>
        {
            if (ModelStore.IsDownloading) ModelStore.CancelDownload();
            else if (!ModelStore.IsReady) ModelStore.StartDownload();
            UpdateModelRow();
        };
        panel.Children.Add(_modelAction);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("AI SUMMARY (DEEPSEEK)", 10, "TextSecondaryBrush", FontWeights.SemiBold));
        var keyRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyBox = Ui.PasswordBox();
        keyRow.Children.Add(_keyBox);
        var saveKey = Ui.Link("Save", 11);
        saveKey.Margin = new Thickness(10, 0, 0, 0);
        saveKey.VerticalAlignment = VerticalAlignment.Center;
        saveKey.MouseLeftButtonUp += (_, _) =>
        {
            if (_keyBox.Password.Trim().Length == 0) return;
            Settings.DeepSeekKey = _keyBox.Password;
            _keyBox.Password = "";
            UpdateKeyStatus();
        };
        Grid.SetColumn(saveKey, 1);
        keyRow.Children.Add(saveKey);
        panel.Children.Add(keyRow);

        var keyStatusRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        keyStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyStatus = Ui.Text("", 10.5, "TextSecondaryBrush");
        _keyStatus.TextWrapping = TextWrapping.Wrap;
        keyStatusRow.Children.Add(_keyStatus);
        var removeKey = Ui.Link("Remove", 10.5);
        removeKey.Margin = new Thickness(10, 0, 0, 0);
        removeKey.MouseLeftButtonUp += (_, _) =>
        {
            Settings.DeepSeekKey = null;
            UpdateKeyStatus();
        };
        Grid.SetColumn(removeKey, 1);
        keyStatusRow.Children.Add(removeKey);
        panel.Children.Add(keyStatusRow);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("CALL LANGUAGE", 10, "TextSecondaryBrush", FontWeights.SemiBold));
        var lang = new Segmented("Hebrew", "Auto detect", Settings.NotesLanguage == "auto" ? 1 : 0)
        {
            Margin = new Thickness(0, 6, 0, 0),
        };
        lang.SelectionChanged += index => Settings.NotesLanguage = index == 1 ? "auto" : "he";
        panel.Children.Add(lang);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("RECENT NOTES", 10, "TextSecondaryBrush", FontWeights.SemiBold));
        _notesList = new StackPanel();
        var scroll = new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _notesList,
        };
        panel.Children.Add(scroll);

        var openFolder = Ui.Link("Open notes folder", 11);
        openFolder.Margin = new Thickness(2, 10, 2, 0);
        openFolder.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(NotesStore.NotesDir);
                Process.Start(new ProcessStartInfo(NotesStore.NotesDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write($"Open notes folder failed: {ex.Message}");
            }
        };
        panel.Children.Add(openFolder);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 12, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        ModelStore.Progress += (received, total) => Dispatcher.InvokeAsync(() => OnModelProgress(received, total));
        ModelStore.Completed += (_, _) => Dispatcher.InvokeAsync(UpdateModelRow);
        NotesStore.Changed += () => Dispatcher.InvokeAsync(RebuildNotesList);

        UpdateModelRow();
        UpdateKeyStatus();
        return panel;
    }

    void OnModelProgress(long received, long total)
    {
        _modelProgressTrack.Visibility = Visibility.Visible;
        if (total > 0)
        {
            var fraction = Math.Clamp((double)received / total, 0, 1);
            _modelProgressFill.Width = Math.Max(0, (_modelProgressTrack.ActualWidth - 2) * fraction);
            _modelStatus.Text = $"Downloading voice model… {received / 1_000_000} / {total / 1_000_000} MB";
        }
        else
        {
            _modelStatus.Text = $"Downloading voice model… {received / 1_000_000} MB";
        }
        _modelAction.Text = "Cancel";
    }

    void UpdateModelRow()
    {
        if (WhisperRuntime.UnavailableReason is { } reason)
        {
            _modelStatus.Text = reason;
            _modelAction.Visibility = Visibility.Collapsed;
            _modelProgressTrack.Visibility = Visibility.Collapsed;
            return;
        }
        if (ModelStore.IsReady)
        {
            _modelStatus.Text = "Voice model ready ✓";
            _modelAction.Visibility = Visibility.Collapsed;
            _modelProgressTrack.Visibility = Visibility.Collapsed;
        }
        else if (ModelStore.IsDownloading)
        {
            _modelStatus.Text = "Downloading voice model…";
            _modelAction.Text = "Cancel";
            _modelAction.Visibility = Visibility.Visible;
        }
        else
        {
            _modelStatus.Text = $"Voice model needed — one-time {ModelStore.DisplaySize} download.";
            _modelAction.Text = "Download model";
            _modelAction.Visibility = Visibility.Visible;
            _modelProgressTrack.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateKeyStatus()
    {
        _keyStatus.Text = Settings.DeepSeekKey is null
            ? "No key — notes will be transcript-only. Paste your DeepSeek key for AI summaries."
            : "Key saved ✓ — summaries on.";
    }

    void RebuildNotesList()
    {
        _notesList.Children.Clear();
        var notes = NotesStore.Load().OrderByDescending(n => n.StartedUtc).Take(10).ToList();
        if (notes.Count == 0)
        {
            var empty = Ui.Text("No notes yet — they'll appear here after your next call.", 11, "TextSecondaryBrush");
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(2, 8, 0, 0);
            _notesList.Children.Add(empty);
            return;
        }

        foreach (var note in notes)
        {
            var stack = new StackPanel();
            var local = note.StartedUtc.ToLocalTime();
            var header = Ui.Text(
                $"{local:HH:mm} · {Math.Max(1, note.DurationSec / 60)} min" +
                (note.State == "transcript-only" ? " · no summary" : note.State == "recovered" ? " · recovered" : ""),
                10.5, "TextSecondaryBrush", FontWeights.SemiBold);
            stack.Children.Add(header);

            var body = note.Summary ?? note.Transcript;
            var preview = Ui.Text(body.Length > 220 ? body[..220] + "…" : body, 11, "TextPrimaryBrush");
            preview.TextWrapping = TextWrapping.Wrap;
            preview.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(preview);

            var copy = Ui.Link("Copy", 10.5);
            copy.Margin = new Thickness(0, 6, 0, 0);
            copy.MouseLeftButtonUp += (_, _) =>
            {
                if (SnippetPaster.TrySetClipboard(note.Summary ?? note.Transcript)) copy.Text = "Copied ✓";
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
            _notesList.Children.Add(card);
        }
    }

    public void ShowNotes()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowNotes);
            return;
        }
        RebuildNotesList();
        UpdateModelRow();
        UpdateKeyStatus();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_notesPanel);
    }
}
