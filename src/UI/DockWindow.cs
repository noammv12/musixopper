using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Palon.Interop;
using ShapePath = System.Windows.Shapes.Path;
using WinF = System.Windows.Forms;

namespace Palon.UI;

enum DockState
{
    Collapsed,  // the quiet capsule: status dot (+ timer and level bars on a call)
    Expanded,   // hover: status · progress · overdue · chips
    Toast,      // a one-line transient
    Card,       // after-call or callback-due card (see DockCardQueue)
    Dictation,  // dictation / Ask-Palon listening
}

/// <summary>
/// The Palon dock: a small always-on-top capsule just above the taskbar.
/// One pill morphs (width, height, radius — exponential ease-out) between
/// the resting capsule, the hover bar, toasts, the listening pill and the
/// two cards: right after a call (summary, one-tap callback, copy for
/// Salesforce) and when a callback comes due. Content crossfades; nothing
/// moves on its own except Palon. It never takes activation
/// (WS_EX_NOACTIVATE | TOOLWINDOW) — that's what makes click-to-paste
/// possible and why it can never steal focus. Only ever Hide() it, never Close().
/// Split across src/UI/Dock/*.cs (resting bar, cards, kit, pure logic).
/// </summary>
sealed partial class DockWindow : Window
{
    // Pill metrics (DIP).
    const double IdleWidth = 40;
    const double IdleHeight = 12;
    const double OnCallHeight = 26;
    const double ExpandedHeight = 40;
    const double ToastHeight = 36;
    const double CardWidth = 404;
    const double CardRadius = 22;
    const double WindowWidth = 640;
    const double WindowHeight = 360; // room for the tallest card; transparent pixels are click-through
    const double BottomGap = 4;    // pill bottom to work-area bottom
    const double EdgeKeepIn = 60;  // min px between pill center and screen edge
    const double RestingOpacity = 0.75;
    const double DisabledOpacity = 0.45;

    static readonly Geometry PauseGlyph = Geometry.Parse("M0,0 H3.6 V11 H0 Z M6.4,0 H10 V11 H6.4 Z");
    static readonly Geometry PlayGlyph = Geometry.Parse("M0,0 L10,5.5 L0,11 Z");

    readonly Border _pill;
    readonly Border _glassSheen;
    readonly ScaleTransform _pillScale = new(1, 1);
    readonly StackPanel _toastContent;
    readonly ShapePath _toastIcon;
    readonly TextBlock _toastText;
    readonly StackPanel _dictationContent;
    readonly Ellipse _dictationDot;
    readonly StackPanel _wavePanel;
    readonly Border[] _waveBars = new Border[5];
    readonly ShapePath _assistantIcon;
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
    double _restWidth;
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
    /// <summary>A due callback's link should open (the scheduler opens it and marks it done).</summary>
    public event Action<Callback>? ReminderOpenRequested;
    bool _dictationActive;
    bool _assistantActive;
    string _assistantStatus = "";

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
        // Keep the pill's 1px stroke on device pixels.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        BuildResting(); // _restContent + _expandedContent (Dock/DockResting.cs)

