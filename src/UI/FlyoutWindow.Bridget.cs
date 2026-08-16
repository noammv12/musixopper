using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bridget.UI;

/// <summary>The flyout's Ask Bridget panel: hotkey, voice toggle, last answer.</summary>
partial class FlyoutWindow
{
    readonly StackPanel _bridgetPanel;
    TextBlock _bridgetKeyHint = null!;
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

    /// <summary>Set by Shell: re-registers the dock's Ask-Bridget hotkey.</summary>
    public Func<bool>? ApplyAssistantHotkey { get; set; }

    /// <summary>Set by Shell: speaks a short sample line with the current voice.</summary>
    public Func<Task>? PreviewVoice { get; set; }

    StackPanel BuildBridgetPanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(BackLink());
        panel.Children.Add(Ui.Text("Ask Bridget", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Press the hotkey (or the 💬 chip), ask out loud — Bridget answers back, or opens one of your commands.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        _bridgetKeyHint = Ui.Text("Needs your DeepSeek key — paste it under Notes & dictation.", 10.5, "AmberBrush", FontWeights.SemiBold);
        _bridgetKeyHint.TextWrapping = TextWrapping.Wrap;
        _bridgetKeyHint.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_bridgetKeyHint);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("ASSISTANT HOTKEY", 10, "TextSecondaryBrush", FontWeights.SemiBold));

