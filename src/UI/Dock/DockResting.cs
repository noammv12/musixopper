using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Sales;
using Palon.Terminal;
using ShapePath = System.Windows.Shapes.Path;

namespace Palon.UI;

/// <summary>Rest (the slim pill + its micro messages), Hover (one row of
/// actions) and the on-call amber capsule.</summary>
sealed partial class DockWindow
{
    // ---- rest ----
    StackPanel _restRow = null!;
    Ellipse _statusDot = null!;
    DockRingsView _rings = null!;
    Border _overdueBadge = null!;
    TextBlock _overdueText = null!;
    StackPanel _flashPanel = null!;
    ShapePath _flashGlyph = null!;
    TextBlock _flashText = null!;
    Border _flashAction = null!;
    PalonAvatar _restAvatar = null!;
    DockRings _today;

    // ---- hover ----
    StackPanel _hoverSeg = null!;
    StackPanel _moreSeg = null!;
    Border _quickButton = null!;
    StackPanel _pinsPanel = null!;
    StackPanel _snippetsPanel = null!;
    Border _dictateButton = null!;
    Border _scanButton = null!;

    // ---- flash (rest micro message) ----
    readonly DispatcherTimer _flashTimer = new();
    Action? _flashClick;
    Action? _flashUndo;
    bool _flashOn;

    // ---- call capsule ----
    Grid _callRow = null!;
    TextBlock _callName = null!;
    TextBlock _callSub = null!;
    Border _callUndo = null!;
    TextBlock _callTimer = null!;
    Border _alarmButton = null!;
    string? _callDisplayName;
    Callback? _callBooked;
    QuickPerson? _lastClient;