        // -- toast content ----------------------------------------------------
        _toastIcon = new ShapePath { Width = 10, Height = 11, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        _toastIcon.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        // Medium, not SemiBold: the sanctioned exception — on the compact pill
        // Medium at Lead size reads better (see AUDIT.md). MaxWidth sits just
        // under the pill's clamp so a long line ellipsizes instead of
        // hard-clipping mid-glyph (AUDIT #6).
        _toastText = new TextBlock
        {
            FontSize = Font.Lead,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            MaxWidth = 540,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _toastContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
        _toastContent.Children.Add(_toastIcon);
        _toastContent.Children.Add(_toastText);
        // An actionable toast must claim the press, or _pill.CaptureMouse()
        // routes the whole gesture to the pill and MouseLeftButtonUp never
        // fires (AUDIT #1). Actionless toasts stay draggable.
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

        // -- dictation / assistant content ------------------------------------
        _dictationDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = DockPalette.Done };
        // Waveform — swapped in for the dot once mic levels flow.
        _wavePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            FlowDirection = FlowDirection.LeftToRight,
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
        _assistantIcon = DockKit.Icon(DockKit.IconChat);
        _assistantIcon.Visibility = Visibility.Collapsed;
        _dictationText = new TextBlock
        {
            FontSize = Font.Lead,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        _dictationText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var dictationFinish = DockKit.Button("סיום", DockKit.Kind.Primary, () =>
            (_assistantActive ? AssistantToggleRequested : DictationToggleRequested)?.Invoke());
        dictationFinish.Height = 28;
        dictationFinish.Margin = new Thickness(4, 0, 0, 0);
        var dictationCancel = DockKit.IconButton(DockKit.IconClose, "ביטול", () =>
            (_assistantActive ? AssistantCancelRequested : DictationCancelRequested)?.Invoke());
        dictationCancel.Margin = new Thickness(4, 0, 0, 0);
        _dictationContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
        _dictationContent.Children.Add(_dictationDot);
        _dictationContent.Children.Add(_wavePanel);
        _dictationContent.Children.Add(_assistantIcon);
        _dictationContent.Children.Add(_dictationText);
        _dictationContent.Children.Add(dictationFinish);
        _dictationContent.Children.Add(dictationCancel);

        BuildCardHost(); // _cardHost (Dock/DockCards.cs)

        var host = new Grid { ClipToBounds = true, FlowDirection = FlowDirection.RightToLeft };
        host.Children.Add(_restContent);
        host.Children.Add(_expandedContent);
        host.Children.Add(_toastContent);
        host.Children.Add(_cardHost);
        host.Children.Add(_dictationContent);
        _glassSheen = new Border { IsHitTestVisible = false, Margin = new Thickness(1) };
        _glassSheen.SetResourceReference(Border.BackgroundProperty, "GlassSheenBrush");

        var layers = new Grid();
        layers.Children.Add(host);
        layers.Children.Add(_glassSheen); // light on the glass, over everything, never clickable

        _pill = new Border
        {
            Width = IdleWidth,
            Height = IdleHeight,
            Opacity = RestingOpacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(20, 20, 20, BottomGap),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            Effect = Ui.Shadow(),
            Child = layers,
        };
        _pill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pill.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeBrush");
        _pill.RenderTransform = _pillScale;
        _pill.RenderTransformOrigin = new Point(0.5, 1); // grows up from the taskbar edge
        SetPillRadius(IdleHeight / 2);
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
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
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
            _collapseDelay.Stop();
            if (_state == DockState.Card) PauseCardIdle();
            if (_state is DockState.Card or DockState.Dictation) return; // persistent states
            if (_state == DockState.Collapsed) _hoverIntent.Start();
            else if (_state == DockState.Toast && _toastAction is null)
            {
                // A plain toast yields to the hover bar; an actionable one
                // holds still under the pointer so it can be clicked.
                _toastTimer.Stop();
                SetState(DockState.Expanded);
            }
            else if (_state == DockState.Toast) _toastTimer.Stop();
        };
        _pill.MouseLeave += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_state == DockState.Expanded) _collapseDelay.Start();
            else if (_state == DockState.Card) ResumeCardIdle();
            else if (_state == DockState.Toast) _toastTimer.Start();
        };
        _pill.MouseLeftButtonDown += OnPillMouseDown;
        _pill.MouseMove += OnPillMouseMove;
        _pill.MouseLeftButtonUp += OnPillMouseUp;

        SnippetStore.Changed += RefreshChips;
        SnippetStore.Changed += ApplySnippetHotkeys;
        DockActions.Changed += RefreshChips;
        CallbackStore.Changed += OnCallbacksChanged;
        Palon.Sales.SalesStore.Changed += OnSalesChanged;
        RefreshHotkeyStrings();
        RefreshDayStats();

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
        // The chat mark says Palon is listening; the green dot marks
        // dictation — until mic levels flow and the waveform takes its place.
        _assistantIcon.Visibility = _assistantActive ? Visibility.Visible : Visibility.Collapsed;
        _dictationDot.Visibility = _assistantActive || _waveActive ? Visibility.Collapsed : Visibility.Visible;
        _dictationText.Text =
            _assistantActive ? "Palon מקשיב — שאל"
            : DictationHotkeyLive ? $"מכתיב — {_dictationHotkey} לסיום"
            : "מכתיב — סיום כשגמרת";
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
        StopCardTimers();
        SnippetStore.Changed -= RefreshChips;
        CallbackStore.Changed -= OnCallbacksChanged;
        DockActions.Changed -= RefreshChips;
        Palon.Sales.SalesStore.Changed -= OnSalesChanged;
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
            // A new call: after-call cards step aside (the note stays in
            // Notes), a showing callback card waits for the call to end.
            _cards.OnCallStarted();
            _cardShownKey = null;
            if (_state == DockState.Card)
            {
                StopCardTimers();
                SetState(RestState());
            }
        }
        else if (state != CallState.OnCall)
        {
            _callTicker.Stop();
        }
        ApplyCallVisuals();
        UpdateStatusText();
        if (_state == DockState.Collapsed) ApplyStateVisuals(DockState.Collapsed); // the capsule re-fits (timer in/out)
        if (wasOnCall && state != CallState.OnCall) TryShowCard(); // a parked callback card returns
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
        var onCall = _callState == CallState.OnCall;
        var timer = onCall ? DockText.Timer(DateTime.UtcNow - _callStartedUtc) : "";
        _restTimer.Text = timer;
        _statusTimer.Text = timer;
        if (onCall && _state == DockState.Collapsed)
        {
            // The capsule re-fits only when the (4-DIP quantized) width
            // actually changes — e.g. the timer crossing an hour.
            var (w, h) = RestMetrics();
            if (Math.Abs(w - _restWidth) > 0.5) ApplyStateVisuals(DockState.Collapsed);
        }
        var text = _callState switch
        {
            CallState.OnCall => "בשיחה",
            CallState.Disabled => "מושהה",
            _ => _assistantStatus.Length > 0 ? _assistantStatus
                : _dictationStatus.Length > 0 ? _dictationStatus
                : _notesStatus.Length > 0 ? _notesStatus
                : "מוכן",
        };
        if (_statusText.Text == text) return;
        _statusText.Text = text;
        DockKit.AlignFlow(_statusText);
        // Crossfade the word, never the ticking timer (it would flicker).
        _statusText.BeginAnimation(OpacityProperty, DockMotion.FromTo(0.3, 1, DockMotion.FadeIn));
        if (_state == DockState.Expanded) RefitExpanded();
    }

    public void ShowToast(string text, bool paused, Action? onClick = null, bool showIcon = true, bool important = false)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(text, paused, onClick, showIcon, important));
            return;
        }
        if (_fullscreenHidden || !IsVisible || _state is DockState.Card or DockState.Dictation)
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
            DockKit.AlignFlow(_toastText);
            _toastIcon.Data = paused ? PauseGlyph : PlayGlyph;
            _toastIcon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            _toastAction = onClick;
            _toastContent.Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand;
        }

        _toastTimer.Stop();
        // Actionable toasts get time to be read and reached for.
        _toastTimer.Interval = TimeSpan.FromMilliseconds(onClick is null ? 2400 : 5000);
        if (!_pill.IsMouseOver) _toastTimer.Start();
        if (_state == DockState.Toast)
        {
            // Toast replacing a toast: dip the old line out, swap, fade the
            // new one in as the pill re-morphs — text never teleports (AUDIT #13).
            var fadeOut = DockMotion.To(0, DockMotion.FadeOut);
            fadeOut.Completed += (_, _) =>
            {
                if (_state != DockState.Toast) return;
                Apply();
                MorphTo(MeasureWidth(_toastContent), ToastHeight, ToastHeight / 2, DockMotion.MorphQuick);
                _toastContent.BeginAnimation(OpacityProperty, DockMotion.FromTo(0, 1, DockMotion.FadeIn));
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
            SetState(DockState.Dictation); // the mic outranks a card; the card waits (RestState)
        }
        else if (_state == DockState.Dictation)
        {
            SetState(RestState()); // still Dictation if the other mode runs
        }
    }

    // No breathing dot: the dock's only ambient motion is Palon. The dot
    // simply holds until the waveform (real mic levels) takes over.
    void StartDictationPulse()
    {
        _dictationDot.BeginAnimation(OpacityProperty, null);
        _dictationDot.Opacity = 1;
    }

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
                DockMotion.FromTo(_waveBars[i].ActualHeight, height, Motion.Fast));
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
        MorphTo(MeasureWidth(_dictationContent), ExpandedHeight, ExpandedHeight / 2, DockMotion.MorphQuick);
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


    /// <summary>Where the dock settles when a transient state ends: the mic
    /// outranks a waiting card, a card outranks the resting bar.</summary>
    DockState RestState() =>
        _dictationActive || _assistantActive ? DockState.Dictation
        : _cards.Current is not null && _callState != CallState.OnCall ? DockState.Card
        : _pill.IsMouseOver ? DockState.Expanded
        : DockState.Collapsed;

    // ---- state & motion --------------------------------------------------------

    void SetState(DockState state)
    {
        if (state == DockState.Card)
        {
            // Every road into the card goes through Present, so the host
            // always holds the card the queue says is current.
            if (_cards.Current is not { } card) state = _pill.IsMouseOver ? DockState.Expanded : DockState.Collapsed;
            else if (_cardShownKey != card.Key)
            {
                Present(card);
                return;
            }
        }
        if (_state == state) return;
        if (_state == DockState.Dictation)
        {
            StopDictationPulse();
            StopWave();
        }
        var outgoing = ContentFor(_state);
        _state = state;
        TransitionContent(outgoing, ContentFor(state));
        if (state is DockState.Collapsed or DockState.Expanded)
        {
            if (_pendingToast is { } held)
            {
                _pendingToast = null;
                Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true));
            }
            else if (_cards.Current is null && _cards.WaitingCount > 0)
            {
                Dispatcher.InvokeAsync(TryShowCard);
            }
        }
        ApplyStateVisuals(state);
    }

    /// <summary>The state's pill geometry, opacity and content motion. Split
    /// from SetState so an un-hide can re-assert visuals a mid-morph hide
    /// may have left stale.</summary>
    void ApplyStateVisuals(DockState state)
    {
        switch (state)
        {
            case DockState.Collapsed:
            {
                var (w, h) = RestMetrics();
                _restWidth = w;
                MorphTo(w, h, h / 2, DockMotion.Morph);
                _pill.BeginAnimation(OpacityProperty, DockMotion.To(RestingOpacityFor(), DockMotion.Morph));
                FadeInContent(_restContent);
                break;
            }
            case DockState.Expanded:
                Reposition();
                RefreshDayStats();
                MorphTo(MeasureWidth(_expandedContent), ExpandedHeight, ExpandedHeight / 2, DockMotion.Morph);
                _pill.BeginAnimation(OpacityProperty, DockMotion.To(1.0, DockMotion.FadeIn));
                FadeInContent(_expandedContent);
                Ui.StaggerIn(_chipsPanel, 18); // chips land one beat apart
                break;
            case DockState.Toast:
                MorphTo(MeasureWidth(_toastContent), ToastHeight, ToastHeight / 2, DockMotion.Morph);
                _pill.BeginAnimation(OpacityProperty, DockMotion.To(1.0, DockMotion.FadeIn));
                FadeInContent(_toastContent);
                break;
            case DockState.Card:
                Reposition();
                MorphTo(CardWidth, MeasureCardHeight(), CardRadius, DockMotion.Morph);
                _pill.BeginAnimation(OpacityProperty, DockMotion.To(1.0, DockMotion.FadeIn));
                DockKit.RiseIn(_cardHost);
                SettleIn(); // one soft entrance — never a pulse
                if (!_pill.IsMouseOver) ResumeCardIdle(); // back from dictation/fullscreen: re-arm the tuck
                break;
            case DockState.Dictation:
                Reposition();
                MorphTo(MeasureWidth(_dictationContent), ExpandedHeight, ExpandedHeight / 2, DockMotion.Morph);
                _pill.BeginAnimation(OpacityProperty, DockMotion.To(1.0, DockMotion.FadeIn));
                FadeInContent(_dictationContent);
                if (_waveActive) UpdateListeningText();
                else StartDictationPulse();
                break;
        }
    }

    void ApplyContentVisibility()
    {
        _restContent.Visibility = _state == DockState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _expandedContent.Visibility = _state == DockState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _toastContent.Visibility = _state == DockState.Toast ? Visibility.Visible : Visibility.Collapsed;
        _cardHost.Visibility = _state == DockState.Card ? Visibility.Visible : Visibility.Collapsed;
        _dictationContent.Visibility = _state == DockState.Dictation ? Visibility.Visible : Visibility.Collapsed;
    }

    UIElement ContentFor(DockState state) => state switch
    {
        DockState.Expanded => _expandedContent,
        DockState.Toast => _toastContent,
        DockState.Card => _cardHost,
        DockState.Dictation => _dictationContent,
        _ => _restContent,
    };

    /// <summary>Outgoing content exits with a quick fade; the incoming fade
    /// starts as the exit lands, so swaps read as a hand-off, not a cut.</summary>
    void TransitionContent(UIElement outgoing, UIElement incoming)
    {
        if (outgoing == incoming) return;
        incoming.Visibility = Visibility.Visible;
        var exit = DockMotion.To(0, DockMotion.FadeOut);
        exit.Completed += (_, _) =>
        {
            // A rapid flip may have made it the active content again —
            // the incoming fade already reclaimed its opacity in that case.
            if (ContentFor(_state) != outgoing) outgoing.Visibility = Visibility.Collapsed;
        };
        outgoing.BeginAnimation(OpacityProperty, exit);
    }

    double RestingOpacityFor() => _callState switch
    {
        CallState.Disabled => DisabledOpacity,
        CallState.OnCall => 0.95, // the live call reads at a glance
        _ => RestingOpacity,
    };

    double MeasureWidth(UIElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Min(Math.Ceiling(content.DesiredSize.Width) + 2, WindowWidth - 40);
    }

    /// <summary>Morphs the pill: width, height and corner radius on one
    /// exponential ease-out, from wherever they are right now (a morph
    /// interrupting a morph continues smoothly — no jumps).</summary>
    void MorphTo(double width, double height, double radius, int ms)
    {
        _pill.BeginAnimation(WidthProperty, DockMotion.FromTo(_pill.ActualWidth, width, ms));
        _pill.BeginAnimation(HeightProperty, DockMotion.FromTo(_pill.ActualHeight, height, ms));
        BeginAnimation(PillRadiusProperty, DockMotion.FromTo(PillRadius, Math.Min(radius, height / 2), ms));
    }

    void SetPillRadius(double radius)
    {
        _pill.CornerRadius = new CornerRadius(radius);
        _glassSheen.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
    }

    /// <summary>Animatable corner radius — CornerRadius can't take a
    /// DoubleAnimation, so this DP proxies into SetPillRadius and the radius
    /// morphs with the size instead of snapping ahead of it.</summary>
    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(
        nameof(PillRadius), typeof(double), typeof(DockWindow),
        new PropertyMetadata(IdleHeight / 2, (d, e) => ((DockWindow)d).SetPillRadius((double)e.NewValue)));

    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }

    /// <summary>The card's single soft entrance: it settles up from 0.98 — no overshoot, no pulse.</summary>
    void SettleIn()
    {
        _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, DockMotion.FromTo(0.98, 1, DockMotion.Morph));
        _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, DockMotion.FromTo(0.98, 1, DockMotion.Morph));
    }

    void FadeInContent(UIElement content)
    {
        content.BeginAnimation(OpacityProperty, null);
        content.Opacity = 0;
        var fadeIn = DockMotion.FromTo(0, 1, DockMotion.FadeIn);
        // Starts as the outgoing content's exit fade lands (TransitionContent).
        fadeIn.BeginTime = TimeSpan.FromMilliseconds(DockMotion.FadeOut);
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
            TryShowCard();
            if (_state is DockState.Collapsed or DockState.Expanded && _pendingToast is { } held)
            {
                _pendingToast = null;
                Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true));
            }
        }
    }
}