        var hotkeyRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _asstHotkeyLabel = Ui.Text("", 11.5, "TextPrimaryBrush", FontWeights.SemiBold);
        _asstHotkeyLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _asstHotkeyBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 7),
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

        var reset = Ui.Link("Reset", 10.5);
        reset.Margin = new Thickness(10, 0, 0, 0);
        reset.VerticalAlignment = VerticalAlignment.Center;
        reset.MouseLeftButtonUp += (_, _) => SaveAssistantHotkey(Hotkey.AssistantDefault);
        Grid.SetColumn(reset, 1);
        hotkeyRow.Children.Add(reset);

        var off = Ui.Link("Turn off", 10.5);
        off.Margin = new Thickness(10, 0, 0, 0);
        off.VerticalAlignment = VerticalAlignment.Center;
        off.MouseLeftButtonUp += (_, _) => SaveAssistantHotkey(Hotkey.Off);
        Grid.SetColumn(off, 2);
        hotkeyRow.Children.Add(off);
        panel.Children.Add(hotkeyRow);

        _asstHotkeyStatus = Ui.Text("Click the box, then press the combo you want.", 10.5, "TextSecondaryBrush");
        _asstHotkeyStatus.TextWrapping = TextWrapping.Wrap;
        _asstHotkeyStatus.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_asstHotkeyStatus);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("VOICE", 10, "TextSecondaryBrush", FontWeights.SemiBold));

        var voiceSwitch = new PillSwitch(Settings.VoiceEnabled);
        voiceSwitch.Toggled += on => Settings.VoiceEnabled = on;
        var voiceToggleRow = Ui.ToggleRow("Speak answers out loud", voiceSwitch);
        voiceToggleRow.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(voiceToggleRow);

        var voiceMode = new Segmented("Neural (online)", "Windows only", Settings.VoicePreference == "windows" ? 1 : 0)
        {
            Margin = new Thickness(0, 10, 0, 0),
        };
        voiceMode.SelectionChanged += index =>
        {
            Settings.VoicePreference = index == 1 ? "windows" : "auto";
            UpdateVoiceStatus();
        };
        panel.Children.Add(voiceMode);

        var preview = Ui.Link("▶ Preview voice", 11);
        preview.Margin = new Thickness(2, 8, 2, 0);
        preview.MouseLeftButtonUp += (_, _) => _ = PreviewVoice?.Invoke();
        panel.Children.Add(preview);

        _voiceStatus = Ui.Text("", 10.5, "TextSecondaryBrush");
        _voiceStatus.TextWrapping = TextWrapping.Wrap;
        _voiceStatus.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_voiceStatus);

        var elevenCaption = Ui.Text("ELEVENLABS (OPTIONAL PREMIUM VOICE)", 10, "TextSecondaryBrush", FontWeights.SemiBold);
        elevenCaption.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(elevenCaption);

        var elevenRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        elevenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        elevenRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elevenKeyBox = Ui.PasswordBox();
        elevenRow.Children.Add(_elevenKeyBox);
        var saveEleven = Ui.Link("Save", 11);
        saveEleven.Margin = new Thickness(10, 0, 0, 0);
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

        var elevenStatusRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        elevenStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        elevenStatusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _elevenKeyStatus = Ui.Text("", 10.5, "TextSecondaryBrush");
        _elevenKeyStatus.TextWrapping = TextWrapping.Wrap;
        elevenStatusRow.Children.Add(_elevenKeyStatus);
        var removeEleven = Ui.Link("Remove", 10.5);
        removeEleven.Margin = new Thickness(10, 0, 0, 0);
        removeEleven.MouseLeftButtonUp += (_, _) =>
        {
            Settings.ElevenLabsKey = null;
            UpdateVoiceStatus();
        };
        Grid.SetColumn(removeEleven, 1);
        elevenStatusRow.Children.Add(removeEleven);
        panel.Children.Add(elevenStatusRow);

        var voiceIdHint = Ui.Text("Voice ID (optional — blank = Rachel)", 10, "TextSecondaryBrush");
        voiceIdHint.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(voiceIdHint);
        var voiceIdBox = Ui.TextBox(Settings.ElevenLabsVoiceId);
        voiceIdBox.Margin = new Thickness(0, 4, 0, 0);
        voiceIdBox.LostFocus += (_, _) => Settings.ElevenLabsVoiceId = voiceIdBox.Text;
        panel.Children.Add(voiceIdBox);

        panel.Children.Add(Ui.Divider(12, 10));

        panel.Children.Add(Ui.Text("LAST ANSWER", 10, "TextSecondaryBrush", FontWeights.SemiBold));

        _exchangeEmpty = Ui.Text("Ask something and it'll show up here.", 11, "TextSecondaryBrush");
        _exchangeEmpty.Margin = new Thickness(2, 8, 0, 0);
        panel.Children.Add(_exchangeEmpty);

        var exchangeStack = new StackPanel();
        _lastQuestion = Ui.Text("", 10.5, "TextSecondaryBrush", FontWeights.SemiBold);
        _lastQuestion.TextWrapping = TextWrapping.Wrap;
        exchangeStack.Children.Add(_lastQuestion);
        _lastAnswer = Ui.Text("", 11, "TextPrimaryBrush");
        _lastAnswer.TextWrapping = TextWrapping.Wrap;
        _lastAnswer.Margin = new Thickness(0, 4, 0, 0);
        exchangeStack.Children.Add(_lastAnswer);
        _copyAnswerLink = Ui.Link("Copy", 10.5);
        _copyAnswerLink.Margin = new Thickness(0, 6, 0, 0);
        _copyAnswerLink.MouseLeftButtonUp += (_, _) =>
        {
            if (SnippetPaster.TrySetClipboard(_lastAnswer.Text)) Ui.Flash(_copyAnswerLink, "Copied ✓", "Copy");
        };
        exchangeStack.Children.Add(_copyAnswerLink);
        _exchangeCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = exchangeStack,
        };
        _exchangeCard.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        panel.Children.Add(_exchangeCard);

        var commandsLink = Ui.Link("Manage commands…", 11);
        commandsLink.Margin = new Thickness(2, 12, 2, 0);
        commandsLink.MouseLeftButtonUp += (_, _) => ShowCommands();
        panel.Children.Add(commandsLink);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 12, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        RefreshAssistantHotkeyRow();
        return panel;
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

    public void ShowBridget()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ShowBridget);
            return;
        }
        _bridgetKeyHint.Visibility = Settings.DeepSeekKey is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshAssistantHotkeyRow();
        ShowFlyoutCore(onboarding: false, force: true);
        ShowPanel(_bridgetPanel);
    }
}
