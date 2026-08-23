using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Palon.Notes;

namespace Palon.UI;

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
    PasswordBox _geminiKeyBox = null!;
    TextBlock _geminiKeyStatus = null!;
    PasswordBox _groqKeyBox = null!;
    TextBlock _groqKeyStatus = null!;
    StackPanel _notesList = null!;
    TextBlock _notesHealth = null!;
    TextBlock _notesHealthLink = null!;
    Border _hotkeyBox = null!;
    TextBlock _hotkeyLabel = null!;
    TextBlock _hotkeyStatus = null!;
    PillSwitch _polishSwitch = null!;
    Grid _toneRow = null!;
    TextBlock _polishHint = null!;
    readonly Dictionary<string, string> _followUps = new(); // note id → drafted follow-up (session-only)

    /// <summary>Set by Shell: re-registers the dock's global dictation
    /// hotkey from Settings; false when Windows refused the combo.</summary>
    public Func<bool>? ApplyDictationHotkey { get; set; }

    /// <summary>Set by Shell: releases all of the dock's global hotkeys so
    /// the capture box can receive combos Palon itself owns.</summary>
    public Action? SuspendGlobalHotkeys { get; set; }

    StackPanel BuildNotesPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Title("Notes & dictation"));
        var subtitle = Ui.Small("Records your calls, types them up, and writes 3 bullets + a next step. Recording is off until you turn it on; audio is deleted right after transcription.");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = Ui.Top(Space.Tight);
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
        toggleRow.Margin = Ui.Top(Space.Section);
        panel.Children.Add(toggleRow);

        // The health line turns "notes stopped working" into a named cause.
        _notesHealth = Ui.Small("");
        _notesHealth.TextWrapping = TextWrapping.Wrap;
        _notesHealth.Margin = Ui.Top(Space.Row);
        panel.Children.Add(_notesHealth);

        _notesHealthLink = Ui.Link("No recent calls seen — check the handlers point at Palon.exe →", Font.Caption);
        _notesHealthLink.TextWrapping = TextWrapping.Wrap;
        _notesHealthLink.Margin = Ui.Top(Space.Tight);
        _notesHealthLink.Visibility = Visibility.Collapsed;
        _notesHealthLink.MouseLeftButtonUp += (_, _) => ShowSoftphoneSetup();
        panel.Children.Add(_notesHealthLink);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        // The notes themselves come first — they're what this panel is for;
        // transcription/key plumbing lives below.
        panel.Children.Add(Ui.Caption("RECENT NOTES"));
        _notesList = new StackPanel();
        var scroll = Ui.ThinScroll(new ScrollViewer
        {
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _notesList,
        });
        panel.Children.Add(scroll);

        var openFolder = Ui.Link("Open notes folder", Font.Small);
        openFolder.Margin = new Thickness(2, Space.Row, 2, 0);
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

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("FAST TRANSCRIPTION (GROQ)"));
        _groqKeyBox = Ui.PasswordBox();
        var groq = Ui.KeyRow(_groqKeyBox,
            onSave: key =>
            {
                Settings.GroqKey = key;
                UpdateGroqStatus();
                UpdateModelRow();
            },
            onRemove: () =>
            {
                Settings.GroqKey = null;
                UpdateGroqStatus();
                UpdateModelRow();
            });
        _groqKeyStatus = groq.Status;
        panel.Children.Add(groq.InputRow);
        panel.Children.Add(groq.StatusRow);

        panel.Children.Add(Ui.Divider(Space.Section, 2));

        var backupCaption = Ui.Caption("OFFLINE BACKUP (ON-DEVICE MODEL)");
        backupCaption.Margin = Ui.Top(Space.Row);
        panel.Children.Add(backupCaption);

        _modelStatus = Ui.Small("");
        _modelStatus.TextWrapping = TextWrapping.Wrap;
        _modelStatus.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_modelStatus);

        _modelProgressFill = new Border { CornerRadius = new CornerRadius(Radius.Hairline), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _modelProgressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        _modelProgressTrack = new Border
        {
            Height = 6,
            CornerRadius = new CornerRadius(Radius.Hairline),
            Margin = Ui.Top(Space.Tight),
            Visibility = Visibility.Collapsed,
            Child = _modelProgressFill,
        };
        _modelProgressTrack.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        panel.Children.Add(_modelProgressTrack);

        _modelAction = Ui.Link("Download model", Font.Small);
        _modelAction.Margin = Ui.Top(Space.Tight);
        _modelAction.MouseLeftButtonUp += (_, _) =>
        {
            if (ModelStore.IsDownloading) ModelStore.CancelDownload();
            else if (!ModelStore.IsReady) ModelStore.StartDownload();
            UpdateModelRow();
        };
        panel.Children.Add(_modelAction);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("AI BRAIN (GEMINI / DEEPSEEK)"));

        _geminiKeyBox = Ui.PasswordBox();
        var gemini = Ui.KeyRow(_geminiKeyBox,
            onSave: key =>
            {
                Settings.GeminiKey = key;
                UpdateKeyStatus();
            },
            onRemove: () =>
            {
                Settings.GeminiKey = null;
                UpdateKeyStatus();
            });
        _geminiKeyStatus = gemini.Status;
        panel.Children.Add(gemini.InputRow);
        panel.Children.Add(gemini.StatusRow);

        var deepSeekCaption = Ui.Caption("DeepSeek (fallback)");
        deepSeekCaption.Margin = Ui.Top(Space.Row);
        panel.Children.Add(deepSeekCaption);
        _keyBox = Ui.PasswordBox();
        var deepSeek = Ui.KeyRow(_keyBox,
            onSave: key =>
            {
                Settings.DeepSeekKey = key;
                UpdateKeyStatus();
            },
            onRemove: () =>
            {
                Settings.DeepSeekKey = null;
                UpdateKeyStatus();
            });
        _keyStatus = deepSeek.Status;
        panel.Children.Add(deepSeek.InputRow);
        panel.Children.Add(deepSeek.StatusRow);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("CALL LANGUAGE"));
        var lang = new Segmented("Hebrew", "Auto detect", Settings.NotesLanguage == "auto" ? 1 : 0)
        {
            Margin = Ui.Top(Space.Tight),
        };
        lang.SelectionChanged += index => Settings.NotesLanguage = index == 1 ? "auto" : "he";
        panel.Children.Add(lang);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("DICTATION"));

        var hotkeyRow = new Grid { Margin = Ui.Top(Space.Tight) };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _hotkeyLabel = Ui.Text("", Font.Body, "TextPrimaryBrush", FontWeights.SemiBold);
        _hotkeyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _hotkeyBox = new Border
        {
            CornerRadius = new CornerRadius(Radius.Control),
            Padding = new Thickness(10, 6, 10, 6),
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = _hotkeyLabel,
        };
        _hotkeyBox.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.SetNoDrag(_hotkeyBox, true); // a shaky click must arm capture, not drag the card
        Ui.HoverFill(_hotkeyBox);
        _hotkeyBox.MouseLeftButtonUp += (_, _) => Keyboard.Focus(_hotkeyBox);
        _hotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            // Release Palon's own hotkeys so pressing e.g. the current combo
            // reaches the capture box instead of starting a dictation.
            SuspendGlobalHotkeys?.Invoke();
            _hotkeyLabel.Text = "Press a key combo…";
            SetHotkeyStatus("Esc cancels. Include Ctrl, Alt or Win.", warn: false);
        };
        _hotkeyBox.LostKeyboardFocus += (_, _) =>
        {
            RestoreGlobalHotkeys();
            RefreshHotkeyRow();
        };
        _hotkeyBox.PreviewKeyDown += OnHotkeyCapture;
        hotkeyRow.Children.Add(_hotkeyBox);

        var resetHotkey = Ui.Link("Reset", Font.Caption);
        resetHotkey.Margin = Ui.Left(Space.Row);
        resetHotkey.VerticalAlignment = VerticalAlignment.Center;
        resetHotkey.MouseLeftButtonUp += (_, _) => SaveHotkey(Hotkey.Default);
        Grid.SetColumn(resetHotkey, 1);
        hotkeyRow.Children.Add(resetHotkey);

        var offHotkey = Ui.Link("Turn off", Font.Caption);
        offHotkey.Margin = Ui.Left(Space.Row);
        offHotkey.VerticalAlignment = VerticalAlignment.Center;
        offHotkey.MouseLeftButtonUp += (_, _) => SaveHotkey(Hotkey.Off);
        Grid.SetColumn(offHotkey, 2);
        hotkeyRow.Children.Add(offHotkey);
        panel.Children.Add(hotkeyRow);

        _hotkeyStatus = Ui.Small("Click the box, then press the combo you want for dictation.");
        _hotkeyStatus.TextWrapping = TextWrapping.Wrap;
        _hotkeyStatus.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_hotkeyStatus);

        _polishSwitch = new PillSwitch(Settings.DictationPolish);
        _polishSwitch.Toggled += on =>
        {
            Settings.DictationPolish = on;
            UpdatePolishRows();
        };
        var polishRow = Ui.ToggleRow("Polish dictation with AI", _polishSwitch);
        polishRow.Margin = Ui.Top(Space.Section);
        panel.Children.Add(polishRow);

        var toneSwitch = new PillSwitch(Settings.DictationProfessional);
        toneSwitch.Toggled += on => Settings.DictationProfessional = on;
        _toneRow = Ui.ToggleRow("Professional tone", toneSwitch);
        _toneRow.Margin = new Thickness(Space.Block, Space.Row, 0, 0); // indented under the polish toggle
        panel.Children.Add(_toneRow);

        _polishHint = Ui.Small("");
        _polishHint.TextWrapping = TextWrapping.Wrap;
        _polishHint.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_polishHint);

        var done = Ui.PrimaryButton("Done");
        done.Margin = Ui.Top(Space.Section);
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
        UpdateNotesHealth();
        return panel;
    }

    void UpdatePolishRows()
    {
        var on = Settings.DictationPolish;
        _toneRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _polishHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _polishHint.Text = AiChat.HasKey
                ? "Fillers and punctuation are cleaned up by the AI before typing."
                : "Add a Gemini or DeepSeek key above — until then the raw text is typed.";
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

    void UpdateNotesHealth()
    {
        var lastNote = NotesStore.Load().MaxBy(n => n.StartedUtc);
        var lastCall = CallStatsStore.Load().MaxBy(c => c.StartedUtc);
        static string When(DateTime? utc) =>
            utc is { } u ? u.ToLocalTime().ToString("d MMM HH:mm") : "never";
        _notesHealth.Text =
            $"Last note: {When(lastNote?.StartedUtc)} · Last call Palon saw: {When(lastCall?.StartedUtc)}";

        // Palon not seeing calls is the handler-misconfiguration signature —
        // but only once there's history to compare against (calls seen before
        // and gone quiet, or migrated notes with no call ever seen since).
        // A fresh install that simply hasn't had a call yet stays calm.
        var stale = lastCall is null
            ? lastNote is not null
            : lastCall.StartedUtc < DateTime.UtcNow.AddDays(-1);
        var warn = stale && Settings.Trigger == TriggerMode.SoftphoneEvents;
        _notesHealthLink.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        _notesHealth.SetResourceReference(TextBlock.ForegroundProperty,
            warn ? "AmberBrush" : "TextSecondaryBrush");
    }

    void UpdateGroqStatus()
    {
        _groqKeyStatus.Text = Settings.GroqKey is null
            ? "No key — using the on-device model. Free key at console.groq.com."
            : "Key saved ✓ — fast Hebrew transcription on (call audio is sent to Groq).";
    }

    void UpdateKeyStatus()
    {
        _geminiKeyStatus.Text = Settings.GeminiKey is null
            ? "No key — free at aistudio.google.com (Flash tier, ~1,500 calls/day)."
            : "Key saved ✓ — Gemini is the primary AI.";
        _keyStatus.Text = Settings.DeepSeekKey is null
            ? Settings.GeminiKey is null
                ? "No key — without any AI key, notes are transcript-only."
                : "No key — optional paid fallback."
            : Settings.GeminiKey is null
                ? "Key saved ✓ — DeepSeek is the AI."
                : "Key saved ✓ — fallback after Gemini.";
        if (_polishHint is not null) UpdatePolishRows(); // key rows are built before the dictation section
    }

    void RebuildNotesList()
    {
        _notesList.Children.Clear();
        var notes = NotesStore.Load().OrderByDescending(n => n.StartedUtc).Take(10).ToList();
        if (notes.Count == 0)
        {
            _notesList.Children.Add(Ui.EmptyState("No notes yet — they'll appear here after your next call."));
            return;
        }

        foreach (var note in notes)
        {
            var stack = new StackPanel();
            var local = note.StartedUtc.ToLocalTime();
            var header = Ui.Caption(
                $"{local:HH:mm} · {Math.Max(1, note.DurationSec / 60)} min" +
                (note.Number is { } number ? $" · {number}" : "") +
                (note.State == "transcript-only" ? " · no summary" : note.State == "recovered" ? " · recovered" : ""));
            stack.Children.Add(header);

            var body = note.Summary ?? note.Transcript;
            var preview = Ui.Small(body.Length > 220 ? body[..220] + "…" : body, "TextPrimaryBrush");
            preview.TextWrapping = TextWrapping.Wrap;
            preview.Margin = Ui.Top(Space.Tight);
            stack.Children.Add(preview);

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = Ui.Top(Space.Tight) };
            links.Children.Add(Ui.CopyLink(() => note.Summary ?? note.Transcript));

            var followHost = new StackPanel();
            if (AiChat.HasKey)
            {
                var follow = Ui.Link("✨ Follow-up", Font.Caption);
                follow.Margin = Ui.Left(Space.Section);
                var busy = false;
                follow.MouseLeftButtonUp += async (_, _) =>
                {
                    if (busy || !AiChat.HasKey) return;
                    busy = true;
                    follow.Text = "Writing…";
                    var text = await AiChat.FollowUpAsync(note.Summary ?? note.Transcript, CancellationToken.None);
                    busy = false;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // Lingers long enough to read, then the link recovers.
                        Ui.Flash(follow, "Follow-up failed — see log", "✨ Follow-up", Motion.Linger);
                        return;
                    }
                    follow.Text = "✨ Follow-up";
                    _followUps[note.Id] = text;
                    RenderAiDraft(followHost, text, note.Number, nested: true);
                };
                links.Children.Add(follow);
            }
            stack.Children.Add(links);
            stack.Children.Add(followHost);
            if (_followUps.TryGetValue(note.Id, out var cached)) RenderAiDraft(followHost, cached, note.Number, nested: true);

            _notesList.Children.Add(Ui.Card(stack));
        }
    }

    /// <summary>An AI-written draft (follow-up or recap): body + Copy, plus
    /// Open-in-WhatsApp when a number is known. Nested drafts sit inside a
    /// note card, so they get the inner-box treatment instead of a card.</summary>
    static void RenderAiDraft(Panel host, string text, string? number, bool nested)
    {
        host.Children.Clear();
        var inner = new StackPanel();
        var body = Ui.Small(text, "TextPrimaryBrush");
        body.TextWrapping = TextWrapping.Wrap;
        inner.Children.Add(body);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = Ui.Top(Space.Tight) };
        links.Children.Add(Ui.CopyLink(() => text));
        // The number came from the softphone handler — one tap sends the
        // draft where it belongs.
        if (Phones.WaMeUrl(number, text) is { } waMe)
        {
            var whatsApp = Ui.Link("Open in WhatsApp", Font.Caption);
            whatsApp.Margin = Ui.Left(Space.Section);
            whatsApp.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(waMe) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Write($"Open WhatsApp failed: {ex.Message}");
                }
            };
            links.Children.Add(whatsApp);
        }
        inner.Children.Add(links);
        if (!nested)
        {
            host.Children.Add(Ui.Card(inner));
            return;
        }
        var box = new Border
        {
            CornerRadius = new CornerRadius(Radius.Control),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = Ui.Top(Space.Row),
            BorderThickness = new Thickness(1),
            Child = inner,
        };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeBrush");
        host.Children.Add(box);
    }

    public void ShowNotes()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowNotes);
            return;
        }
        RebuildNotesList();
        Ui.StaggerIn(_notesList);
        UpdateModelRow();
        UpdateKeyStatus();
        UpdateGroqStatus();
        RefreshHotkeyRow();
        UpdateNotesHealth();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_notesPanel);
    }
}
