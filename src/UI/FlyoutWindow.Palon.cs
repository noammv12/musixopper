using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Palon.Notes;

namespace Palon.UI;

/// <summary>The flyout's Ask Palon panel: hotkey, voice toggle, last answer.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _palonPanel;
    TextBlock _palonKeyHint = null!;
    Border _asstHotkeyBox = null!;
    TextBlock _asstHotkeyLabel = null!;
    TextBlock _asstHotkeyStatus = null!;
    TextBlock _voiceStatus = null!;
    PasswordBox _elevenKeyBox = null!;
    TextBlock _elevenKeyStatus = null!;
    TextBlock _lastQuestion = null!;
    TextBlock _lastAnswer = null!;
    TextBlock _copyAnswerLink = null!;
    Border _exchangeCard = null!;
    TextBlock _exchangeEmpty = null!;

    /// <summary>Set by Shell: re-registers the dock's Ask-Palon hotkey.</summary>
    public Func<bool>? ApplyAssistantHotkey { get; set; }

    /// <summary>Set by Shell: speaks a short sample line with the current voice.</summary>
    public Func<Task>? PreviewVoice { get; set; }

    StackPanel BuildPalonPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Title("Ask Palon"));
        var subtitle = Ui.Small("Press the hotkey (or the 💬 chip), ask out loud — Palon answers back, opens your commands, sets reminders, and digs through your call notes.");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(subtitle);

        _palonKeyHint = Ui.Caption("Needs a Gemini or DeepSeek key — paste one under Notes & dictation.", "AmberBrush");
        _palonKeyHint.TextWrapping = TextWrapping.Wrap;
        _palonKeyHint.Margin = Ui.Top(Space.Row);
        panel.Children.Add(_palonKeyHint);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("ASSISTANT HOTKEY"));

        var hotkeyRow = new Grid { Margin = Ui.Top(Space.Tight) };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _asstHotkeyLabel = Ui.Text("", Font.Body, "TextPrimaryBrush", FontWeights.SemiBold);
        _asstHotkeyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _asstHotkeyBox = new Border
        {
            CornerRadius = new CornerRadius(Radius.Control),
            Padding = new Thickness(10, 6, 10, 6),
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = _asstHotkeyLabel,
        };
        _asstHotkeyBox.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(_asstHotkeyBox);
        _asstHotkeyBox.MouseLeftButtonUp += (_, _) => Keyboard.Focus(_asstHotkeyBox);
        _asstHotkeyBox.GotKeyboardFocus += (_, _) =>
        {
            SuspendGlobalHotkeys?.Invoke();
            _asstHotkeyLabel.Text = "Press a key combo…";
            SetAssistantHotkeyStatus("Esc cancels. Include Ctrl, Alt or Win.", warn: false);
        };
        _asstHotkeyBox.LostKeyboardFocus += (_, _) =>
        {
            RestoreGlobalHotkeys();
            RefreshAssistantHotkeyRow();
        };
        _asstHotkeyBox.PreviewKeyDown += OnAssistantHotkeyCapture;
        hotkeyRow.Children.Add(_asstHotkeyBox);

        var reset = Ui.Link("Reset", Font.Caption);
        reset.Margin = Ui.Left(Space.Row);
        reset.VerticalAlignment = VerticalAlignment.Center;
        reset.MouseLeftButtonUp += (_, _) => SaveAssistantHotkey(Hotkey.AssistantDefault);
        Grid.SetColumn(reset, 1);
        hotkeyRow.Children.Add(reset);

        var off = Ui.Link("Turn off", Font.Caption);
        off.Margin = Ui.Left(Space.Row);
        off.VerticalAlignment = VerticalAlignment.Center;
        off.MouseLeftButtonUp += (_, _) => SaveAssistantHotkey(Hotkey.Off);
        Grid.SetColumn(off, 2);
        hotkeyRow.Children.Add(off);
        panel.Children.Add(hotkeyRow);

        _asstHotkeyStatus = Ui.Small("Click the box, then press the combo you want.");
        _asstHotkeyStatus.TextWrapping = TextWrapping.Wrap;
        _asstHotkeyStatus.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_asstHotkeyStatus);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("LISTENING"));

        var autoStopSwitch = new PillSwitch(Settings.AssistantAutoStop);
        autoStopSwitch.Toggled += on => Settings.AssistantAutoStop = on;
        var autoStopRow = Ui.ToggleRow("Stop when you go quiet", autoStopSwitch);
        autoStopRow.Margin = Ui.Top(Space.Row);
        panel.Children.Add(autoStopRow);

        var conversationSwitch = new PillSwitch(Settings.ConversationMode);
        conversationSwitch.Toggled += on => Settings.ConversationMode = on;
        var conversationRow = Ui.ToggleRow("Keep listening after answers", conversationSwitch);
        conversationRow.Margin = Ui.Top(Space.Row);
        panel.Children.Add(conversationRow);

        var conversationHint = Ui.Small("Palon reopens the mic briefly for a follow-up and it closes itself if you say nothing. Needs “stop when you go quiet” on.");
        conversationHint.TextWrapping = TextWrapping.Wrap;
        conversationHint.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(conversationHint);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("VOICE"));

        var voiceSwitch = new PillSwitch(Settings.VoiceEnabled);
        voiceSwitch.Toggled += on => Settings.VoiceEnabled = on;
        var voiceToggleRow = Ui.ToggleRow("Speak answers out loud", voiceSwitch);
        voiceToggleRow.Margin = Ui.Top(Space.Row);
        panel.Children.Add(voiceToggleRow);

        var voiceMode = new Segmented("Neural (online)", "Windows only", Settings.VoicePreference == "windows" ? 1 : 0)
        {
            Margin = Ui.Top(Space.Row),
        };
        voiceMode.SelectionChanged += index =>
        {
            Settings.VoicePreference = index == 1 ? "windows" : "auto";
            UpdateVoiceStatus();
        };
        panel.Children.Add(voiceMode);

        var preview = Ui.Link("▶ Preview voice", Font.Small);
        preview.Margin = new Thickness(2, Space.Row, 2, 0);
        preview.MouseLeftButtonUp += (_, _) => _ = PreviewVoice?.Invoke();
        panel.Children.Add(preview);

        _voiceStatus = Ui.Small("");
        _voiceStatus.TextWrapping = TextWrapping.Wrap;
        _voiceStatus.Margin = Ui.Top(Space.Tight);
        panel.Children.Add(_voiceStatus);

        var elevenCaption = Ui.Caption("ELEVENLABS (OPTIONAL PREMIUM VOICE)");
        elevenCaption.Margin = Ui.Top(Space.Section);
        panel.Children.Add(elevenCaption);

        var elevenRow = new Grid { Margin = Ui.Top(Space.Tight) };
        elevenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        elevenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elevenKeyBox = Ui.PasswordBox();
        elevenRow.Children.Add(_elevenKeyBox);
        var saveEleven = Ui.Link("Save", Font.Small);
        saveEleven.Margin = Ui.Left(Space.Row);
        saveEleven.VerticalAlignment = VerticalAlignment.Center;
        saveEleven.MouseLeftButtonUp += (_, _) =>
        {
            if (_elevenKeyBox.Password.Trim().Length == 0) return;
            Settings.ElevenLabsKey = _elevenKeyBox.Password;
            _elevenKeyBox.Password = "";
            UpdateVoiceStatus();
        };
        Grid.SetColumn(saveEleven, 1);
        elevenRow.Children.Add(saveEleven);
        panel.Children.Add(elevenRow);

        var elevenStatusRow = new Grid { Margin = Ui.Top(Space.Tight) };
        elevenStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        elevenStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elevenKeyStatus = Ui.Small("");
        _elevenKeyStatus.TextWrapping = TextWrapping.Wrap;
        elevenStatusRow.Children.Add(_elevenKeyStatus);
        var removeEleven = Ui.Link("Remove", Font.Small);
        removeEleven.Margin = Ui.Left(Space.Row);
        removeEleven.MouseLeftButtonUp += (_, _) =>
        {
            Settings.ElevenLabsKey = null;
            UpdateVoiceStatus();
        };
        Grid.SetColumn(removeEleven, 1);
        elevenStatusRow.Children.Add(removeEleven);
        panel.Children.Add(elevenStatusRow);

        var voiceIdHint = Ui.Caption("Voice ID (optional — blank = Daniel)");
        voiceIdHint.Margin = Ui.Top(Space.Row);
        panel.Children.Add(voiceIdHint);
        var voiceIdBox = Ui.TextBox(Settings.ElevenLabsVoiceId);
        voiceIdBox.Margin = Ui.Top(Space.Tight);
        // Committed per keystroke — the neighboring links are TextBlocks that
        // never take focus, so LostFocus would silently drop the value.
        voiceIdBox.TextChanged += (_, _) => Settings.ElevenLabsVoiceId = voiceIdBox.Text;
        panel.Children.Add(voiceIdBox);

        panel.Children.Add(Ui.Divider(Space.Section, Space.Row));

        panel.Children.Add(Ui.Caption("LAST ANSWER"));

        _exchangeEmpty = Ui.EmptyState("Ask something and it'll show up here.");
        panel.Children.Add(_exchangeEmpty);

        var exchangeStack = new StackPanel();
        _lastQuestion = Ui.Caption("");
        _lastQuestion.TextWrapping = TextWrapping.Wrap;
        exchangeStack.Children.Add(_lastQuestion);
        _lastAnswer = Ui.Small("", "TextPrimaryBrush");
        _lastAnswer.TextWrapping = TextWrapping.Wrap;
        _lastAnswer.Margin = Ui.Top(Space.Tight);
        exchangeStack.Children.Add(_lastAnswer);
        _copyAnswerLink = Ui.Link("Copy", Font.Caption);
        _copyAnswerLink.Margin = Ui.Top(Space.Tight);
        _copyAnswerLink.MouseLeftButtonUp += (_, _) =>
        {
            if (SnippetPaster.TrySetClipboard(_lastAnswer.Text)) Ui.Flash(_copyAnswerLink, "Copied ✓", "Copy");
        };
        exchangeStack.Children.Add(_copyAnswerLink);
        _exchangeCard = Ui.Card(exchangeStack);
        _exchangeCard.Visibility = Visibility.Collapsed;
        panel.Children.Add(_exchangeCard);

        var commandsLink = Ui.Link("Manage commands…", Font.Small);
        commandsLink.Margin = new Thickness(2, Space.Section, 2, 0);
        commandsLink.MouseLeftButtonUp += (_, _) => ShowCommands();
        panel.Children.Add(commandsLink);

        var done = Ui.PrimaryButton("Done");
        done.Margin = Ui.Top(Space.Section);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        RefreshAssistantHotkeyRow();
        UpdateVoiceStatus();
        return panel;
    }

    void UpdateVoiceStatus()
    {
        if (Settings.VoicePreference == "windows")
        {
            _voiceStatus.Text = "Offline Windows voice only — nothing leaves your PC for speech.";
            _elevenKeyStatus.Text = Settings.ElevenLabsKey is null
                ? "No key."
                : "Key saved ✓ — unused while Windows-only is selected.";
            return;
        }
        _voiceStatus.Text = Settings.ElevenLabsKey is null
            ? "Free neural voice — Avri for Hebrew, Ryan (British) for English. Falls back to the Windows voice when offline."
            : "Premium ElevenLabs voice first, then the free neural voice, then offline.";
        _elevenKeyStatus.Text = Settings.ElevenLabsKey is null
            ? "No key — the free neural voice is used. elevenlabs.io for the premium tier."
            : "Key saved ✓ — premium voice on.";
    }

    void RestoreGlobalHotkeys()
    {
        ApplyDictationHotkey?.Invoke();
        ApplyAssistantHotkey?.Invoke();
        ApplySnippetHotkeys?.Invoke();
    }

    void OnAssistantHotkeyCapture(object sender, KeyEventArgs e)
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
            SetAssistantHotkeyStatus("Include Ctrl, Alt or Win in the combo.", warn: true);
            return;
        }
        Keyboard.ClearFocus();
        SaveAssistantHotkey(combo);
    }

    void SaveAssistantHotkey(Hotkey combo)
    {
        var previous = Settings.AssistantHotkey;
        Settings.AssistantHotkey = combo.Serialize();
        if (ApplyAssistantHotkey?.Invoke() ?? true)
        {
            SetAssistantHotkeyStatus(combo.IsOff
                ? "Hotkey off — ask with the 💬 chip in the dock."
                : $"Saved ✓ — press {combo} anywhere to ask.", warn: false);
        }
        else
        {
            Settings.AssistantHotkey = previous;
            ApplyAssistantHotkey?.Invoke();
            SetAssistantHotkeyStatus($"{combo} is taken by another app — kept {Hotkey.LoadAssistant()}.", warn: true);
        }
        RefreshAssistantHotkeyRow();
    }

    void SetAssistantHotkeyStatus(string text, bool warn)
    {
        _asstHotkeyStatus.Text = text;
        _asstHotkeyStatus.SetResourceReference(TextBlock.ForegroundProperty, warn ? "AmberBrush" : "TextSecondaryBrush");
    }

    void RefreshAssistantHotkeyRow()
    {
        var combo = Hotkey.LoadAssistant();
        _asstHotkeyLabel.Text = combo.IsOff ? "Off — click to set" : combo.ToString();
    }

    /// <summary>Shows the latest Q&A in the panel (called from any thread).</summary>
    public void SetLastExchange(string question, string answer)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetLastExchange(question, answer));
            return;
        }
        _lastQuestion.Text = question;
        _lastAnswer.Text = answer;
        _copyAnswerLink.Text = "Copy";
        _exchangeEmpty.Visibility = Visibility.Collapsed;
        _exchangeCard.Visibility = Visibility.Visible;
    }

    public void ShowPalon()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowPalon);
            return;
        }
        _palonKeyHint.Visibility = AiChat.HasKey ? Visibility.Collapsed : Visibility.Visible;
        RefreshAssistantHotkeyRow();
        UpdateVoiceStatus();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_palonPanel);
    }
}
