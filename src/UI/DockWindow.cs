using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Interop;
using ShapePath = System.Windows.Shapes.Path;
using WinF = System.Windows.Forms;

namespace Palon.UI;

enum DockState
{
    Collapsed,
    Expanded,
    Toast,
    Reminder,
    Dictation,
}

/// <summary>
/// The Palon dock: a small always-on-top capsule that lives just above the
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
    // The Palon P at 14 px — stem + one right bowl, same geometry as the app icon.
    static readonly Geometry PMark = Geometry.Parse("M5.78,2.89 L5.78,11.11 M5.78,2.89 A2.28,2.28 0 0 1 5.78,7.44");

    readonly Border _pill;
    readonly Border _glassSheen;
    readonly ScaleTransform _pillScale = new(1, 1);
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
    TextBlock _reminderOpenLabel = null!;
    readonly StackPanel _dictationContent;
    readonly Ellipse _dictationDot;
    readonly StackPanel _wavePanel;
    readonly Border[] _waveBars = new Border[5];
    readonly TextBlock _assistantGlyph;
    readonly TextBlock _dictationText;

    // Waveform: center-weighted bar profile, level smoothed with fast
    // attack / slow decay so speech snaps up and settles down.
    static readonly double[] WaveWeights = { 0.6, 0.85, 1, 0.85, 0.6 };
    const double WaveMinHeight = 4;
    const double WaveMaxHeight = 18;
    bool _waveActive;
    double _voiceLevel;
    int _waveTick;

    readonly DispatcherTimer _hoverIntent;
    readonly DispatcherTimer _collapseDelay;
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _fullscreenPoll;
    readonly DispatcherTimer _repositionDebounce;
    readonly DispatcherTimer _callTicker;
    DateTime _callStartedUtc;
    int _pollTicks;

    DockState _state = DockState.Collapsed;
    CallState _callState = CallState.Idle;
    bool _fullscreenHidden;
    Action? _toastAction;
    string _notesStatus = "";
    string _dictationStatus = "";
    (string Text, bool Paused, Action? OnClick, bool ShowIcon)? _pendingToast;

    // drag
    bool _dragging;
    int _dragStartCursorX;
    double _dragStartLeft;
    double _dragScale = 1;

    const int DictationHotkeyId = 0xA11;
    Hotkey _dictationHotkey = Hotkey.LoadDictation();
    bool _dictationHotkeyFailed;

    const int AssistantHotkeyId = 0xA12;
    Hotkey _assistantHotkey = Hotkey.LoadAssistant();
    bool _assistantHotkeyFailed;

    const int SnippetHotkeyBase = 0xA21; // ids 0xA21–0xA29 = Ctrl+Alt+1–9
    readonly Snippet?[] _hotkeySnippets = new Snippet?[9];

    public event Action? OpenFlyoutRequested;
    public event Action? OpenRemindersRequested;
    public event Action? OpenNotesRequested;
    public event Action? DictationToggleRequested;
    public event Action? DictationCancelRequested;
    public event Action? AssistantToggleRequested;
    public event Action? AssistantCancelRequested;
    bool _dictationActive;
    bool _assistantActive;
    string _assistantStatus = "";
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
        FontFamily = Font.Family;
        // Same deal as the flyout: keep the pill's 1px stroke on device pixels.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _collapsedDot = new Ellipse { Width = 6, Height = 6 };
        _collapsedDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");

        // -- expanded content ------------------------------------------------
        var pMark = new ShapePath
        {
            Data = PMark,
            StrokeThickness = 1.75,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pMark.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

        _statusDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _statusDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");
        _statusText = new TextBlock { Text = "Listening for calls", FontSize = Font.Body, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");

        _chipsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _expandedContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 12, 0) };
        _expandedContent.Children.Add(pMark);
        _expandedContent.Children.Add(_statusDot);
        _expandedContent.Children.Add(_statusText);
        _expandedContent.Children.Add(divider);
        _expandedContent.Children.Add(_chipsPanel);

        // -- toast content ----------------------------------------------------
        _toastIcon = new ShapePath { Width = 10, Height = 11, VerticalAlignment = VerticalAlignment.Center };
        _toastIcon.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        // Medium, not SemiBold: the sanctioned exception — on the compact pill
        // Medium at Lead size reads better (see AUDIT.md). MaxWidth sits just
        // under the pill's 600px clamp so a long line ellipsizes instead of
        // hard-clipping mid-glyph.
        _toastText = new TextBlock
        {
            FontSize = Font.Lead,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 540,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _toastContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        _toastContent.Children.Add(_toastIcon);
        _toastContent.Children.Add(_toastText);
        // Like the chips (:939): an actionable toast must claim the press,
        // or _pill.CaptureMouse() routes the whole gesture to the pill and
        // this MouseLeftButtonUp never fires — the click was silently inert.
        // Actionless toasts stay draggable.
        _toastContent.MouseLeftButtonDown += (_, e) =>
        {
            if (_toastAction is not null) e.Handled = true;
        };
        _toastContent.MouseLeftButtonUp += (_, _) =>
        {
            if (_dragging || _toastAction is not { } action) return;
            _toastAction = null;
            _toastTimer.Stop();
            SetState(RestState()); // hovered stays expanded instead of slamming shut
            action();
        };

        // -- reminder content ---------------------------------------------
        var phone = new TextBlock { Text = "📞", FontSize = Font.Body, VerticalAlignment = VerticalAlignment.Center };
        _reminderLabel = new TextBlock
        {
            FontSize = Font.Lead,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _reminderLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _reminderCount = new TextBlock { FontSize = Font.Small, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        _reminderCount.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var openButton = ReminderButton("Open", primary: true);
        _reminderOpenLabel = (TextBlock)openButton.Child;
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

        // -- dictation / assistant content ------------------------------------
        _dictationDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        _dictationDot.SetResourceReference(Shape.FillProperty, "StatusGoodBrush");
        // Wispr-style waveform — swapped in for the dot once mic levels flow.
        _wavePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        for (var i = 0; i < _waveBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = WaveMinHeight,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            _waveBars[i] = bar;
            _wavePanel.Children.Add(bar);
        }
        _assistantGlyph = new TextBlock
        {
            Text = "💬",
            FontSize = Font.Body,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _dictationText = new TextBlock
        {
            FontSize = Font.Lead,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _dictationText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var dictationFinish = ReminderButton("Finish", primary: true);
        dictationFinish.Margin = new Thickness(12, 0, 0, 0);
        dictationFinish.MouseLeftButtonUp += (_, _) =>
            (_assistantActive ? AssistantToggleRequested : DictationToggleRequested)?.Invoke();
        var dictationCancel = ReminderButton("✕", primary: false);
        dictationCancel.MouseLeftButtonUp += (_, _) =>
            (_assistantActive ? AssistantCancelRequested : DictationCancelRequested)?.Invoke();
        _dictationContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        _dictationContent.Children.Add(_dictationDot);
        _dictationContent.Children.Add(_wavePanel);
        _dictationContent.Children.Add(_assistantGlyph);
        _dictationContent.Children.Add(_dictationText);
        _dictationContent.Children.Add(dictationFinish);
        _dictationContent.Children.Add(dictationCancel);

        var host = new Grid();
        host.Children.Add(_collapsedDot);
        host.Children.Add(_expandedContent);
        host.Children.Add(_toastContent);
        host.Children.Add(_reminderContent);
        host.Children.Add(_dictationContent);
        _glassSheen = new Border { IsHitTestVisible = false, Margin = new Thickness(1) };
        _glassSheen.SetResourceReference(Border.BackgroundProperty, "GlassSheenBrush");
        host.Children.Add(_glassSheen); // light on the glass, over everything, never clickable

        _pill = new Border
        {
            Width = CollapsedWidth,
            Height = CollapsedHeight,
            Opacity = RestingOpacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(20, 20, 20, BottomGap),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            Effect = Ui.Shadow(),
            Child = host,
        };
        _pill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pill.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeBrush");
        _pill.RenderTransform = _pillScale;
        _pill.RenderTransformOrigin = new Point(0.5, 1); // pops up from the taskbar edge
        SetPillRadius(5);
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
        _fullscreenPoll.Tick += (_, _) =>
        {
            UpdateFullscreenHidden();
            // Cheap insurance against z-order theft: an app leaving
            // fullscreen (or another topmost tool) can end up above us.
            if (++_pollTicks % 5 == 0 && IsVisible) ReassertTopmost();
        };
        _repositionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _repositionDebounce.Tick += (_, _) =>
        {
            _repositionDebounce.Stop();
            Reposition();
        };
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
        SnippetStore.Changed += ApplySnippetHotkeys;
        RefreshHotkeyStrings();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

            if (HwndSource.FromHwnd(hwnd) is { } source) source.AddHook(WndProc);
            ApplyDictationHotkey();
            ApplyAssistantHotkey();
            ApplySnippetHotkeys();

            // A silently-dead hotkey reads as "hotkeys don't exist" — say it
            // out loud once. Held as an important toast until the dock shows.
            var taken = new List<string>(2);
            if (_dictationHotkeyFailed) taken.Add(_dictationHotkey.ToString());
            if (_assistantHotkeyFailed) taken.Add(_assistantHotkey.ToString());
            if (taken.Count > 0)
                ShowToast($"{string.Join(" and ", taken)} taken by another app — pick a different combo in settings",
                    paused: false, showIcon: false, important: true);
        };
    }

    /// <summary>
    /// (Re)binds the global Ask-Palon hotkey from Settings. Returns false
    /// when Windows refused the combo (already taken by another app).
    /// </summary>
    public bool ApplyAssistantHotkey()
    {
        _assistantHotkey = Hotkey.LoadAssistant();
        _assistantHotkeyFailed = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            RefreshHotkeyStrings();
            return true; // not sourced yet; SourceInitialized re-applies
        }
        NativeMethods.UnregisterHotKey(hwnd, AssistantHotkeyId);
        var ok = true;
        if (!_assistantHotkey.IsOff)
        {
            ok = NativeMethods.RegisterHotKey(hwnd, AssistantHotkeyId,
                _assistantHotkey.Modifiers | NativeMethods.MOD_NOREPEAT, _assistantHotkey.Vk);
            _assistantHotkeyFailed = !ok;
            if (!ok) Log.Write($"Assistant hotkey {_assistantHotkey} unavailable (taken by another app)");
        }
        RefreshHotkeyStrings();
        return ok;
    }

    bool AssistantHotkeyLive => !_assistantHotkey.IsOff && !_assistantHotkeyFailed;

    /// <summary>The bindings that actually registered (null = off or taken) —
    /// for UI that advertises hotkeys, so it never advertises a dead one.</summary>
    public (string? Ask, string? Dictate) LiveHotkeys() => (
        AssistantHotkeyLive ? _assistantHotkey.ToString() : null,
        DictationHotkeyLive ? _dictationHotkey.ToString() : null);

    /// <summary>
    /// (Re)binds Ctrl+Alt+1–9 to the first nine snippets when the opt-in
    /// setting is on. Combos another app owns are skipped silently — a
    /// snippet hotkey is a convenience, never worth an error state.
    /// </summary>
    public void ApplySnippetHotkeys()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ApplySnippetHotkeys);
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // not sourced yet; SourceInitialized applies

        for (var i = 0; i < _hotkeySnippets.Length; i++)
        {
            NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
            _hotkeySnippets[i] = null;
        }
        if (!Settings.SnippetHotkeys) return;

        var snippets = SnippetStore.Load();
        for (var i = 0; i < snippets.Count && i < _hotkeySnippets.Length; i++)
        {
            // vk 0x31 + i = '1'…'9'
            if (NativeMethods.RegisterHotKey(hwnd, SnippetHotkeyBase + i,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)(0x31 + i)))
                _hotkeySnippets[i] = snippets[i];
            else
                Log.Write($"Snippet hotkey Ctrl+Alt+{i + 1} unavailable (taken by another app)");
        }
    }

    /// <summary>
    /// (Re)binds the global dictation hotkey from Settings. Returns false
    /// when Windows refused the combo (already taken by another app); the
    /// dock's hint strings fall back to the on-screen buttons in that case.
    /// </summary>
    public bool ApplyDictationHotkey()
    {
        _dictationHotkey = Hotkey.LoadDictation();
        _dictationHotkeyFailed = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            RefreshHotkeyStrings();
            return true; // not sourced yet; SourceInitialized re-applies
        }
        NativeMethods.UnregisterHotKey(hwnd, DictationHotkeyId);
        var ok = true;
        if (!_dictationHotkey.IsOff)
        {
            ok = NativeMethods.RegisterHotKey(hwnd, DictationHotkeyId,
                _dictationHotkey.Modifiers | NativeMethods.MOD_NOREPEAT, _dictationHotkey.Vk);
            _dictationHotkeyFailed = !ok;
            if (!ok) Log.Write($"Dictation hotkey {_dictationHotkey} unavailable (taken by another app)");
        }
        RefreshHotkeyStrings();
        return ok;
    }

    /// <summary>
    /// Temporarily releases every global hotkey (dictation + snippets) so the
    /// flyout's capture box can receive those keystrokes itself. Restore with
    /// ApplyDictationHotkey + ApplySnippetHotkeys.
    /// </summary>
    public void SuspendHotkeys()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(SuspendHotkeys);
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.UnregisterHotKey(hwnd, DictationHotkeyId);
        NativeMethods.UnregisterHotKey(hwnd, AssistantHotkeyId);
        for (var i = 0; i < _hotkeySnippets.Length; i++)
        {
            NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
            _hotkeySnippets[i] = null;
        }
    }

    void RefreshHotkeyStrings()
    {
        UpdateListeningText();
        RefreshChips(); // the 🎙/💬 chip tooltips render the same bindings
    }

    void UpdateListeningText()
    {
        // 💬 marks Palon listening; the pulsing dot marks dictation — until
        // mic levels flow and the waveform takes the dot's place.
        _assistantGlyph.Visibility = _assistantActive ? Visibility.Visible : Visibility.Collapsed;
        _dictationDot.Visibility = _assistantActive || _waveActive ? Visibility.Collapsed : Visibility.Visible;
        _dictationText.Text =
            _assistantActive ? "Palon is listening — ask away"
            : DictationHotkeyLive ? $"Listening — {_dictationHotkey} to finish"
            : "Listening — click Finish when done";
    }

    bool DictationHotkeyLive => !_dictationHotkey.IsOff && !_dictationHotkeyFailed;

    // ---- lifecycle -----------------------------------------------------------

    public void ShowDock()
    {
        Show();
        Reposition();
        // Don't wait for the first 2 s poll tick — if the user is already
        // presenting/fullscreen (or the logon shell still reads busy), hide
        // now; and if we're hidden wrongly it re-checks on the next tick.
        UpdateFullscreenHidden();
        _fullscreenPoll.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // At logon the taskbar often hasn't reserved its strip yet, so the
        // first Reposition pins the pill under it — re-run once things settle.
        RepositionSoon(TimeSpan.FromSeconds(1.5));
        RepositionSoon(TimeSpan.FromSeconds(5));

        // Anything held before the window existed (e.g. the startup hotkey-
        // conflict warning) replays now — nothing else fires it at launch.
        if (_pendingToast is { } held)
        {
            _pendingToast = null;
            Dispatcher.InvokeAsync(
                () => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true),
                DispatcherPriority.Loaded);
        }
    }

    public void Shutdown()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.UnregisterHotKey(hwnd, DictationHotkeyId);
            NativeMethods.UnregisterHotKey(hwnd, AssistantHotkeyId);
            for (var i = 0; i < _hotkeySnippets.Length; i++)
                NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
        }
        catch
        {
        }
        _hoverIntent.Stop();
        _collapseDelay.Stop();
        _toastTimer.Stop();
        _fullscreenPoll.Stop();
        _callTicker.Stop();
        _repositionDebounce.Stop();
        SnippetStore.Changed -= RefreshChips;
        SnippetStore.Changed -= ApplySnippetHotkeys;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Hide();
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reposition);

    // SystemEvents raise on a worker thread — always hop to the dispatcher.
    // Unlock and RDP reconnect commonly change resolution/work area without
    // a DisplaySettingsChanged, and resume can shuffle the z-order.
    void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock
            or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            Dispatcher.InvokeAsync(ResyncPresentation);
    }

    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.InvokeAsync(ResyncPresentation);
    }

    void ResyncPresentation()
    {
        Reposition();
        UpdateFullscreenHidden();
        ReassertTopmost();
    }

    void RepositionSoon(TimeSpan delay)
    {
        var once = new DispatcherTimer { Interval = delay };
        once.Tick += (_, _) =>
        {
            once.Stop();
            Reposition();
            UpdateFullscreenHidden();
        };
        once.Start();
    }

    void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

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
            SettleCollapsedOpacity(Motion.Fast);
    }

    /// <summary>Shown in the expanded status line while a note is being processed.</summary>
    public void SetNotesStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetNotesStatus(status));
            return;
        }
        _notesStatus = status;
        UpdateStatusText();
    }

    public void SetDictationStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetDictationStatus(status));
            return;
        }
        _dictationStatus = status;
        UpdateStatusText();
    }

    public void SetAssistantStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetAssistantStatus(status));
            return;
        }
        _assistantStatus = status;
        UpdateStatusText();
    }

    void UpdateStatusText()
    {
        var text = _callState switch
        {
            CallState.OnCall => (DateTime.UtcNow - _callStartedUtc) is { TotalHours: >= 1 } elapsed
                ? $"On a call — {elapsed:hh\\:mm\\:ss}"
                : $"On a call — {DateTime.UtcNow - _callStartedUtc:mm\\:ss}",
            CallState.Disabled => "Paused",
            _ => _assistantStatus.Length > 0 ? _assistantStatus
                : _dictationStatus.Length > 0 ? _dictationStatus
                : _notesStatus.Length > 0 ? _notesStatus
                : "Listening for calls",
        };
        if (_statusText.Text == text) return;
        _statusText.Text = text;
        // The call timer re-renders every second — pulsing then would flicker.
        if (_callState != CallState.OnCall)
            _statusText.BeginAnimation(OpacityProperty, Motion.FromTo(0.35, 1, Motion.Base));
    }

    public void ShowToast(string text, bool paused, Action? onClick = null, bool showIcon = true, bool important = false)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(text, paused, onClick, showIcon, important));
            return;
        }
        if (_fullscreenHidden || !IsVisible || _state is DockState.Reminder or DockState.Dictation)
        {
            // Blocked right now — important toasts are held and replayed when
            // the dock returns to a normal state, never dropped.
            if (important || onClick is not null) _pendingToast = (text, paused, onClick, showIcon);
            return;
        }
        // Pause/resume toasts are redundant while expanded (the status line
        // says it) — but actionable or important toasts must never be dropped:
        // the user hovering the dock is exactly who's waiting for the outcome.
        if (_state == DockState.Expanded && onClick is null && !important) return;

        void Apply()
        {
            _toastText.Text = text;
            _toastIcon.Data = paused ? PauseGlyph : PlayGlyph;
            _toastIcon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            _toastAction = onClick;
            _toastContent.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;
        }

        _toastTimer.Stop();
        _toastTimer.Start();
        if (_state == DockState.Toast)
        {
            // Toast replacing a toast: dip the old line out, swap, fade the
            // new one in as the pill resizes — text never teleports mid-morph.
            // A third toast during the dip simply replaces this animation.
            var fadeOut = Motion.Fade(0, Motion.Exit);
            fadeOut.Completed += (_, _) =>
            {
                Apply();
                AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, Motion.Fast, Motion.Out);
                _toastContent.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, Motion.Fast));
            };
            _toastContent.BeginAnimation(OpacityProperty, fadeOut);
            return;
        }
        Apply();
        SetState(DockState.Toast);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            // These arrive in bursts (taskbar auto-hide toggles, resolution
            // switches) — coalesce into one reposition.
            _repositionDebounce.Stop();
            _repositionDebounce.Start();
        }
        else if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == DictationHotkeyId)
            {
                DictationToggleRequested?.Invoke();
                handled = true;
            }
            else if (id == AssistantHotkeyId)
            {
                AssistantToggleRequested?.Invoke();
                handled = true;
            }
            else if (id >= SnippetHotkeyBase && id < SnippetHotkeyBase + _hotkeySnippets.Length)
            {
                // The foreground app is the paste target — the dock never activates.
                if (_hotkeySnippets[id - SnippetHotkeyBase] is { } snippet)
                    _ = SnippetPaster.PasteAsync(snippet);
                handled = true;
            }
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
        SyncListeningState(active);
    }

    /// <summary>Ask-Palon listening shares the dictation pill (dot + Finish
    /// + ✕) with its own text; the buttons route by which mode is active.</summary>
    public void SetAssistant(bool active)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetAssistant(active));
            return;
        }
        _assistantActive = active;
        SyncListeningState(active);
    }

    void SyncListeningState(bool activated)
    {
        UpdateListeningText();
        if (activated)
        {
            if (_state != DockState.Reminder) SetState(DockState.Dictation);
        }
        else if (_state == DockState.Dictation)
        {
            SetState(RestState()); // still Dictation if the other mode runs
        }
    }

    void StartDictationPulse() => _dictationDot.BeginAnimation(OpacityProperty,
        new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(Motion.PulseFast))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Sine, // breathe, don't blink
        });

    void StopDictationPulse()
    {
        _dictationDot.BeginAnimation(OpacityProperty, null);
        _dictationDot.Opacity = 1;
    }

    /// <summary>Live mic level (dBFS) while dictating / asking — drives the
    /// waveform bars. Safe from any thread; ignored outside the listening
    /// pill. If levels never arrive the pulsing dot simply stays.</summary>
    public void SetVoiceLevel(double db)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetVoiceLevel(db));
            return;
        }
        if (_state != DockState.Dictation) return;
        if (!_waveActive) StartWave();

        // −55…−20 dBFS → 0…1; fast attack, slow decay.
        var target = Math.Clamp((db + 55) / 35.0, 0, 1);
        _voiceLevel += (target - _voiceLevel) * (target > _voiceLevel ? 0.6 : 0.15);
        _waveTick++;
        for (var i = 0; i < _waveBars.Length; i++)
        {
            // A whisper of per-bar drift so quiet stretches still feel alive.
            var jitter = 0.08 * Math.Sin(_waveTick * 0.9 + i * 1.7);
            var span = Math.Clamp(_voiceLevel * WaveWeights[i] + jitter, 0, 1);
            var height = WaveMinHeight + (WaveMaxHeight - WaveMinHeight) * span;
            _waveBars[i].BeginAnimation(HeightProperty,
                Motion.FromTo(_waveBars[i].ActualHeight, height, Motion.Fast, Motion.Out));
        }
    }

    void StartWave()
    {
        _waveActive = true;
        _voiceLevel = 0;
        StopDictationPulse();
        _wavePanel.Visibility = Visibility.Visible;
        UpdateListeningText(); // hides the dot
        // The bars are wider than the dot — let the pill grow to fit.
        AnimatePillTo(MeasureWidth(_dictationContent), ExpandedHeight, Motion.Fast, Motion.Out);
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

    /// <summary>Where the dock settles when a transient state ends.</summary>
    DockState RestState() =>
        _dictationActive || _assistantActive ? DockState.Dictation
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
            AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, Motion.Fast, Motion.Out);
            return;
        }
        SetState(RestState());
    }

    void UpdateReminderContent()
    {
        if (_currentReminder is not { } current) return;
        _reminderOpenLabel.Text = current.Reminder.HasUrl ? "Open" : "Done";
        // The label is user text (often Hebrew) glued to an LTR prefix —
        // isolate it so the separators keep their place.
        _reminderLabel.Text = (current.Missed ? "Missed · " : "") + Bidi.Isolate(current.Reminder.DisplayLabel);
        _reminderCount.Text = _reminderQueue.Count > 0 ? $"+{_reminderQueue.Count}" : "";
        if (_state == DockState.Reminder)
            AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, Motion.Fast, Motion.Out);
    }

    Border ReminderButton(string text, bool primary)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Font.Body,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, primary ? "OnAccentBrush" : "TextPrimaryBrush");
        var button = new Border
        {
            CornerRadius = new CornerRadius(Radius.Card),
            Padding = Pad.Chip,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        if (primary)
        {
            button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            button.MouseEnter += (_, _) => button.Opacity = Ui.HoverDim;
            button.MouseLeave += (_, _) => button.Opacity = 1.0;
        }
        else
        {
            button.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
            Ui.HoverFill(button);
        }
        Ui.HoverSpring(button, 1.05);
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
        // Four snippets, not five: the notes chip needs the width budget.
        foreach (var snippet in SnippetStore.Load().Take(4))
            _chipsPanel.Children.Add(MakeChip(snippet));

        var dictate = MakeChipShell("🎙");
        dictate.ToolTip = DictationHotkeyLive
            ? $"Dictate ({_dictationHotkey}) — speak, and the text is typed where your cursor is"
            : "Dictate — speak, and the text is typed where your cursor is";
        dictate.MouseLeftButtonUp += (_, _) => DictationToggleRequested?.Invoke();
        _chipsPanel.Children.Add(dictate);

        var ask = MakeChipShell("💬");
        ask.ToolTip = AssistantHotkeyLive
            ? $"Ask Palon ({_assistantHotkey}) — he answers, opens things, sets reminders, searches your notes"
            : "Ask Palon — he answers, opens things, sets reminders, searches your notes";
        ask.MouseLeftButtonUp += (_, _) => AssistantToggleRequested?.Invoke();
        _chipsPanel.Children.Add(ask);

        var remind = MakeChipShell("⏰");
        remind.ToolTip = "Remind me to call someone back";
        remind.MouseLeftButtonUp += (_, _) => OpenRemindersRequested?.Invoke();
        _chipsPanel.Children.Add(remind);

        var notes = MakeChipShell("📝");
        notes.ToolTip = "Your call notes";
        notes.MouseLeftButtonUp += (_, _) => OpenNotesRequested?.Invoke();
        _chipsPanel.Children.Add(notes);

        var more = MakeChipShell("…");
        more.MouseLeftButtonUp += (_, _) => OpenFlyoutRequested?.Invoke();
        _chipsPanel.Children.Add(more);

        if (_state == DockState.Expanded) AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, Motion.Fast, Motion.Out);
    }

    Border MakeChip(Snippet snippet)
    {
        var original = snippet.Label.Length > 0 ? snippet.Label : "(untitled)";
        var chip = MakeChipShell(original);
        var label = (TextBlock)chip.Child;
        chip.ToolTip = snippet.Text.Length > 120 ? snippet.Text[..120] + "…" : snippet.Text;

        // One revert timer per chip: rapid clicks restart it instead of
        // stacking timers, so the feedback never reverts early and the
        // MinWidth pin holds until the last flash is done.
        var revert = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.Revert) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            label.Text = original;
            chip.MinWidth = 0;
        };
        void Feedback(string message)
        {
            chip.MinWidth = chip.ActualWidth; // keep the pill from jumping while the text swaps
            label.Text = message;
            revert.Stop();
            revert.Start();
        }

        chip.MouseLeftButtonUp += async (_, _) =>
        {
            var result = await SnippetPaster.PasteAsync(snippet);
            Feedback(result switch
            {
                PasteResult.Pasted => "Pasted ✓",
                PasteResult.CopiedOnly => "Copied ✓",
                _ => "Failed",
            });
        };
        chip.MouseRightButtonUp += (_, _) =>
        {
            var result = SnippetPaster.CopyOnly(snippet);
            Feedback(result == PasteResult.Failed ? "Failed" : "Copied ✓");
        };
        return chip;
    }

    Border MakeChipShell(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Font.Body,
            MaxWidth = 90, // five glyph chips now share the pill's width budget
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(Radius.Card),
            Padding = Pad.Chip,
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "ControlFillBrush");
        Ui.HoverFill(chip);
        Ui.HoverSpring(chip);
        // Chips never start a pill drag.
        chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return chip;
    }

    // ---- state & animation --------------------------------------------------------

    void SetState(DockState state)
    {
        if (_state == state) return;
        if (_state == DockState.Dictation)
        {
            StopDictationPulse();
            StopWave();
        }
        var outgoing = ContentFor(_state);
        _state = state;
        TransitionContent(outgoing, ContentFor(state));
        if (state is DockState.Collapsed or DockState.Expanded && _pendingToast is { } held)
        {
            _pendingToast = null;
            Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true));
        }
        ApplyStateVisuals(state);
    }

    /// <summary>The current state's pill geometry, opacity and content
    /// motion. Split from SetState so an un-hide can re-assert visuals a
    /// mid-morph hide may have left stale.</summary>
    void ApplyStateVisuals(DockState state)
    {
        switch (state)
        {
            case DockState.Collapsed:
                AnimatePillRadius(5, Motion.Base);
                AnimatePillTo(CollapsedWidth, CollapsedHeight, Motion.Base, Motion.InOut);
                SettleCollapsedOpacity(Motion.Base, Motion.InOut);
                FadeInContent(_collapsedDot); // reclaims opacity from its exit fade
                break;
            case DockState.Expanded:
                Reposition();
                AnimatePillRadius(20, Motion.Slow);
                AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_expandedContent);
                Ui.StaggerIn(_chipsPanel, 18); // chips land one beat apart
                PopPill();
                break;
            case DockState.Toast:
                AnimatePillRadius(18, Motion.Base);
                AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, Motion.Base, Motion.Out);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_toastContent);
                RiseIn(_toastContent);
                PopPill();
                break;
            case DockState.Reminder:
                Reposition();
                AnimatePillRadius(20, Motion.Slow);
                AnimatePillTo(MeasureWidth(_reminderContent), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_reminderContent);
                PopPill();
                break;
            case DockState.Dictation:
                Reposition();
                AnimatePillRadius(20, Motion.Slow);
                AnimatePillTo(MeasureWidth(_dictationContent), ExpandedHeight, Motion.Slow, Motion.Overshoot);
                _pill.BeginAnimation(OpacityProperty, Motion.Fade(1.0, Motion.Fast));
                FadeInContent(_dictationContent);
                PopPill();
                if (_waveActive) UpdateListeningText();
                else StartDictationPulse();
                break;
        }
    }

    /// <summary>Fade the pill down to its resting opacity, then hand over to
    /// the idle breathe — a barely-there slow swell that says "alive" the way
    /// Wispr Flow's sliver does. Any later opacity animation replaces it.</summary>
    void SettleCollapsedOpacity(int ms, IEasingFunction? ease = null)
    {
        var settle = Motion.Fade(RestingOpacityFor(), ms, ease);
        settle.Completed += (_, _) =>
        {
            if (_state == DockState.Collapsed && _callState == CallState.Idle) StartIdleBreathe();
        };
        _pill.BeginAnimation(OpacityProperty, settle);
    }

    void StartIdleBreathe()
    {
        var rest = RestingOpacityFor();
        _pill.BeginAnimation(OpacityProperty,
            new DoubleAnimation(rest, rest - 0.12, TimeSpan.FromMilliseconds(Motion.PulseSlow * 2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Sine,
            });
    }

    void ApplyContentVisibility()
    {
        _collapsedDot.Visibility = _state == DockState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _expandedContent.Visibility = _state == DockState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _toastContent.Visibility = _state == DockState.Toast ? Visibility.Visible : Visibility.Collapsed;
        _reminderContent.Visibility = _state == DockState.Reminder ? Visibility.Visible : Visibility.Collapsed;
        _dictationContent.Visibility = _state == DockState.Dictation ? Visibility.Visible : Visibility.Collapsed;
    }

    UIElement ContentFor(DockState state) => state switch
    {
        DockState.Expanded => _expandedContent,
        DockState.Toast => _toastContent,
        DockState.Reminder => _reminderContent,
        DockState.Dictation => _dictationContent,
        _ => _collapsedDot,
    };

    /// <summary>Outgoing content exits with a quick fade instead of blinking
    /// off; the incoming fade (FadeInContent, delayed a beat) starts as the
    /// exit lands, so swaps read as a hand-off, not a cut.</summary>
    void TransitionContent(UIElement outgoing, UIElement incoming)
    {
        if (outgoing == incoming) return;
        incoming.Visibility = Visibility.Visible;
        var exit = Motion.Fade(0, Motion.Exit);
        exit.Completed += (_, _) =>
        {
            // A rapid flip may have made it the active content again —
            // FadeInContent already reclaimed its opacity in that case.
            if (ContentFor(_state) != outgoing) outgoing.Visibility = Visibility.Collapsed;
        };
        outgoing.BeginAnimation(OpacityProperty, exit);
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

    void SetPillRadius(double radius)
    {
        _pill.CornerRadius = new CornerRadius(radius);
        _glassSheen.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
    }

    /// <summary>Animatable corner radius — CornerRadius can't take a
    /// DoubleAnimation, so this DP proxies into SetPillRadius and the radius
    /// morphs with the width instead of snapping ahead of it.</summary>
    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(
        nameof(PillRadius), typeof(double), typeof(DockWindow),
        new PropertyMetadata(5.0, (d, e) => ((DockWindow)d).SetPillRadius((double)e.NewValue)));

    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }

    // Motion.Out, not the pill's Overshoot: a radius that overshoots past
    // height/2 squares off the collapsed sliver for a frame.
    void AnimatePillRadius(double to, int ms) =>
        BeginAnimation(PillRadiusProperty, Motion.FromTo(PillRadius, to, ms, Motion.Out));

    /// <summary>A tiny spring up from the taskbar edge whenever the pill grows.</summary>
    void PopPill()
    {
        _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.96, 1, Motion.Slow, Motion.Overshoot));
        _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.96, 1, Motion.Slow, Motion.Overshoot));
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
        // One Exit beat late, so the incoming fade starts as the outgoing
        // content's exit fade lands (TransitionContent).
        fadeIn.BeginTime = TimeSpan.FromMilliseconds(Motion.Exit);
        content.BeginAnimation(OpacityProperty, fadeIn);
    }

    static void RiseIn(UIElement content)
    {
        var rise = new TranslateTransform(0, 4);
        content.RenderTransform = rise;
        var up = Motion.FromTo(4, 0, Motion.Base, Motion.Out);
        up.BeginTime = TimeSpan.FromMilliseconds(Motion.Exit);
        rise.BeginAnimation(TranslateTransform.YProperty, up);
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
        // Never throw: an unhandled throw here (headless RDP, no screens)
        // would be swallowed by the dispatcher handler and leave the window
        // parked off-screen at -10000 with no retry.
        try
        {
            var wa = (WinF.Screen.PrimaryScreen ?? WinF.Screen.AllScreens[0]).WorkingArea;
            var scale = Dpi.MoveToAndGetScale(this, wa);

            double windowWidthPx = Width * scale;
            double windowHeightPx = Height * scale;
            double centerPx = wa.Left + Settings.DockX * wa.Width;
            centerPx = Math.Clamp(centerPx, wa.Left + EdgeKeepIn * scale, wa.Right - EdgeKeepIn * scale);

            Left = (centerPx - windowWidthPx / 2) / scale;
            Top = (wa.Bottom - windowHeightPx) / scale;
        }
        catch (Exception ex)
        {
            Log.Write($"Dock reposition failed: {ex.Message}");
        }
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
            // A hide mid-morph froze the pill's animated size/opacity where
            // they were — re-run the state's visuals so it returns whole.
            ApplyStateVisuals(_state);
            TryShowReminder();
            if (_state is DockState.Collapsed or DockState.Expanded && _pendingToast is { } held)
            {
                _pendingToast = null;
                Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true));
            }
        }
    }
}
