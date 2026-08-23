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
    HotkeyCaptureBox _asstHotkeyBox = null!;
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

        _asstHotkeyBox = new HotkeyCaptureBox(
            load: Hotkey.LoadAssistant,
            save: SaveAssistantHotkey,
            status: SetAssistantHotkeyStatus,
            suspend: () => SuspendGlobalHotkeys?.Invoke(),
            restore: RestoreGlobalHotkeys);
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

        _elevenKeyBox = Ui.PasswordBox();
        var eleven = Ui.KeyRow(_elevenKeyBox,
            onSave: key =>
            {
                Settings.ElevenLabsKey = key;
                UpdateVoiceStatus();
            },
            onRemove: () =>
            {
                Settings.ElevenLabsKey = null;
                UpdateVoiceStatus();
            });
        _elevenKeyStatus = eleven.Status;
        panel.Children.Add(eleven.InputRow);
        panel.Children.Add(eleven.StatusRow);

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
        _copyAnswerLink = Ui.CopyLink(() => _lastAnswer.Text);
        _copyAnswerLink.Margin = Ui.Top(Space.Tight);
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

    void RefreshAssistantHotkeyRow() => _asstHotkeyBox.Refresh();

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
        Ui.AlignByScript(_lastQuestion);
        Ui.AlignByScript(_lastAnswer);
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