    void BuildRest()
    {
        _statusDot = new Ellipse { Width = 8, Height = 8, Fill = DockPalette.Done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _rings = new DockRingsView { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        _overdueText = DockKit.Numeric("", 11.5, Brushes.White, FontWeights.SemiBold);
        _overdueText.HorizontalAlignment = HorizontalAlignment.Center;
        _overdueBadge = new Border
        {
            MinWidth = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = DockPalette.Overdue,
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = _overdueText,
            Visibility = Visibility.Collapsed,
        };
        DockKit.Clickable(_overdueBadge, () => OpenRemindersRequested?.Invoke());

        _flashGlyph = new ShapePath { Width = 10, Height = 11, Fill = DockPalette.Text, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 0, 8, 0) };
        _flashText = DockKit.Text("", Font.Lead, null, FontWeights.Medium);
        _flashText.MaxWidth = 380;
        _flashAction = DockKit.Button("בטל", DockKit.Kind.Ghost, () =>
        {
            var undo = _flashUndo;
            _flashUndo = null;
            if (undo is not null)
            {
                undo();
                Flash("בוטל", null, null, null);
            }
        }, height: 28);
        _flashAction.Margin = new Thickness(6, 0, 0, 0);
        _flashPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 4, 0) };
        _flashPanel.Children.Add(_flashGlyph);
        _flashPanel.Children.Add(_flashText);
        _flashPanel.Children.Add(_flashAction);
        _flashPanel.MouseLeftButtonDown += (_, e) =>
        {
            if (_flashClick is not null) e.Handled = true;
        };
        _flashPanel.MouseLeftButtonUp += (_, e) =>
        {
            if (_flashClick is not { } click) return;
            e.Handled = true;
            _flashClick = null;
            EndFlash();
            click();
        };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            if (_pill.IsMouseOver && (_flashClick is not null || _flashUndo is not null)) return; // MouseLeave re-arms
            EndFlash();
        };

        _restAvatar = new PalonAvatar { Width = 28, Height = 28, Mood = PalonMood.Idle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Palon · פתח את Now" };
        var avatarPressed = false;
        _restAvatar.MouseLeftButtonDown += (_, e) =>
        {
            if (_mode != DockMode.Hover) return; // at rest a click just opens the row
            e.Handled = true;
            avatarPressed = true;
        };
        _restAvatar.MouseLeftButtonUp += (_, e) =>
        {
            if (!avatarPressed) return;
            avatarPressed = false;
            e.Handled = true;
            TerminalWindow.ShowSingleton();
        };
        _restAvatar.MouseLeave += (_, _) => avatarPressed = false;

        BuildHover();

        _restRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
        };
        _restRow.Children.Add(_statusDot);
        _restRow.Children.Add(_rings);
        _restRow.Children.Add(new Border { Width = 8 });
        _restRow.Children.Add(_overdueBadge);
        _restRow.Children.Add(_flashPanel);
        _restRow.Children.Add(_hoverSeg);
        _restRow.Children.Add(_restAvatar);
    }

    void BuildHover()
    {
        _quickButton = DockKit.Button("+ חזרה", DockKit.Kind.Primary, () => OpenQuick(), height: 34);
        _pinsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var more = DockKit.IconButton(DockKit.IconMore, "עוד: הכתבה, קריאת מסך, קטעים", ToggleMore, 34, DockPalette.Text);

        _dictateButton = DockKit.IconButton(DockKit.IconMic, "הכתבה", () => DictationToggleRequested?.Invoke(), 34, DockPalette.Text);
        _scanButton = DockKit.IconButton(DockKit.IconScan, "קרא מהמסך", TerminalWindow.ReadScreenFromShortcut, 34, DockPalette.Text);
        var ask = DockKit.IconButton(DockKit.IconChat, "שאל את Palon", () => AssistantToggleRequested?.Invoke(), 34, DockPalette.Text);
        var settings = DockKit.IconButton(DockKit.IconGear, "קטעים והגדרות", () => OpenFlyoutRequested?.Invoke(), 34, DockPalette.Text);
        _snippetsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _moreSeg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _moreSeg.Children.Add(DockKit.Divider());
        _moreSeg.Children.Add(_dictateButton);
        _moreSeg.Children.Add(_scanButton);
        _moreSeg.Children.Add(ask);
        _moreSeg.Children.Add(_snippetsPanel);
        _moreSeg.Children.Add(settings);

        _hoverSeg = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _hoverSeg.Children.Add(DockKit.Divider());
        _hoverSeg.Children.Add(_quickButton);
        _hoverSeg.Children.Add(_pinsPanel);
        _hoverSeg.Children.Add(more);
        _hoverSeg.Children.Add(_moreSeg);
        _hoverSeg.Children.Add(DockKit.Divider());
        foreach (FrameworkElement child in _hoverSeg.Children)
            if (child is Border { Width: not 1 } b) b.Margin = new Thickness(0, 0, 6, 0);

        RebuildPins();
        RebuildSnippets();
        RefreshHoverTips();
    }

    void ExpandHover()
    {
        _moreSeg.Visibility = Visibility.Collapsed;
        RebuildPins(); // the first name may have changed since last time
        _hoverSeg.BeginAnimation(OpacityProperty, null);
        _hoverSeg.Visibility = Visibility.Visible;
        _hoverSeg.Opacity = 0;
        var fade = DockMotion.FromTo(0, 1, 350);
        fade.BeginTime = DockMotion.Delay(60);
        _hoverSeg.BeginAnimation(OpacityProperty, fade);
        _restAvatar.RenderTransformOrigin = new Point(0.5, 0.5);
        var lean = new RotateTransform(0);
        _restAvatar.RenderTransform = lean;
        lean.BeginAnimation(RotateTransform.AngleProperty, DockMotion.FromTo(0, 10, 600, DockMotion.Spring));
    }

    void CollapseHover()
    {
        if (_restAvatar.RenderTransform is RotateTransform lean)
            lean.BeginAnimation(RotateTransform.AngleProperty, DockMotion.To(0, 450));
        if (_hoverSeg.Visibility != Visibility.Visible) return;
        // Collapse first so the pill measures its resting width; the spring and
        // the clip carry the row out of view.
        _hoverSeg.BeginAnimation(OpacityProperty, null);
        _hoverSeg.Opacity = 0;
        _hoverSeg.Visibility = Visibility.Collapsed;
        _moreSeg.Visibility = Visibility.Collapsed;
    }

    void ToggleMore()
    {
        _moreSeg.Visibility = _moreSeg.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (_moreSeg.Visibility == Visibility.Visible) DockKit.RiseIn(_moreSeg, 0);
        Refit();
    }

    void RefreshHoverTips()
    {
        if (_quickButton is null) return;
        _quickButton.ToolTip = LiveQuickHotkey is { } key ? $"חזרה מהירה ({key})" : "חזרה מהירה";
        _dictateButton.ToolTip = DictationHotkeyLive ? $"הכתבה ({_dictationHotkey})" : "הכתבה";
        _scanButton.ToolTip = _screenHotkeyRegistered ? $"קרא מהמסך ({ScreenHotkeyLabel})" : "קרא מהמסך";
    }

    // ---- pinned templates ---------------------------------------------------------

    void OnTemplatesChanged() => Dispatcher.InvokeAsync(RebuildPins, DispatcherPriority.Background);

    void OnDockSettingsChanged()
    {
        OnTemplatesChanged();
        OnStoresChanged();
    }

    /// <summary>The client the templates greet: the caller, else the last client.</summary>
    string CurrentFirstName()
    {
        var name = OnCall ? _callDisplayName : _lastClient?.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsAsciiDigit)) return "";
        return TemplateFill.FirstName(name);
    }

    void RebuildPins()
    {
        if (_pinsPanel is null) return;
        _pinsPanel.Children.Clear();
        List<MessageTemplate> pins;
        try
        {
            pins = DockPins.Pick(TemplatesStore.Load(), Settings.DockPinnedTemplates);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock pins load failed: {ex.Message}");
            return;
        }
        var first = CurrentFirstName();
        foreach (var t in pins)
        {
            var label = DockPins.ShortLabel(t);
            var button = DockKit.Button(label, DockKit.Kind.Secondary, null, height: 34);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.ToolTip = first.Length > 0 ? $"{t.Title} · העתק עם השם {first}" : $"{t.Title} · העתק";
            var text = DockKit.LabelOf(button);
            var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                DockKit.SetText(text, label);
                button.MinWidth = 0;
                Refit();
            };
            var template = t;
            DockKit.Clickable(button, () =>
            {
                var name = CurrentFirstName();
                var filled = TemplateFill.Fill(template, name);
                var ok = DockKit.TryCopy(filled);
                if (ok)
                {
                    try
                    {
                        TemplateLearningStore.LogCopy(template, OnCall ? _callDisplayName : _lastClient?.Name,
                            OnCall ? _callNumber : _lastClient?.Phone, filled, null, "dock");
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Dock template log failed: {ex.Message}");
                    }
                }
                if (!revert.IsEnabled) button.MinWidth = button.ActualWidth;
                DockKit.SetText(text, !ok ? "נכשל" : name.Length > 0 ? $"הועתק · {name}" : "הועתק");
                revert.Stop();
                revert.Start();
                Refit();
            });
            _pinsPanel.Children.Add(button);
        }
        if (_mode == DockMode.Hover) Refit();
    }

    // ---- snippets (in "…") ------------------------------------------------------------

    void OnSnippetsChanged()
    {
        Dispatcher.InvokeAsync(RebuildSnippets, DispatcherPriority.Background);
        ApplySnippetHotkeys();
    }

    void RebuildSnippets()
    {
        _snippetsPanel.Children.Clear();
        List<Snippet> snippets;
        try
        {
            snippets = SnippetStore.Load().Take(3).ToList();
        }
        catch
        {
            return;
        }
        foreach (var snippet in snippets)
        {
            var original = snippet.Label.Length > 0 ? snippet.Label : "(ללא שם)";
            var chip = DockKit.Button(original, DockKit.Kind.Secondary, null, DockKit.IconSnippet, height: 34);
            chip.Margin = new Thickness(0, 0, 6, 0);
            DockKit.LabelOf(chip).MaxWidth = 90;
            chip.ToolTip = (snippet.Text.Length > 120 ? snippet.Text[..120] + "…" : snippet.Text) + "\n(קליק ימני: העתק בלבד)";
            var label = DockKit.LabelOf(chip);
            var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Revert) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                DockKit.SetText(label, original);
                chip.MinWidth = 0;
            };
            void Show(string message)
            {
                if (!revert.IsEnabled) chip.MinWidth = chip.ActualWidth;
                DockKit.SetText(label, message);
                revert.Stop();
                revert.Start();
            }
            DockKit.Clickable(chip, async () =>
            {
                // The foreground app is the paste target — the dock never activated.
                var result = await SnippetPaster.PasteAsync(snippet);
                Show(result switch { PasteResult.Pasted => "הודבק ✓", PasteResult.CopiedOnly => "הועתק ✓", _ => "נכשל" });
            });
            chip.MouseRightButtonUp += (_, _) => Show(SnippetPaster.CopyOnly(snippet) == PasteResult.Failed ? "נכשל" : "הועתק ✓");
            _snippetsPanel.Children.Add(chip);
        }
    }

    // ---- today: rings, overdue, status dot ------------------------------------------------

    void OnStoresChanged() => Dispatcher.InvokeAsync(RefreshToday, DispatcherPriority.Background);

    void RefreshToday()
    {
        var now = DateTime.Now;
        try
        {
            var callbacks = CallbackStore.Load();
            // Same source and goal as the Now window's calls ring (CallStatsStore + Settings.CallsGoal).
            var records = CallStatsStore.Load();
            var calls = records.Select(c => c.StartedUtc.ToLocalTime());
            var book = SalesStore.Get(now.Year, now.Month);
            _today = DockRings.Compute(now, calls, book?.Deals.Select(d => d.Date) ?? Enumerable.Empty<DateTime>(), book?.Target, callbacks,
                DayRings.CallsGoal(records, now, Settings.CallsGoal));
        }
        catch (Exception ex)
        {
            Log.Write($"Dock today read failed: {ex.Message}");
        }
        _rings.Set(_today.CallsP, _today.DepositsP, _today.CallbacksP);
        _rings.ToolTip = _today.Label;
        _overdueText.Text = _today.Overdue.ToString(CultureInfo.InvariantCulture);
        _overdueBadge.ToolTip = _today.Overdue == 1 ? "חזרה אחת באיחור" : $"{_today.Overdue} חזרות באיחור";
        var show = _today.Overdue > 0 && !_flashOn;
        var changed = (_overdueBadge.Visibility == Visibility.Visible) != show;
        _overdueBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (changed && _mode != DockMode.Moment && !OnCall) Refit();
    }

    void UpdateStatusDot()
    {
        _statusDot.Fill = _callState switch
        {
            CallState.OnCall => DockPalette.OnCall,
            CallState.Disabled => DockPalette.Faint,
            _ when _notesStatus.Length > 0 => DockPalette.Rings[0],
            _ => DockPalette.Done,
        };
        _statusDot.ToolTip = _callState switch
        {
            CallState.OnCall => "בשיחה",
            CallState.Disabled => "מושהה",
            _ => _notesStatus.Length > 0 ? _notesStatus : "מוכן",
        };
    }

    // ---- flash: the rest-state micro message ------------------------------------------------

    /// <summary>A one-line message in the pill (music paused, "דני · מחר 11:00 · בטל").
    /// On a call it takes the capsule's second line instead — nothing grows.</summary>
    void Flash(string text, System.Windows.Media.Geometry? glyph, Action? onClick, Action? undo)
    {
        _flashClick = onClick;
        _flashUndo = undo;
        _flashTimer.Stop();
        _flashTimer.Interval = TimeSpan.FromMilliseconds(onClick is null && undo is null ? DockMotion.FlashMs : DockMotion.FlashActionMs);
        if (OnCall)
        {
            SetCallSub(text, undo);
            _flashOn = true;
            _flashTimer.Start();
            return;
        }
        DockKit.SetText(_flashText, text);
        _flashGlyph.Data = glyph;
        _flashGlyph.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        _flashAction.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        _flashPanel.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;
        _flashOn = true;
        _rings.Visibility = Visibility.Collapsed;
        _overdueBadge.Visibility = Visibility.Collapsed;
        _flashPanel.Visibility = Visibility.Visible;
        DockKit.RiseIn(_flashPanel, 0);
        if (!_pill.IsMouseOver || onClick is null && undo is null) _flashTimer.Start();
        if (_mode != DockMode.Moment) Refit();
    }

    void ResumeFlashTimer()
    {
        if (_flashOn && !_flashTimer.IsEnabled) _flashTimer.Start();
    }

    void EndFlash()
    {
        _flashTimer.Stop();
        _flashOn = false;
        _flashClick = null;
        _flashUndo = null;
        if (OnCall)
        {
            SetCallSub(CallBookedLine(), _callBooked is null ? null : UndoCallBooking);
            return;
        }
        _flashPanel.Visibility = Visibility.Collapsed;
        _rings.Visibility = Visibility.Visible;
        DockKit.RiseIn(_rings, 0);
        RefreshToday();
        if (_mode != DockMode.Moment) Refit();
    }

    // ---- the on-call capsule -----------------------------------------------------------------

    void BuildCall()
    {
        var dot = new Ellipse { Width = 9, Height = 9, Fill = DockPalette.OnCall, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _callName = DockKit.Text("", 14, null, FontWeights.SemiBold);
        _callName.MaxWidth = 170;
        _callSub = DockKit.Text("", 11.5, DockPalette.Muted);
        _callSub.MaxWidth = 170;
        _callUndo = DockKit.Button("בטל", DockKit.Kind.Ghost, () => UndoCallBooking(), height: 22);
        _callUndo.Padding = new Thickness(6, 0, 6, 1);
        _callUndo.Visibility = Visibility.Collapsed;
        var subRow = new StackPanel { Orientation = Orientation.Horizontal };
        subRow.Children.Add(_callSub);
        subRow.Children.Add(_callUndo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_callName);
        names.Children.Add(subRow);
        _callTimer = DockKit.Numeric("00:00", 14, DockPalette.OnCall, FontWeights.Medium);
        _callTimer.Margin = new Thickness(10, 0, 10, 0);
        _alarmButton = DockKit.IconButton(DockKit.IconClock, "קבע חזרה", OnCallAlarm, 38, null, DockKit.Kind.Primary);

        _callRow = new Grid { Margin = new Thickness(18, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        _callRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _callRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _callRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _callRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _callRow.Children.Add(dot);
        Grid.SetColumn(names, 1);
        _callRow.Children.Add(names);
        Grid.SetColumn(_callTimer, 2);
        _callRow.Children.Add(_callTimer);
        Grid.SetColumn(_alarmButton, 3);
        _callRow.Children.Add(_alarmButton);
    }

    void BeginCallCapsule()
    {
        string? name = null;
        try
        {
            name = ClientIndex.NameForPhone(CallbackStore.Load(), _callNumber);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock caller lookup failed: {ex.Message}");
        }
        _callDisplayName = name ?? _callNumber;
        if (_callNumber is not null || name is not null) _lastClient = new QuickPerson(name ?? _callNumber!, _callNumber);
        DockKit.SetText(_callName, _callDisplayName ?? "בשיחה");
        if (_callName.FlowDirection == FlowDirection.LeftToRight) _callName.HorizontalAlignment = HorizontalAlignment.Right;
        SetCallSub(CallBookedLine(), null);
        UpdateCallTimer();
        RebuildPins();
    }

    void EndCallCapsule()
    {
        _lastCallBooking = _callBooked; // the after-call card adjusts this one instead of adding another
        _callBooked = null;
        _chipsTimer = null;
        _alarmButton.ToolTip = "קבע חזרה";
        DockKit.Paint(_alarmButton, DockKit.Kind.Primary);
        ((ShapePath)_alarmButton.Child).Stroke = DockPalette.OnPrimary;
        if (_flashOn) EndFlash();
    }

    void UpdateCallTimer()
    {
        if (!OnCall) return;
        var text = DockText.Timer(DateTime.UtcNow - _callStartedUtc); // the only thing that moves on a call
        _callTimer.Text = text;
        if (_chipsTimer is not null) _chipsTimer.Text = text;
    }

    string CallBookedLine() =>
        _callBooked is { } b ? $"חזרה · {DockText.When(b.DueAtUtc.ToLocalTime(), DateTime.Now)}"
        : _callNumber is not null && _callDisplayName != _callNumber ? _callNumber : "⏰ לחיצה אחת קובעת חזרה";

    void SetCallSub(string text, Action? undo)
    {
        DockKit.SetText(_callSub, text);
        _callSub.ToolTip = text.Length > 30 ? text : null; // a long caller brief reads in full on hover
        _callSub.Foreground = _callBooked is not null && undo is not null ? DockPalette.Done : DockPalette.Muted;
        _callUndo.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>⏰: the first press books the smart default for the caller
    /// (heard time, else tomorrow 10:00); the next press opens the chips.</summary>
    void OnCallAlarm()
    {
        if (_callBooked is null)
        {
            var now = DateTime.Now;
            var when = DockQuick.ApplyRules(DockQuick.SmartDefault(null, now), RulesFor(_callDisplayName, _callNumber));
            _callBooked = BookFor(_callDisplayName, _callNumber, when, "חזרה משיחה");
            if (_callBooked is null)
            {
                SetCallSub("לא הצלחתי לקבוע", null);
                return;
            }
            SetCallSub(CallBookedLine(), UndoCallBooking);
            _alarmButton.ToolTip = "עוד זמנים לחזרה";
            DockKit.Paint(_alarmButton, DockKit.Kind.Secondary);
            ((ShapePath)_alarmButton.Child).Stroke = DockPalette.Text;
            RefreshToday();
            return;
        }
        if (_moment == MomentKind.CallChips) SetMode(DockMode.Rest);
        else ShowCallChips();
    }

    void UndoCallBooking()
    {
        if (_callBooked is { } b)
        {
            try
            {
                CallbackStore.Remove(b.Id);
            }
            catch (Exception ex)
            {
                Log.Write($"Dock call undo failed: {ex.Message}");
            }
        }
        _callBooked = null;
        _alarmButton.ToolTip = "קבע חזרה";
        DockKit.Paint(_alarmButton, DockKit.Kind.Primary);
        ((ShapePath)_alarmButton.Child).Stroke = DockPalette.OnPrimary;
        SetCallSub(CallBookedLine(), null);
        if (_moment == MomentKind.CallChips) SetMode(DockMode.Rest);
        RefreshToday();
    }

    /// <summary>Adds a callback for someone (name and/or phone). Null on failure.</summary>
    static Callback? BookFor(string? name, string? phone, DateTime whenLocal, string note, string? callNoteId = null)
    {
        try
        {
            var isNumber = name is not null && name.Any(char.IsAsciiDigit) && CallbackStore.NormalizePhone(name) is not null;
            return CallbackStore.Add(note, whenLocal.ToUniversalTime(),
                name: isNumber ? null : name, phone: phone ?? (isNumber ? name : null),
                source: CallbackSource.Manual, callNoteId: callNoteId);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock booking failed: {ex.Message}");
            return null;
        }
    }

    static IReadOnlyList<Palon.Memory.TimeRule> RulesFor(string? name, string? phone)
    {
        try
        {
            return Palon.Memory.MemoryStore.RulesFor(name, phone);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock rules read failed: {ex.Message}");
            return Array.Empty<Palon.Memory.TimeRule>();
        }
    }
}

/// <summary>Three concentric progress rings (calls · deposits · callbacks), drawn once per change — never animated.</summary>
sealed class DockRingsView : FrameworkElement
{
    readonly double[] _p = new double[3];

    public void Set(double calls, double deposits, double callbacks)
    {
        _p[0] = calls;
        _p[1] = deposits;
        _p[2] = callbacks;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var radii = new[] { 10.5, 7, 3.5 };
        for (var i = 0; i < 3; i++)
        {
            var r = radii[i];
            var track = new Pen(DockPalette.RingTrack, 2.4);
            dc.DrawEllipse(null, track, c, r, r);
            var p = Math.Clamp(_p[i], 0, 1);
            if (p <= 0) continue;
            var pen = new Pen(DockPalette.Rings[i], 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (p >= 0.999)
            {
                dc.DrawEllipse(null, pen, c, r, r);
                continue;
            }
            var a = p * 2 * Math.PI;
            var start = new Point(c.X, c.Y - r);
            var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(new[] { fig });
            dc.DrawGeometry(null, pen, geo);
        }
    }
}
