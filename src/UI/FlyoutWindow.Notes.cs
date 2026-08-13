using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    PasswordBox _groqKeyBox = null!;
    TextBlock _groqKeyStatus = null!;
    StackPanel _notesList = null!;
    Border _hotkeyBox = null!;
    TextBlock _hotkeyLabel = null!;
    TextBlock _hotkeyStatus = null!;
    PillSwitch _polishSwitch = null!;
    Grid _toneRow = null!;
    TextBlock _polishHint = null!;

    /// <summary>Set by Shell: re-registers the dock's global dictation
    /// hotkey from Settings; false when Windows refused the combo.</summary>
    public Func<bool>? ApplyDictationHotkey { get; set; }

    StackPanel BuildNotesPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Notes & dictation", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Records your calls, types them up, and writes 3 bullets + a next step. Recording is off until you turn it on; audio is deleted right after transcription.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        _notesSwitch = new PillSwitch(Settings.NotesEnabled);
        _notesSwitch.Toggled += on =>
        {
            Settings.NotesEnabled = on;
            // With a Groq key the model is only an optional offline backup —
            // don't force a 466 MB download (nor start one on a CPU that
            // can't run it).
            if (on && Settings.GroqKey is null && WhisperRuntime.EnsureLoaded() &&
                !ModelStore.IsReady && !ModelStore.IsDownloading)
                ModelStore.StartDownload();
            UpdateModelRow();
        };
        var toggleRow = Ui.ToggleRow("Take notes on my calls", _notesSwitch);
        toggleRow.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(toggleRow);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("FAST TRANSCRIPTION (GROQ)", 10, "TextSecondaryBrush", FontWeights.SemiBold));
        var groqRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        groqRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groqRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _groqKeyBox = Ui.PasswordBox();
        groqRow.Children.Add(_groqKeyBox);
        var saveGroq = Ui.Link("Save", 11);
        saveGroq.Margin = new Thickness(10, 0, 0, 0);
        saveGroq.VerticalAlignment = VerticalAlignment.Center;
        saveGroq.MouseLeftButtonUp += (_, _) =>
        {
            if (_groqKeyBox.Password.Trim().Length == 0) return;
            Settings.GroqKey = _groqKeyBox.Password;
            _groqKeyBox.Password = "";
            UpdateGroqStatus();
            UpdateModelRow();
        };
        Grid.SetColumn(saveGroq, 1);
        groqRow.Children.Add(saveGroq);
        panel.Children.Add(groqRow);

        var groqStatusRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        groqStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groqStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _groqKeyStatus = Ui.Text("", 10.5, "TextSecondaryBrush");
        _groqKeyStatus.TextWrapping = TextWrapping.Wrap;
        groqStatusRow.Children.Add(_groqKeyStatus);
        var removeGroq = Ui.Link("Remove", 10.5);
        removeGroq.Margin = new Thickness(10, 0, 0, 0);
        removeGroq.MouseLeftButtonUp += (_, _) =>
        {
            Settings.GroqKey = null;
            UpdateGroqStatus();
            UpdateModelRow();
        };
        Grid.SetColumn(removeGroq, 1);
        groqStatusRow.Children.Add(removeGroq);
        panel.Children.Add(groqStatusRow);

        panel.Children.Add(Ui.Divider(12, 2));

        var backupCaption = Ui.Text("OFFLINE BACKUP (ON-DEVICE MODEL)", 10, "TextSecondaryBrush", FontWeights.SemiBold);
        backupCaption.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(backupCaption);

        _modelStatus = Ui.Text("", 11, "TextSecondaryBrush");
        _modelStatus.TextWrapping = TextWrapping.Wrap;
        _modelStatus.Margin = new Thickness(0, 6, 0, 0);
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

        panel.Children.Add(Ui.Text("DICTATION", 10, "TextSecondaryBrush", FontWeights.SemiBold));

        var hotkeyRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _hotkeyLabel = Ui.Text("", 11.5, "TextPrimaryBrush", FontWeights.SemiBold);
        _hotkeyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _hotkeyBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 7),
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = _hotkeyLabel,
        };
        _hotkeyBox.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(_hotkeyBox);
        _hotkeyBox.MouseLeftButtonUp += (_, _) => Keyboard.Focus(_hotkeyBox);
        _hotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            _hotkeyLabel.Text = "Press a key combo…";
            SetHotkeyStatus("Esc cancels. Include Ctrl, Alt or Win.", warn: false);
        };
        _hotkeyBox.LostKeyboardFocus += (_, _) => RefreshHotkeyRow();
        _hotkeyBox.PreviewKeyDown += OnHotkeyCapture;
        hotkeyRow.Children.Add(_hotkeyBox);

        var resetHotkey = Ui.Link("Reset", 10.5);
        resetHotkey.Margin = new Thickness(10, 0, 0, 0);
        resetHotkey.VerticalAlignment = VerticalAlignment.Center;
        resetHotkey.MouseLeftButtonUp += (_, _) => SaveHotkey(Hotkey.Default);
        Grid.SetColumn(resetHotkey, 1);
        hotkeyRow.Children.Add(resetHotkey);

        var offHotkey = Ui.Link("Turn off", 10.5);
        offHotkey.Margin = new Thickness(10, 0, 0, 0);
        offHotkey.VerticalAlignment = VerticalAlignment.Center;
        offHotkey.MouseLeftButtonUp += (_, _) => SaveHotkey(Hotkey.Off);
        Grid.SetColumn(offHotkey, 2);
        hotkeyRow.Children.Add(offHotkey);
        panel.Children.Add(hotkeyRow);

        _hotkeyStatus = Ui.Text("Click the box, then press the combo you want for dictation.", 10.5, "TextSecondaryBrush");
        _hotkeyStatus.TextWrapping = TextWrapping.Wrap;
        _hotkeyStatus.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_hotkeyStatus);

        _polishSwitch = new PillSwitch(Settings.DictationPolish);
        _polishSwitch.Toggled += on =>
        {
            Settings.DictationPolish = on;
            UpdatePolishRows();
        };
        var polishRow = Ui.ToggleRow("Polish dictation with AI", _polishSwitch);
        polishRow.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(polishRow);

        var toneSwitch = new PillSwitch(Settings.DictationProfessional);
        toneSwitch.Toggled += on => Settings.DictationProfessional = on;
        _toneRow = Ui.ToggleRow("Professional tone", toneSwitch);
        _toneRow.Margin = new Thickness(14, 8, 0, 0); // indented under the polish toggle
        panel.Children.Add(_toneRow);

        _polishHint = Ui.Text("", 10.5, "TextSecondaryBrush");
        _polishHint.TextWrapping = TextWrapping.Wrap;
        _polishHint.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_polishHint);

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
        UpdateGroqStatus();
        RefreshHotkeyRow();
        UpdatePolishRows();
        return panel;
    }

    void UpdatePolishRows()
    {
        var on = Settings.DictationPolish;
        _toneRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _polishHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _polishHint.Text = Settings.DeepSeekKey is null
                ? "Add a DeepSeek key above — until then the raw text is typed."
                : "Fillers and punctuation are cleaned up (via DeepSeek) before typing.";
        }
    }

    void OnHotkeyCapture(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus(); // LostKeyboardFocus restores the label
            return;
        }
        if (Hotkey.IsModifierKey(key)) return; // mid-combo — keep waiting
        if (Hotkey.FromKeyEvent(e) is not { } combo)
        {
            SetHotkeyStatus("Include Ctrl, Alt or Win in the combo.", warn: true);
            return;
        }
        Keyboard.ClearFocus();
        SaveHotkey(combo);
    }

    void SaveHotkey(Hotkey combo)
    {
        var previous = Settings.DictationHotkey;
        Settings.DictationHotkey = combo.Serialize();
        if (ApplyDictationHotkey?.Invoke() ?? true)
        {
            SetHotkeyStatus(combo.IsOff
                ? "Hotkey off — dictate with the 🎙 chip in the dock."
                : $"Saved ✓ — press {combo} anywhere to dictate.", warn: false);
        }
        else
        {
            // Windows refused the combo (another app owns it) — keep the old one.
            Settings.DictationHotkey = previous;
            ApplyDictationHotkey?.Invoke();
            SetHotkeyStatus($"{combo} is taken by another app — kept {Hotkey.LoadDictation()}.", warn: true);
        }
        RefreshHotkeyRow();
    }

    void SetHotkeyStatus(string text, bool warn)
    {
        _hotkeyStatus.Text = text;
        _hotkeyStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "AmberBrush" : "TextSecondaryBrush");
    }

    void RefreshHotkeyRow()
    {
        var combo = Hotkey.LoadDictation();
        _hotkeyLabel.Text = combo.IsOff ? "Off — click to set" : combo.ToString();
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
            _modelStatus.Text = Settings.GroqKey is null
                ? $"Voice model needed — one-time {ModelStore.DisplaySize} download."
                : $"Optional — used when Groq is unreachable ({ModelStore.DisplaySize}).";
            _modelAction.Text = "Download model";
            _modelAction.Visibility = Visibility.Visible;
            _modelProgressTrack.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateGroqStatus()
    {
        _groqKeyStatus.Text = Settings.GroqKey is null
            ? "No key — using the on-device model. Free key at console.groq.com."
            : "Key saved ✓ — fast Hebrew transcription on (call audio is sent to Groq).";
    }

    void UpdateKeyStatus()
    {
        _keyStatus.Text = Settings.DeepSeekKey is null
            ? "No key — notes will be transcript-only. Paste your DeepSeek key for AI summaries."
            : "Key saved ✓ — summaries on.";
        if (_polishHint is not null) UpdatePolishRows(); // key row is built before the dictation section
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
        UpdateGroqStatus();
        RefreshHotkeyRow();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_notesPanel);
    }
}
