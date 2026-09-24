using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Interop;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>The quick "name · when" field (Ctrl+Alt+R / "+ חזרה") and the listening pill.</summary>
sealed partial class DockWindow
{
    // ---- quick field ----
    Grid? _quickRow;
    TextBox _quickBox = null!;
    TextBlock _quickPlaceholder = null!;
    Border _quickPreview = null!;
    TextBlock _quickPreviewText = null!;
    TextBlock _quickMiss = null!;
    Border _quickEdit = null!;
    Border _quickDone = null!;
    TextBlock _quickDoneText = null!;
    Border _quickDoneBadge = null!;
    bool _quickBooked;
    bool _focusMode;
    IntPtr _prevForeground;
    List<QuickPerson> _people = new();
    QuickParse _quickParse = new(null, null);
    DispatcherTimer? _quickTuck;

    void ToggleQuick()
    {
        if (_moment == MomentKind.Quick) CloseQuick(restoreFocus: true);
        else OpenQuick();
    }

    /// <summary>Opens the field and gives it the keyboard — the one time the dock takes focus.</summary>
    void OpenQuick()
    {
        if (_fullscreenHidden || !IsVisible) return;
        if (_moment == MomentKind.Listening) return; // one thing at a time; the mic wins
        BuildQuick();
        try
        {
            var deals = Palon.Sales.SalesStore.ListMonths().SelectMany(m => m.Deals);
            _people = DockQuick.People(ClientIndex.Build(Palon.Notes.NotesStore.Load(), CallbackStore.Load(), deals));
        }
        catch (Exception ex)
        {
            Log.Write($"Dock people load failed: {ex.Message}");
            _people = new();
        }
        _quickBooked = false;
        _quickTuck?.Stop();
        _quickBox.Text = "";
        _quickEdit.Visibility = Visibility.Visible;
        _quickDone.Visibility = Visibility.Collapsed;
        UpdateQuickPreview();
        if (_moment == MomentKind.Card) PauseCardIdle(); // the card waits in the queue; the field shows now
        ShowMoment(MomentKind.Quick, _quickRow!);
        EnterFocusMode();
    }

    void CloseQuick(bool restoreFocus)
    {
        _quickTuck?.Stop();
        LeaveFocusMode(restoreFocus);
        if (_moment == MomentKind.Quick) Settle();
    }

