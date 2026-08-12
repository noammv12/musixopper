using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Saley.Interop;
using ShapePath = System.Windows.Shapes.Path;
using WinF = System.Windows.Forms;

namespace Saley.UI;

enum DockState
{
    Collapsed,
    Expanded,
    Toast,
    Reminder,
    Dictation,
}

/// <summary>
/// The Saley dock: a small always-on-top capsule that lives just above the
/// taskbar. Collapsed it's a quiet sliver with a status dot; on hover it
/// expands to show status and snippet chips (click = paste into the focused
/// app); on call events it self-expands as a toast for two seconds.
/// It never takes activation (WS_EX_NOACTIVATE | TOOLWINDOW) — that's what
/// makes click-to-paste possible. Only ever Hide() it, never Close().
/// </summary>
sealed class DockWindow : Window
{
    // Collapsed sliver / expanded pill metrics (DIP).
    const double CollapsedWidth = 36;
    const double CollapsedHeight = 10;
    const double ExpandedHeight = 40;
    const double ToastHeight = 36;
    const double WindowWidth = 640;
    const double WindowHeight = 76;
    const double BottomGap = 4;    // pill bottom to work-area bottom
    const double EdgeKeepIn = 60;  // min px between pill center and screen edge
    const double RestingOpacity = 0.7;
    const double DisabledOpacity = 0.45;

    static readonly Geometry PauseGlyph = Geometry.Parse("M0,0 H3.6 V11 H0 Z M6.4,0 H10 V11 H6.4 Z");
    static readonly Geometry PlayGlyph = Geometry.Parse("M0,0 L10,5.5 L0,11 Z");
    // The Saley S at 14 px — same two tangent arcs as the app icon.
    static readonly Geometry SMark = Geometry.Parse("M8.45,3.49 A2.06,2.06 0 1 0 7,7 A2.06,2.06 0 1 1 5.55,10.51");

    readonly Border _pill;
    readonly Ellipse _collapsedDot;
    readonly StackPanel _expandedContent;
    readonly StackPanel _toastContent;
    readonly Ellipse _statusDot;
    readonly TextBlock _statusText;
    readonly StackPanel _chipsPanel;
    readonly ShapePath _toastIcon;
    readonly TextBlock _toastText;
    readonly StackPanel _reminderContent;
    readonly TextBlock _reminderLabel;
    readonly TextBlock _reminderCount;
    readonly StackPanel _dictationContent;
    readonly Ellipse _dictationDot;

    readonly DispatcherTimer _hoverIntent;
    readonly DispatcherTimer _collapseDelay;
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _fullscreenPoll;
    readonly DispatcherTimer _callTicker;
    DateTime _callStartedUtc;

    DockState _state = DockState.Collapsed;
    CallState _callState = CallState.Idle;
    bool _fullscreenHidden;
    Action? _toastAction;
    string _processingStatus = "";

    // drag
    bool _dragging;
    int _dragStartCursorX;
    double _dragStartLeft;
    double _dragScale = 1;

    const int DictationHotkeyId = 0xA11;

    public event Action? OpenFlyoutRequested;
    public event Action? OpenRemindersRequested;
    public event Action? DictationToggleRequested;
    public event Action? DictationCancelRequested;
    bool _dictationActive;
    public event Action<Reminder>? ReminderOpenRequested;
    public event Action<Reminder>? ReminderSnoozeRequested;
    public event Action<Reminder>? ReminderDismissRequested;

    readonly Queue<(Reminder Reminder, bool Missed)> _reminderQueue = new();
    (Reminder Reminder, bool Missed)? _currentReminder;

    public DockWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = WindowWidth;   // fixed HWND size; only the inner pill animates
        Height = WindowHeight; // (transparent pixels are click-through)
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif");

        _collapsedDot = new Ellipse { Width = 6, Height = 6 };
        _collapsedDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");