    void EnterFocusMode()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var fg = NativeMethods.GetForegroundWindow();
        if (!_focusMode && fg != hwnd) _prevForeground = fg;
        _focusMode = true;
        SetNoActivate(hwnd, false);
        Focusable = true;
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);
        Dispatcher.InvokeAsync(() =>
        {
            if (!_focusMode) return;
            _quickBox.Focus();
            Keyboard.Focus(_quickBox);
        }, DispatcherPriority.Input);
    }

    /// <summary>Back to never-activate; focus returns to the app the user was in.</summary>
    void LeaveFocusMode(bool restoreFocus)
    {
        if (!_focusMode) return;
        _focusMode = false; // first: the activation change below re-enters via Deactivated
        var hwnd = new WindowInteropHelper(this).Handle;
        Keyboard.ClearFocus();
        Focusable = false;
        if (hwnd != IntPtr.Zero) SetNoActivate(hwnd, true);
        var prev = _prevForeground;
        _prevForeground = IntPtr.Zero;
        if (restoreFocus && prev != IntPtr.Zero && NativeMethods.IsWindow(prev)) NativeMethods.SetForegroundWindow(prev);
    }

    void BuildQuick()
    {
        if (_quickRow is not null) return;
        var clock = DockKit.Icon(DockKit.IconClock, 18, DockPalette.Muted);
        clock.Margin = new Thickness(0, 0, 12, 0);

        _quickBox = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = DockPalette.Text,
            CaretBrush = DockPalette.Text,
            SelectionBrush = DockPalette.Rings[0],
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Thickness(0),
            MaxLength = 80,
        };
        _quickBox.TextChanged += (_, _) => UpdateQuickPreview();
        _quickBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                QuickBook();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseQuick(restoreFocus: true);
            }
        };
        _quickPlaceholder = DockKit.Text("דני 11 · מאיה מחר בערב · בעוד שעה", 16, DockPalette.Faint);
        _quickPlaceholder.IsHitTestVisible = false;
        _quickPlaceholder.FlowDirection = FlowDirection.RightToLeft;
        _quickPlaceholder.HorizontalAlignment = HorizontalAlignment.Left;
        var field = new Grid { VerticalAlignment = VerticalAlignment.Center };
        field.Children.Add(_quickPlaceholder);
        field.Children.Add(_quickBox);

        _quickPreviewText = DockKit.Text("", 13.5, DockPalette.OnPrimary, FontWeights.SemiBold);
        _quickPreviewText.MaxWidth = 230;
        var enter = DockKit.Text("↵", 13, DockPalette.OnPrimary);
        enter.Margin = new Thickness(8, 0, 0, 0);
        enter.Opacity = 0.55;
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        previewRow.Children.Add(_quickPreviewText);
        previewRow.Children.Add(enter);
        _quickPreview = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(16, 0, 14, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = previewRow,
            ToolTip = "Enter קובע",
            Visibility = Visibility.Collapsed,
        };
        DockKit.Paint(_quickPreview, DockKit.Kind.Primary);
        DockKit.Clickable(_quickPreview, QuickBook);
        _quickMiss = DockKit.Text("", 12.5, DockPalette.OverdueText);
        _quickMiss.Margin = new Thickness(8, 0, 4, 0);
        var hotkey = DockKit.Text("", 11, DockPalette.Faint);
        hotkey.Margin = new Thickness(8, 0, 4, 0);
        var close = DockKit.IconButton(DockKit.IconClose, "סגור (Esc)", () => CloseQuick(restoreFocus: true), 36);
        close.Margin = new Thickness(4, 0, 0, 0);

        var edit = new Grid { VerticalAlignment = VerticalAlignment.Center };
        edit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        edit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        edit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        edit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        edit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        edit.Children.Add(clock);
        Grid.SetColumn(field, 1);
        edit.Children.Add(field);
        Grid.SetColumn(_quickMiss, 2);
        edit.Children.Add(_quickMiss);
        Grid.SetColumn(_quickPreview, 3);
        edit.Children.Add(_quickPreview);
        Grid.SetColumn(close, 4);
        edit.Children.Add(close);
        _quickEdit = new Border { Child = edit, Padding = new Thickness(18, 0, 8, 0), Height = 56 };

        _quickDoneBadge = DockKit.CheckBadge(DockPalette.Done, 26);
        _quickDoneText = DockKit.Text("", 14, null, FontWeights.SemiBold);
        _quickDoneText.Margin = new Thickness(12, 0, 8, 0);
        var undo = DockKit.Button("בטל", DockKit.Kind.Ghost, null, height: 32);
        var done = new Grid { VerticalAlignment = VerticalAlignment.Center };
        done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        done.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        done.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        done.Children.Add(_quickDoneBadge);
        Grid.SetColumn(_quickDoneText, 1);
        done.Children.Add(_quickDoneText);
        Grid.SetColumn(undo, 2);
        done.Children.Add(undo);
        _quickDone = new Border { Child = done, Padding = new Thickness(16, 0, 10, 0), Height = 56, Visibility = Visibility.Collapsed };
        DockKit.Clickable(undo, () =>
        {
            UndoQuick();
            _quickTuck?.Stop();
            Settle();
            Flash("בוטל", null, null, null);
        });

        _quickRow = new Grid();
        _quickRow.Children.Add(_quickEdit);
        _quickRow.Children.Add(_quickDone);
    }

    QuickPerson? QuickContext() =>
        OnCall && (_callDisplayName ?? _callNumber) is { } n ? new QuickPerson(n, _callNumber) : _lastClient;

    void UpdateQuickPreview()
    {
        var text = _quickBox.Text;
        _quickPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _quickParse = DockQuick.Parse(text, DateTime.Now, _people, QuickContext());
        if (_quickParse.CanBook && _quickParse.WhenLocal is { } w)
        {
            var ruled = DockQuick.ApplyRules(w, RulesFor(_quickParse.Who!.Name, _quickParse.Who.Phone));
            _quickParse = _quickParse with { WhenLocal = ruled };
            DockKit.SetText(_quickPreviewText, DockQuick.Preview(_quickParse, DateTime.Now));
            _quickPreview.Visibility = Visibility.Visible;
            _quickMiss.Visibility = Visibility.Collapsed;
        }
        else
        {
            _quickPreview.Visibility = Visibility.Collapsed;
            DockKit.SetText(_quickMiss, _quickParse.Miss ?? "");
            _quickMiss.Visibility = _quickParse.Miss is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    Callback? _quickCreated;

    void QuickBook()
    {
        if (_quickBooked || !_quickParse.CanBook) return;
        var who = _quickParse.Who!;
        var when = _quickParse.WhenLocal!.Value;
        _quickCreated = BookFor(who.Name, who.Phone, when, "");
        if (_quickCreated is null)
        {
            DockKit.SetText(_quickMiss, "לא הצלחתי לקבוע");
            _quickMiss.Visibility = Visibility.Visible;
            return;
        }
        _quickBooked = true;
        _lastClient = who;
        var line = $"{who.Name} · {DockText.When(when, DateTime.Now)}";
        DockKit.SetText(_quickDoneText, "נקבע · " + line);
        Swap(_quickEdit, _quickDone);
        DockKit.Pop(_quickDoneBadge);
        _restAvatar.Cheer();
        LeaveFocusMode(restoreFocus: true); // typing goes straight back where it was
        RefreshToday();
        _quickTuck ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _quickTuck.Tick -= OnQuickTuck;
        _quickTuck.Tick += OnQuickTuck;
        _quickTuck.Start();

        void OnQuickTuck(object? s, EventArgs e)
        {
            _quickTuck!.Stop();
            _quickTuck.Tick -= OnQuickTuck;
            if (_pill.IsMouseOver)
            {
                _quickTuck.Tick += OnQuickTuck;
                _quickTuck.Start();
                return;
            }
            if (_moment != MomentKind.Quick) return;
            Settle();
            var first = TemplateFill.FirstName(who.Name);
            Flash($"{first} · {DockText.When(when, DateTime.Now)}", null, null, UndoQuick);
        }
    }

    void UndoQuick()
    {
        if (_quickCreated is not { } c) return;
        try
        {
            CallbackStore.Remove(c.Id);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock quick undo failed: {ex.Message}");
        }
        _quickCreated = null;
        RefreshToday();
    }

    // ---- listening (dictation / Ask Palon) --------------------------------------------------

    StackPanel _listenRow = null!;
    Ellipse _listenDot = null!;
    StackPanel _wavePanel = null!;
    readonly Border[] _waveBars = new Border[5];
    System.Windows.Shapes.Path _listenIcon = null!;
    TextBlock _listenText = null!;
    static readonly double[] WaveWeights = { 0.6, 0.85, 1, 0.85, 0.6 };
    const double WaveMinHeight = 4;
    const double WaveMaxHeight = 20;
    bool _waveActive;
    double _voiceLevel;
    int _waveTick;
    bool _dictationActive;
    bool _assistantActive;
    string _dictationStatus = "";
    string _assistantStatus = "";

    void BuildListening()
    {
        _listenDot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Fill = DockPalette.Done };
        _wavePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, FlowDirection = FlowDirection.LeftToRight };
        for (var i = 0; i < _waveBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = WaveMinHeight,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = DockPalette.Text,
            };
            _waveBars[i] = bar;
            _wavePanel.Children.Add(bar);
        }
        _listenIcon = DockKit.Icon(DockKit.IconChat, 16);
        _listenIcon.Visibility = Visibility.Collapsed;
        _listenText = DockKit.Text("", Font.Lead, null, FontWeights.Medium);
        _listenText.Margin = new Thickness(10, 0, 10, 0);
        _listenText.MaxWidth = 320;
        var finish = DockKit.Button("סיום", DockKit.Kind.Primary, () =>
            (_assistantActive ? AssistantToggleRequested : DictationToggleRequested)?.Invoke(), height: 34);
        var cancel = DockKit.IconButton(DockKit.IconClose, "ביטול", () =>
            (_assistantActive ? AssistantCancelRequested : DictationCancelRequested)?.Invoke(), 34);
        cancel.Margin = new Thickness(4, 0, 0, 0);
        _listenRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 7, 7, 7) };
        _listenRow.Children.Add(_listenDot);
        _listenRow.Children.Add(_wavePanel);
        _listenRow.Children.Add(_listenIcon);
        _listenRow.Children.Add(_listenText);
        _listenRow.Children.Add(finish);
        _listenRow.Children.Add(cancel);
    }

    void UpdateListeningText()
    {
        if (_listenText is null) return;
        _listenIcon.Visibility = _assistantActive ? Visibility.Visible : Visibility.Collapsed;
        _listenDot.Visibility = _assistantActive || _waveActive ? Visibility.Collapsed : Visibility.Visible;
        var text =
            _assistantActive ? (_assistantStatus.Length > 0 ? _assistantStatus : "Palon מקשיב — שאל")
            : _dictationStatus.Length > 0 ? _dictationStatus
            : DictationHotkeyLive ? $"מכתיב — {_dictationHotkey} לסיום"
            : "מכתיב — סיום כשגמרת";
        if (_listenText.Text == text) return;
        DockKit.SetText(_listenText, text);
        if (_moment == MomentKind.Listening) Refit();
    }

    public void SetDictation(bool active)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetDictation(active));
            return;
        }
        _dictationActive = active;
        if (!active) _dictationStatus = "";
        SyncListening(active);
    }

    public void SetAssistant(bool active)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetAssistant(active));
            return;
        }
        _assistantActive = active;
        if (!active) _assistantStatus = "";
        SyncListening(active);
    }

    void SyncListening(bool activated)
    {
        UpdateListeningText();
        if (activated)
        {
            if (_moment == MomentKind.Quick) CloseQuick(restoreFocus: true);
            PauseCardIdle();
            ShowMoment(MomentKind.Listening, _listenRow); // the mic outranks a card; the card waits
        }
        else if (_moment == MomentKind.Listening && !_dictationActive && !_assistantActive)
        {
            StopWave();
            Settle();
        }
    }

    /// <summary>Live mic level (dBFS) while dictating / asking — drives the waveform.</summary>
    public void SetVoiceLevel(double db)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetVoiceLevel(db));
            return;
        }
        if (_moment != MomentKind.Listening) return;
        if (!_waveActive)
        {
            _waveActive = true;
            _voiceLevel = 0;
            _wavePanel.Visibility = Visibility.Visible;
            UpdateListeningText();
            Refit();
        }
        // −55…−20 dBFS → 0…1; fast attack, slow decay.
        var target = Math.Clamp((db + 55) / 35.0, 0, 1);
        _voiceLevel += (target - _voiceLevel) * (target > _voiceLevel ? 0.6 : 0.15);
        _waveTick++;
        for (var i = 0; i < _waveBars.Length; i++)
        {
            var jitter = DockMotion.Reduced ? 0 : 0.08 * Math.Sin(_waveTick * 0.9 + i * 1.7);
            var span = Math.Clamp(_voiceLevel * WaveWeights[i] + jitter, 0, 1);
            var height = WaveMinHeight + (WaveMaxHeight - WaveMinHeight) * span;
            _waveBars[i].BeginAnimation(HeightProperty, DockMotion.FromTo(_waveBars[i].ActualHeight, height, Motion.Fast));
        }
    }

    void StopWave()
    {
        if (!_waveActive) return;
        _waveActive = false;
        _wavePanel.Visibility = Visibility.Collapsed;
        foreach (var bar in _waveBars)
        {
            bar.BeginAnimation(HeightProperty, null);
            bar.Height = WaveMinHeight;
        }
        UpdateListeningText();
    }
}