        // -- expanded content ------------------------------------------------
        var sMark = new ShapePath
        {
            Data = SMark,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sMark.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

        _statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _statusDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");
        _statusText = new TextBlock { Text = "Listening for calls", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");

        _chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _expandedContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 12, 0) };
        _expandedContent.Children.Add(sMark);
        _expandedContent.Children.Add(_statusDot);
        _expandedContent.Children.Add(_statusText);
        _expandedContent.Children.Add(divider);
        _expandedContent.Children.Add(_chipsPanel);

        // -- toast content ----------------------------------------------------
        _toastIcon = new ShapePath { Width = 10, Height = 11, VerticalAlignment = VerticalAlignment.Center };
        _toastIcon.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        _toastText = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _toastContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        _toastContent.Children.Add(_toastIcon);
        _toastContent.Children.Add(_toastText);
        _toastContent.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragging || _toastAction is not { } action) return;
            _toastAction = null;
            _toastTimer.Stop();
            SetState(RestState() == DockState.Dictation ? DockState.Dictation : DockState.Collapsed);
            action();
        };

        // -- reminder content ---------------------------------------------
        var phone = new TextBlock { Text = "📞", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _reminderLabel = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _reminderLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _reminderCount = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _reminderCount.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var openButton = ReminderButton("Open", primary: true);
        openButton.Margin = new Thickness(12, 0, 0, 0);
        openButton.MouseLeftButtonUp += (_, _) => ActOnCurrentReminder(r => ReminderOpenRequested?.Invoke(r));
        var snoozeButton = ReminderButton("10m", primary: false);
        snoozeButton.MouseLeftButtonUp += (_, _) => ActOnCurrentReminder(r => ReminderSnoozeRequested?.Invoke(r));
        var dismissButton = ReminderButton("✕", primary: false);
        dismissButton.MouseLeftButtonUp += (_, _) => ActOnCurrentReminder(r => ReminderDismissRequested?.Invoke(r));

        _reminderContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        _reminderContent.Children.Add(phone);
        _reminderContent.Children.Add(_reminderLabel);
        _reminderContent.Children.Add(_reminderCount);
        _reminderContent.Children.Add(openButton);
        _reminderContent.Children.Add(snoozeButton);
        _reminderContent.Children.Add(dismissButton);

        // -- dictation content ----------------------------------------------
        _dictationDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _dictationDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");
        var dictationText = new TextBlock
        {
            Text = "Listening — Ctrl+Alt+D to finish",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        dictationText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var dictationCancel = ReminderButton("✕", primary: false);
        dictationCancel.Margin = new Thickness(12, 0, 0, 0);
        dictationCancel.MouseLeftButtonUp += (_, _) => DictationCancelRequested?.Invoke();
        _dictationContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        _dictationContent.Children.Add(_dictationDot);
        _dictationContent.Children.Add(dictationText);
        _dictationContent.Children.Add(dictationCancel);

        var host = new Grid();
        host.Children.Add(_collapsedDot);
        host.Children.Add(_expandedContent);
        host.Children.Add(_toastContent);
        host.Children.Add(_reminderContent);
        host.Children.Add(_dictationContent);

        _pill = new Border
        {
            Width = CollapsedWidth,
            Height = CollapsedHeight,
            CornerRadius = new CornerRadius(5),
            Opacity = RestingOpacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(20, 20, 20, BottomGap),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            Child = host,
        };
        _pill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pill.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeBrush");
        Content = _pill;

        ApplyContentVisibility();

        _hoverIntent = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverIntent.Tick += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_pill.IsMouseOver && _state == DockState.Collapsed) SetState(DockState.Expanded);
        };
        _collapseDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            if (!_pill.IsMouseOver && _state == DockState.Expanded) SetState(DockState.Collapsed);
        };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_state == DockState.Toast) SetState(RestState());
        };
        _fullscreenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fullscreenPoll.Tick += (_, _) => UpdateFullscreenHidden();
        _callTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _callTicker.Tick += (_, _) => UpdateStatusText();

        _pill.MouseEnter += (_, _) =>
        {
            if (_state is DockState.Reminder or DockState.Dictation) return; // persistent states
            _collapseDelay.Stop();
            if (_state == DockState.Collapsed) _hoverIntent.Start();
            else if (_state == DockState.Toast)
            {
                _toastTimer.Stop();
                SetState(DockState.Expanded);
            }
        };
        _pill.MouseLeave += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_state == DockState.Expanded) _collapseDelay.Start();
        };
        _pill.MouseLeftButtonDown += OnPillMouseDown;
        _pill.MouseMove += OnPillMouseMove;
        _pill.MouseLeftButtonUp += OnPillMouseUp;

        SnippetStore.Changed += RefreshChips;
        RefreshChips();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

            if (!NativeMethods.RegisterHotKey(hwnd, DictationHotkeyId,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x44 /* D */))
                Log.Write("Dictation hotkey Ctrl+Alt+D unavailable (taken by another app)");
            if (HwndSource.FromHwnd(hwnd) is { } source) source.AddHook(WndProc);
        };
    }

    // ---- lifecycle -----------------------------------------------------------

    public void ShowDock()
    {
        Show();
        Reposition();
        _fullscreenPoll.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    public void Shutdown()
    {
        try
        {
            NativeMethods.UnregisterHotKey(new WindowInteropHelper(this).Handle, DictationHotkeyId);
        }
        catch
        {
        }
        _hoverIntent.Stop();
        _collapseDelay.Stop();
        _toastTimer.Stop();
        _fullscreenPoll.Stop();
        _callTicker.Stop();
        SnippetStore.Changed -= RefreshChips;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Hide();
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reposition);

    // ---- engine hooks ----------------------------------------------------------

    public void SyncState(CallState state)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SyncState(state));
            return;
        }
        var wasOnCall = _callState == CallState.OnCall;
        _callState = state;
        if (state == CallState.OnCall && !wasOnCall)
        {
            _callStartedUtc = DateTime.UtcNow;
            _callTicker.Start();
        }
        else if (state != CallState.OnCall)
        {
            _callTicker.Stop();
        }
        var dotKey = state switch
        {
            CallState.OnCall => "AmberBrush",
            CallState.Disabled => "TextSecondaryBrush",
            _ => "StatusGoodBrush",
        };
        _collapsedDot.SetResourceReference(Shape.FillProperty, dotKey);
        _statusDot.SetResourceReference(Shape.FillProperty, dotKey);
        UpdateStatusText();
        if (_state == DockState.Collapsed)
            _pill.BeginAnimation(OpacityProperty, Motion.Fade(RestingOpacityFor(), Motion.Fast));
    }

    /// <summary>Shown in the expanded status line while a note is being processed.</summary>
    public void SetProcessingStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetProcessingStatus(status));
            return;
        }
        _processingStatus = status;
        UpdateStatusText();
    }

    void UpdateStatusText()
    {
        _statusText.Text = _callState switch
        {
            CallState.OnCall => (DateTime.UtcNow - _callStartedUtc) is { TotalHours: >= 1 } elapsed
                ? $"On a call — {elapsed:hh\\:mm\\:ss}"
                : $"On a call — {DateTime.UtcNow - _callStartedUtc:mm\\:ss}",
            CallState.Disabled => "Paused",
            _ => _processingStatus.Length > 0 ? _processingStatus : "Listening for calls",
        };
    }

    public void ShowToast(string text, bool paused, Action? onClick = null, bool showIcon = true, bool important = false)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(text, paused, onClick, showIcon, important));
            return;
        }
        if (_fullscreenHidden || !IsVisible) return;
        if (_state is DockState.Reminder or DockState.Dictation) return; // persistent states outrank toasts
        // Pause/resume toasts are redundant while expanded (the status line
        // says it) — but actionable or important toasts must never be dropped:
        // the user hovering the dock is exactly who's waiting for the outcome.
        if (_state == DockState.Expanded && onClick is null && !important) return;

        _toastText.Text = text;
        _toastIcon.Data = paused ? PauseGlyph : PlayGlyph;
        _toastIcon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
        _toastAction = onClick;
        _toastContent.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;

        _toastTimer.Stop();
        _toastTimer.Start();
        if (_state == DockState.Toast)
        {
            // Toast replacing a toast: resize the pill for the new text.
            AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, 150, Motion.Out);
            return;
        }
        SetState(DockState.Toast);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == DictationHotkeyId)
        {
            DictationToggleRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---- dictation --------------------------------------------------------------

    public void SetDictation(bool active)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetDictation(active));
            return;
        }
        _dictationActive = active;
        if (active)
        {
            if (_state != DockState.Reminder) SetState(DockState.Dictation);
        }
        else if (_state == DockState.Dictation)
        {
            SetState(_pill.IsMouseOver ? DockState.Expanded : DockState.Collapsed);
        }
    }

    void StartDictationPulse() => _dictationDot.BeginAnimation(OpacityProperty,
        new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

    void StopDictationPulse()
    {
        _dictationDot.BeginAnimation(OpacityProperty, null);
        _dictationDot.Opacity = 1;
    }

    /// <summary>Where the dock settles when a transient state ends.</summary>
    DockState RestState() =>
        _dictationActive ? DockState.Dictation
        : _pill.IsMouseOver ? DockState.Expanded
        : DockState.Collapsed;

    // ---- reminders -------------------------------------------------------------

    public void ShowReminder(Reminder reminder, bool missed)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowReminder(reminder, missed));
            return;
        }
        _reminderQueue.Enqueue((reminder, missed));
        TryShowReminder();
    }

    void TryShowReminder()
    {
        if (_fullscreenHidden || !IsVisible)
        {
            return; // held in the queue; surfaced when the dock returns
        }
        if (_state == DockState.Reminder)
        {
            UpdateReminderContent();
            return;
        }
        if (_reminderQueue.Count == 0 || _currentReminder is not null) return;
        _currentReminder = _reminderQueue.Dequeue();
        UpdateReminderContent();
        _toastTimer.Stop();
        SetState(DockState.Reminder);
    }

    void ActOnCurrentReminder(Action<Reminder> action)
    {
        if (_currentReminder is not { } current) return;
        _currentReminder = null;
        action(current.Reminder);
        if (_reminderQueue.Count > 0)
        {
            _currentReminder = _reminderQueue.Dequeue();
            UpdateReminderContent();
            AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, 150, Motion.Out);
            return;
        }
        SetState(RestState());
    }

    void UpdateReminderContent()
    {
        if (_currentReminder is not { } current) return;
        _reminderLabel.Text = (current.Missed ? "Missed · " : "") + current.Reminder.DisplayLabel;
        _reminderCount.Text = _reminderQueue.Count > 0 ? $"+{_reminderQueue.Count}" : "";
        if (_state == DockState.Reminder)
            AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, 150, Motion.Out);
    }

    Border ReminderButton(string text, bool primary)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, primary ? "OnAccentBrush" : "TextPrimaryBrush");
        var button = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        if (primary)
        {
            button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            button.MouseEnter += (_, _) => button.Opacity = 0.9;
            button.MouseLeave += (_, _) => button.Opacity = 1.0;
        }
        else
        {
            button.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
            Ui.HoverFill(button);
        }
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return button;
    }

    // ---- chips -----------------------------------------------------------------

    void RefreshChips()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshChips);
            return;
        }
        _chipsPanel.Children.Clear();
        foreach (var snippet in SnippetStore.Load().Take(5))
            _chipsPanel.Children.Add(MakeChip(snippet));

        var dictate = MakeChipShell("🎙");
        dictate.ToolTip = "Dictate (Ctrl+Alt+D) — speak, and the text is typed where your cursor is";
        dictate.MouseLeftButtonUp += (_, _) => DictationToggleRequested?.Invoke();
        _chipsPanel.Children.Add(dictate);

        var remind = MakeChipShell("⏰");
        remind.ToolTip = "Remind me to call someone back";
        remind.MouseLeftButtonUp += (_, _) => OpenRemindersRequested?.Invoke();
        _chipsPanel.Children.Add(remind);

        var more = MakeChipShell("…");
        more.MouseLeftButtonUp += (_, _) => OpenFlyoutRequested?.Invoke();
        _chipsPanel.Children.Add(more);

        if (_state == DockState.Expanded) AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, 150, Motion.Out);
    }

    Border MakeChip(Snippet snippet)
    {
        var chip = MakeChipShell(snippet.Label.Length > 0 ? snippet.Label : "(untitled)");
        var label = (TextBlock)chip.Child;
        chip.ToolTip = snippet.Text.Length > 120 ? snippet.Text[..120] + "…" : snippet.Text;

        chip.MouseLeftButtonUp += async (_, _) =>
        {
            var result = await SnippetPaster.PasteAsync(snippet);
            Feedback(chip, label, result switch
            {
                PasteResult.Pasted => "Pasted ✓",
                PasteResult.CopiedOnly => "Copied ✓",
                _ => "Failed",
            }, snippet.Label);
        };
        chip.MouseRightButtonUp += (_, _) =>
        {
            var result = SnippetPaster.CopyOnly(snippet);
            Feedback(chip, label, result == PasteResult.Failed ? "Failed" : "Copied ✓", snippet.Label);
        };
        return chip;
    }

    Border MakeChipShell(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            MaxWidth = 110,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 4, 10, 5),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(chip);
        // Chips never start a pill drag.
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return chip;
    }

    static void Feedback(Border chip, TextBlock label, string message, string original)
    {
        chip.MinWidth = chip.ActualWidth; // keep the pill from jumping while the text swaps
        label.Text = message;
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            label.Text = original.Length > 0 ? original : "(untitled)";
            chip.MinWidth = 0;
        };
        revert.Start();
    }

    // ---- state & animation --------------------------------------------------------

    void SetState(DockState state)
    {
        if (_state == state) return;
        if (_state == DockState.Dictation) StopDictationPulse();
        _state = state;
        ApplyContentVisibility();

        switch (state)
        {
            case DockState.Collapsed:
                _pill.CornerRadius = new CornerRadius(5);
                AnimatePillTo(CollapsedWidth, CollapsedHeight, Motion.Base, Motion.InOut);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(RestingOpacityFor(), Motion.Base, Motion.InOut));
                break;
            case DockState.Expanded:
                Reposition();
                _pill.CornerRadius = new CornerRadius(20);
                AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_expandedContent);
                break;
            case DockState.Toast:
                _pill.CornerRadius = new CornerRadius(18);
                AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, Motion.Base, Motion.Out);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_toastContent);
                break;
            case DockState.Reminder:
                Reposition();
                _pill.CornerRadius = new CornerRadius(20);
                AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_reminderContent);
                break;
            case DockState.Dictation:
                Reposition();
                _pill.CornerRadius = new CornerRadius(20);
                AnimatePillTo(MeasureWidth(_dictationContent), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_dictationContent);
                StartDictationPulse();
                break;
        }
    }

    void ApplyContentVisibility()
    {
        _collapsedDot.Visibility = _state == DockState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _expandedContent.Visibility = _state == DockState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _toastContent.Visibility = _state == DockState.Toast ? Visibility.Visible : Visibility.Collapsed;
        _reminderContent.Visibility = _state == DockState.Reminder ? Visibility.Visible : Visibility.Collapsed;
        _dictationContent.Visibility = _state == DockState.Dictation ? Visibility.Visible : Visibility.Collapsed;
    }

    double RestingOpacityFor() => _callState == CallState.Disabled ? DisabledOpacity : RestingOpacity;

    double MeasureExpandedWidth() => MeasureWidth(_expandedContent);

    double MeasureWidth(UIElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Min(content.DesiredSize.Width + 8, WindowWidth - 40);
    }

    void AnimatePillTo(double width, double height, int ms, IEasingFunction ease)
    {
        _pill.BeginAnimation(WidthProperty, Motion.FromTo(_pill.ActualWidth, width, ms, ease));
        _pill.BeginAnimation(HeightProperty, Motion.FromTo(_pill.ActualHeight, height, ms, ease));
    }

    static void PrepareFade(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
    }

    void FadeInContent(UIElement content)
    {
        PrepareFade(content);
        var fadeIn = Motion.FromTo(0, 1, Motion.Fast);
        fadeIn.BeginTime = TimeSpan.FromMilliseconds(60);
        content.BeginAnimation(OpacityProperty, fadeIn);
    }

    // ---- drag --------------------------------------------------------------------

    void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out var pt);
        _dragStartCursorX = pt.X;
        _dragStartLeft = Left;
        _dragScale = CurrentScale();
        _dragging = false;
        _pill.CaptureMouse();
    }

    void OnPillMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pill.IsMouseCaptured) return;
        NativeMethods.GetCursorPos(out var pt);
        var deltaPx = pt.X - _dragStartCursorX;
        if (!_dragging && Math.Abs(deltaPx) <= SystemParameters.MinimumHorizontalDragDistance) return;
        _dragging = true;
        Left = ClampLeft(_dragStartLeft + deltaPx / _dragScale);
    }

    void OnPillMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pill.IsMouseCaptured) return;
        _pill.ReleaseMouseCapture();
        if (_dragging)
        {
            _dragging = false;
            PersistDockX();
        }
        else if (_state == DockState.Collapsed)
        {
            _hoverIntent.Stop();
            SetState(DockState.Expanded); // click beats the hover-intent wait
        }
    }

    // ---- positioning ----------------------------------------------------------------

    void Reposition()
    {
        if (_dragging) return;
        var wa = (WinF.Screen.PrimaryScreen ?? WinF.Screen.AllScreens[0]).WorkingArea;
        var scale = Dpi.MoveToAndGetScale(this, wa);

        double windowWidthPx = Width * scale;
        double windowHeightPx = Height * scale;
        double centerPx = wa.Left + Settings.DockX * wa.Width;
        centerPx = Math.Clamp(centerPx, wa.Left + EdgeKeepIn * scale, wa.Right - EdgeKeepIn * scale);

        Left = (centerPx - windowWidthPx / 2) / scale;
        Top = (wa.Bottom - windowHeightPx) / scale;
    }

    double ClampLeft(double left)
    {
        var wa = (WinF.Screen.PrimaryScreen ?? WinF.Screen.AllScreens[0]).WorkingArea;
        var scale = _dragScale;
        double centerPx = left * scale + Width * scale / 2;
        centerPx = Math.Clamp(centerPx, wa.Left + EdgeKeepIn * scale, wa.Right - EdgeKeepIn * scale);
        return (centerPx - Width * scale / 2) / scale;
    }

    void PersistDockX()
    {
        var wa = (WinF.Screen.PrimaryScreen ?? WinF.Screen.AllScreens[0]).WorkingArea;
        var scale = CurrentScale();
        double centerPx = Left * scale + Width * scale / 2;
        if (wa.Width > 0) Settings.DockX = (centerPx - wa.Left) / wa.Width;
    }

    double CurrentScale()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        return scale <= 0 ? 1 : scale;
    }

    // ---- fullscreen ------------------------------------------------------------------

    void UpdateFullscreenHidden()
    {
        var hide = false;
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var quns) == 0)
            {
                hide = quns is NativeMethods.QUNS_BUSY
                    or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
                    or NativeMethods.QUNS_PRESENTATION_MODE;
            }
        }
        catch
        {
            // Treat failures as "not fullscreen".
        }
        if (hide == _fullscreenHidden) return;
        _fullscreenHidden = hide;
        Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        if (!hide)
        {
            Reposition();
            TryShowReminder();
        }
    }
}
