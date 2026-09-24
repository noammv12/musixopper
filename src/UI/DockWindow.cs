using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Palon.Interop;
using Palon.Terminal;
using WinF = System.Windows.Forms;

namespace Palon.UI;

/// <summary>The dock's three states. Everything else is gone.</summary>
enum DockMode
{
    Rest,   // the slim pill (or the amber capsule on a call)
    Hover,  // the pill grows one row of actions
    Moment, // a card, the quick field, the listening pill, or the in-call chips
}

enum MomentKind { None, Card, Quick, Listening, CallChips }

/// <summary>
/// Palon Dock 2: one always-on-top pill just above the taskbar that morphs —
/// width, height and radius on one spring, content crossfading — between
/// exactly three states (see docs/design/Dock2.dc.html):
/// <list type="bullet">
/// <item>Rest: status dot, three micro rings for today, overdue count, small
/// Palon. On a call it becomes the amber capsule (who · timer · ⏰).</item>
/// <item>Hover: "+ חזרה", pinned templates (one click copies with the
/// client's first name), Palon (opens Now), "…" for dictate / read screen / snippets.</item>
/// <item>Moment: the after-call card (four time chips — one click books),
/// the callback-due card, one nudge, the quick "name · when" field, the
/// listening pill.</item>
/// </list>
/// It never takes activation (WS_EX_NOACTIVATE | TOOLWINDOW) except while the
/// quick field is open, and gives focus back when it closes. Only ever
/// Hide() it, never Close(). Split across src/UI/Dock/*.cs.
/// </summary>
sealed partial class DockWindow : Window
{
    const double WindowWidth = 720;
    const double WindowHeight = 320; // room for the tallest card; transparent pixels are click-through
    const double BottomGap = 6;
    const double EdgeKeepIn = 120;   // min px between pill center and screen edge
    const double RestHeight = 44;
    const double HoverHeight = 48;
    const double CallWidth = 400;
    const double CallHeight = 52;
    const double DisabledOpacity = 0.5;

    readonly Border _pill;
    readonly Border _callGlass;      // amber layer, faded in on a call
    readonly Grid _host;
    readonly Border _momentHost;

    readonly DispatcherTimer _hoverIntent;
    readonly DispatcherTimer _collapseDelay;
    readonly DispatcherTimer _fullscreenPoll;
    readonly DispatcherTimer _repositionDebounce;
    readonly DispatcherTimer _callTicker;
    int _pollTicks;

    DockMode _mode = DockMode.Rest;
    MomentKind _moment = MomentKind.None;
    UIElement? _shownLayer;
    CallState _callState = CallState.Idle;
    string? _callNumber;
    DateTime _callStartedUtc;
    bool _fullscreenHidden;
    string _notesStatus = "";
    (string Text, bool Paused, Action? OnClick, bool ShowIcon)? _pendingToast;

    // drag
    bool _dragging;
    int _dragStartCursorX;
    double _dragStartLeft;
    double _dragScale = 1;

    // ---- hotkeys ----
    const int DictationHotkeyId = 0xA11;
    const int AssistantHotkeyId = 0xA12;
    const int ScreenHotkeyId = 0xA13;
    const int QuickHotkeyId = 0xA14;
    const int SnippetHotkeyBase = 0xA21; // ids 0xA21–0xA29 = Ctrl+Alt+1–9
    const string ScreenHotkeyLabel = "Ctrl+Alt+Shift+S";
    Hotkey _dictationHotkey = Hotkey.LoadDictation();
    Hotkey _assistantHotkey = Hotkey.LoadAssistant();
    Hotkey _quickHotkey = Hotkey.LoadQuickCallback();
    bool _dictationHotkeyFailed, _assistantHotkeyFailed, _quickHotkeyFailed, _screenHotkeyRegistered;
    readonly Snippet?[] _hotkeySnippets = new Snippet?[9];

    public event Action? OpenFlyoutRequested;
    public event Action? OpenRemindersRequested;
    public event Action? DictationToggleRequested;
    public event Action? DictationCancelRequested;
    public event Action? AssistantToggleRequested;
    public event Action? AssistantCancelRequested;
    /// <summary>A due callback's link should open (the scheduler opens it and marks it done).</summary>
    public event Action<Callback>? ReminderOpenRequested;

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
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        BuildRest();      // _restRow + hover segment (Dock/DockResting.cs)
        BuildCall();      // _callRow (Dock/DockResting.cs)
        BuildListening(); // _listenRow (Dock/DockMoments.cs)
        BuildCardTimers(); // (Dock/DockCards.cs)

        _momentHost = new Border { VerticalAlignment = VerticalAlignment.Top };

        _host = new Grid { ClipToBounds = true, FlowDirection = FlowDirection.RightToLeft };
        foreach (var layer in new UIElement[] { _restRow, _callRow, _momentHost })
        {
            layer.Visibility = Visibility.Collapsed;
            _host.Children.Add(layer);
        }

        _callGlass = new Border { Background = DockPalette.CallGlass, Opacity = 0, IsHitTestVisible = false };
        var sheen = new Border { Background = DockPalette.Sheen, IsHitTestVisible = false };
        var layers = new Grid();
        layers.Children.Add(_callGlass);
        layers.Children.Add(sheen);
        layers.Children.Add(_host);

        _pill = new Border
        {
            Width = 180,
            Height = RestHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(20, 20, 20, BottomGap),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
            BorderBrush = DockPalette.Stroke,
            Background = DockPalette.Glass,
            Effect = Ui.Shadow(),
            Child = layers,
            ClipToBounds = false,
        };
        // The glass layers follow the pill's radius.
        _callGlass.CornerRadius = sheen.CornerRadius = new CornerRadius(RestHeight / 2);
        _radiusTargets = new[] { _callGlass, sheen };
        SetPillRadius(RestHeight / 2);
        Content = _pill;

        _hoverIntent = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hoverIntent.Tick += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_pill.IsMouseOver && _mode == DockMode.Rest && CanHover) SetMode(DockMode.Hover);
        };
        _collapseDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            if (!_pill.IsMouseOver && _mode == DockMode.Hover) SetMode(DockMode.Rest);
        };
        _fullscreenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fullscreenPoll.Tick += (_, _) =>
        {
            UpdateFullscreenHidden();
            // Cheap insurance against z-order theft.
            if (++_pollTicks % 5 == 0 && IsVisible) ReassertTopmost();
            if (_pollTicks % 30 == 0) RefreshToday(); // the day rolls over; stores changed elsewhere
        };
        _repositionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _repositionDebounce.Tick += (_, _) =>
        {
            _repositionDebounce.Stop();
            Reposition();
        };
        _callTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _callTicker.Tick += (_, _) => UpdateCallTimer();

        _pill.MouseEnter += (_, _) =>
        {
            _collapseDelay.Stop();
            if (_mode == DockMode.Moment) PauseCardIdle();
            if (_mode == DockMode.Rest && CanHover) _hoverIntent.Start();
        };
        _pill.MouseLeave += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_mode == DockMode.Hover) _collapseDelay.Start();
            else if (_mode == DockMode.Moment) ResumeCardIdle();
            ResumeFlashTimer();
        };
        _pill.MouseLeftButtonDown += OnPillMouseDown;
        _pill.MouseMove += OnPillMouseMove;
        _pill.MouseLeftButtonUp += OnPillMouseUp;

        SnippetStore.Changed += OnSnippetsChanged;
        CallbackStore.Changed += OnCallbacksChanged;
        Palon.Sales.SalesStore.Changed += OnStoresChanged;
        Palon.Notes.NotesStore.Changed += OnStoresChanged;
        CallStatsStore.Changed += OnStoresChanged;
        DockActions.SettingsChanged += OnDockSettingsChanged;
        TemplatesStore.Changed += OnTemplatesChanged;
        Palon.Agentic.NudgeHub.Raised += OnNudgeRaised;
        Palon.Agentic.NudgeHub.Dismissed += OnNudgeDismissed;

        Deactivated += (_, _) =>
        {
            // Clicked away from the quick field — it closes; focus already moved on.
            if (_moment == MomentKind.Quick && !_quickBooked) CloseQuick(restoreFocus: false);
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetNoActivate(hwnd, true);
            if (HwndSource.FromHwnd(hwnd) is { } source) source.AddHook(WndProc);
            ApplyDictationHotkey();
            ApplyAssistantHotkey();
            ApplyScreenHotkey();
            ApplyQuickCallbackHotkey();
            ApplySnippetHotkeys();

            // A silently-dead hotkey reads as "hotkeys don't exist" — say it once.
            var taken = new List<string>(3);
            if (_dictationHotkeyFailed) taken.Add(_dictationHotkey.ToString());
            if (_assistantHotkeyFailed) taken.Add(_assistantHotkey.ToString());
            if (_quickHotkeyFailed) taken.Add(_quickHotkey.ToString());
            if (taken.Count > 0)
                ShowToast($"{string.Join(" and ", taken)} taken by another app — pick a different combo in settings",
                    paused: false, showIcon: false, important: true);
        };

        RefreshToday();
        ShowLayer(_restRow, animate: false);
        ApplyShape(animate: false);
    }

    static void SetNoActivate(IntPtr hwnd, bool on)
    {
        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex = on ? ex | NativeMethods.WS_EX_NOACTIVATE : ex & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    // ---- hotkeys ---------------------------------------------------------------

    bool DictationHotkeyLive => !_dictationHotkey.IsOff && !_dictationHotkeyFailed;
    bool AssistantHotkeyLive => !_assistantHotkey.IsOff && !_assistantHotkeyFailed;
    bool QuickHotkeyLive => !_quickHotkey.IsOff && !_quickHotkeyFailed;

    /// <summary>The bindings that actually registered (null = off or taken).</summary>
    public (string? Ask, string? Dictate) LiveHotkeys() => (
        AssistantHotkeyLive ? _assistantHotkey.ToString() : null,
        DictationHotkeyLive ? _dictationHotkey.ToString() : null);

    public string? LiveQuickHotkey => QuickHotkeyLive ? _quickHotkey.ToString() : null;

    bool Register(int id, Hotkey key, string what)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return true; // not sourced yet; SourceInitialized applies
        NativeMethods.UnregisterHotKey(hwnd, id);
        if (key.IsOff) return true;
        var ok = NativeMethods.RegisterHotKey(hwnd, id, key.Modifiers | NativeMethods.MOD_NOREPEAT, key.Vk);
        if (!ok) Log.Write($"{what} hotkey {key} unavailable (taken by another app)");
        return ok;
    }

    /// <summary>(Re)binds the dictation hotkey. False when Windows refused it.</summary>
    public bool ApplyDictationHotkey()
    {
        _dictationHotkey = Hotkey.LoadDictation();
        var ok = Register(DictationHotkeyId, _dictationHotkey, "Dictation");
        _dictationHotkeyFailed = !ok;
        UpdateListeningText();
        return ok;
    }

    /// <summary>(Re)binds the Ask-Palon hotkey. False when Windows refused it.</summary>
    public bool ApplyAssistantHotkey()
    {
        _assistantHotkey = Hotkey.LoadAssistant();
        var ok = Register(AssistantHotkeyId, _assistantHotkey, "Assistant");
        _assistantHotkeyFailed = !ok;
        return ok;
    }

    /// <summary>(Re)binds the quick-callback hotkey (default Ctrl+Alt+R). False
    /// when it collides with another Palon hotkey or another app owns it.</summary>
    public bool ApplyQuickCallbackHotkey()
    {
        if (!CheckAccess()) return Dispatcher.Invoke(ApplyQuickCallbackHotkey);
        _quickHotkey = Hotkey.LoadQuickCallback();
        if (Hotkey.ConflictWith(_quickHotkey, OtherHotkeys()) is { } clash)
        {
            Log.Write($"Quick-callback hotkey {_quickHotkey} clashes with {clash}; not registered");
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(hwnd, QuickHotkeyId);
            _quickHotkeyFailed = true;
            return false;
        }
        var ok = Register(QuickHotkeyId, _quickHotkey, "Quick callback");
        _quickHotkeyFailed = !ok;
        RefreshHoverTips();
        return ok;
    }

    /// <summary>Palon's other hotkeys, for conflict checks (settings uses the same list).</summary>
    public IEnumerable<(string Name, Hotkey Key)> OtherHotkeys()
    {
        yield return ("Dictation", Hotkey.LoadDictation());
        yield return ("Ask Palon", Hotkey.LoadAssistant());
        foreach (var f in Hotkey.Fixed(Settings.ScreenReadHotkey, Settings.SnippetHotkeys)) yield return f;
    }

    /// <summary>(Re)binds the opt-in screen-read hotkey. False when on but taken by another app.</summary>
    public bool ApplyScreenHotkey()
    {
        if (!CheckAccess()) return Dispatcher.Invoke(ApplyScreenHotkey);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return true;
        NativeMethods.UnregisterHotKey(hwnd, ScreenHotkeyId);
        _screenHotkeyRegistered = false;
        var ok = true;
        if (Settings.ScreenReadHotkey)
        {
            ok = NativeMethods.RegisterHotKey(hwnd, ScreenHotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, 0x53 /* S */);
            _screenHotkeyRegistered = ok;
            if (!ok) Log.Write($"Screen-read hotkey {ScreenHotkeyLabel} unavailable (taken by another app)");
        }
        RefreshHoverTips();
        return ok;
    }

    /// <summary>(Re)binds Ctrl+Alt+1–9 to the first nine snippets when the opt-in setting is on.</summary>
    public void ApplySnippetHotkeys()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(ApplySnippetHotkeys);
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        for (var i = 0; i < _hotkeySnippets.Length; i++)
        {
            NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
            _hotkeySnippets[i] = null;
        }
        if (!Settings.SnippetHotkeys) return;
        var snippets = SnippetStore.Load();
        for (var i = 0; i < snippets.Count && i < _hotkeySnippets.Length; i++)
        {
            if (NativeMethods.RegisterHotKey(hwnd, SnippetHotkeyBase + i,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)(0x31 + i)))
                _hotkeySnippets[i] = snippets[i];
            else
                Log.Write($"Snippet hotkey Ctrl+Alt+{i + 1} unavailable (taken by another app)");
        }
    }

    /// <summary>Releases every global hotkey so the settings capture box can
    /// receive those keystrokes itself. Restore with the Apply* methods.</summary>
    public void SuspendHotkeys()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(SuspendHotkeys);
            return;
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        foreach (var id in new[] { DictationHotkeyId, AssistantHotkeyId, ScreenHotkeyId, QuickHotkeyId })
            NativeMethods.UnregisterHotKey(hwnd, id);
        for (var i = 0; i < _hotkeySnippets.Length; i++)
        {
            NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
            _hotkeySnippets[i] = null;
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            // Bursts (taskbar auto-hide, resolution switches) — coalesce.
            _repositionDebounce.Stop();
            _repositionDebounce.Start();
        }
        else if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            handled = true;
            if (id == DictationHotkeyId) DictationToggleRequested?.Invoke();
            else if (id == AssistantHotkeyId) AssistantToggleRequested?.Invoke();
            else if (id == ScreenHotkeyId) TerminalWindow.ReadScreenFromShortcut();
            else if (id == QuickHotkeyId) ToggleQuick();
            else if (id >= SnippetHotkeyBase && id < SnippetHotkeyBase + _hotkeySnippets.Length)
            {
                // The foreground app is the paste target — the dock never activates.
                if (_hotkeySnippets[id - SnippetHotkeyBase] is { } snippet) _ = SnippetPaster.PasteAsync(snippet);
            }
            else handled = false;
        }
        return IntPtr.Zero;
    }

    // ---- lifecycle -----------------------------------------------------------

    public void ShowDock()
    {
        Show();
        Reposition();
        UpdateFullscreenHidden();
        _fullscreenPoll.Start();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // At logon the taskbar often hasn't reserved its strip yet — re-run once things settle.
        RepositionSoon(TimeSpan.FromSeconds(1.5));
        RepositionSoon(TimeSpan.FromSeconds(5));
        if (_pendingToast is { } held)
        {
            _pendingToast = null;
            Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true),
                DispatcherPriority.Loaded);
        }
    }

    public void Shutdown()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var id in new[] { DictationHotkeyId, AssistantHotkeyId, ScreenHotkeyId, QuickHotkeyId })
                NativeMethods.UnregisterHotKey(hwnd, id);
            for (var i = 0; i < _hotkeySnippets.Length; i++) NativeMethods.UnregisterHotKey(hwnd, SnippetHotkeyBase + i);
        }
        catch
        {
        }
        _hoverIntent.Stop();
        _collapseDelay.Stop();
        _fullscreenPoll.Stop();
        _callTicker.Stop();
        _repositionDebounce.Stop();
        StopCardTimers();
        _flashTimer.Stop();
        SnippetStore.Changed -= OnSnippetsChanged;
        CallbackStore.Changed -= OnCallbacksChanged;
        Palon.Sales.SalesStore.Changed -= OnStoresChanged;
        Palon.Notes.NotesStore.Changed -= OnStoresChanged;
        CallStatsStore.Changed -= OnStoresChanged;
        DockActions.SettingsChanged -= OnDockSettingsChanged;
        TemplatesStore.Changed -= OnTemplatesChanged;
        Palon.Agentic.NudgeHub.Raised -= OnNudgeRaised;
        Palon.Agentic.NudgeHub.Dismissed -= OnNudgeDismissed;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Hide();
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reposition);

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
        RefreshToday();
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

    bool OnCall => _callState == CallState.OnCall;

    public void SyncState(CallState state, string? number = null)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SyncState(state, number));
            return;
        }
        var wasOnCall = OnCall;
        _callState = state;
        if (OnCall && !wasOnCall)
        {
            _callNumber = number;
            _callStartedUtc = DateTime.UtcNow;
            _callBooked = null;
            BeginCallCapsule();
            _callTicker.Start();
            // A new call: after-call cards and nudges step aside; a showing
            // callback card waits for the call to end.
            _cards.OnCallStarted();
            _cardShownKey = null;
            StopCardTimers();
            if (_moment is MomentKind.Card) { _moment = MomentKind.None; _mode = DockMode.Rest; }
            if (_moment == MomentKind.Quick) CloseQuick(restoreFocus: true);
            if (_mode == DockMode.Hover)
            {
                CollapseHover();
                _mode = DockMode.Rest;
            }
        }
        else if (OnCall && number is not null && _callNumber is null)
        {
            _callNumber = number; // the number arrived a tick after the call
            BeginCallCapsule();
        }
        else if (!OnCall && wasOnCall)
        {
            _callTicker.Stop();
            EndCallCapsule();
            if (_moment == MomentKind.CallChips) { _moment = MomentKind.None; _mode = DockMode.Rest; }
        }
        UpdateStatusDot();
        Refresh();
        if (wasOnCall && !OnCall) TryShowCard(); // a parked callback card returns
    }

    /// <summary>Note processing status — shown as the dot's tooltip (the dock stays quiet).</summary>
    public void SetNotesStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetNotesStatus(status));
            return;
        }
        _notesStatus = status;
        UpdateStatusDot();
    }

    public void SetDictationStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetDictationStatus(status));
            return;
        }
        _dictationStatus = status;
        UpdateListeningText();
    }

    public void SetAssistantStatus(string status)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetAssistantStatus(status));
            return;
        }
        _assistantStatus = status;
        UpdateListeningText();
    }

    /// <summary>
    /// A micro message on the resting pill (music paused, the caller brief,
    /// warnings). While a Moment holds the pill, important / actionable ones
    /// wait and replay when it settles; plain ones are dropped.
    /// </summary>
    public void ShowToast(string text, bool paused, Action? onClick = null, bool showIcon = true, bool important = false)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(text, paused, onClick, showIcon, important));
            return;
        }
        if (_fullscreenHidden || !IsVisible || _mode == DockMode.Moment)
        {
            if (important || onClick is not null) _pendingToast = (text, paused, onClick, showIcon);
            return;
        }
        Flash(text, showIcon ? (paused ? DockKit.PauseGlyph : DockKit.PlayGlyph) : null, onClick, undo: null);
    }

    void ReplayPendingToast()
    {
        if (_pendingToast is not { } held || _mode == DockMode.Moment) return;
        _pendingToast = null;
        Dispatcher.InvokeAsync(() => ShowToast(held.Text, held.Paused, held.OnClick, held.ShowIcon, important: true));
    }

    // ---- state machine -----------------------------------------------------------

    bool CanHover => !OnCall;

    /// <summary>Moves between the three states; Moment needs a <see cref="MomentKind"/>.</summary>
    void SetMode(DockMode mode, MomentKind moment = MomentKind.None)
    {
        if (mode != DockMode.Moment) moment = MomentKind.None;
        if (_mode == mode && _moment == moment) return;
        var wasHover = _mode == DockMode.Hover;
        _mode = mode;
        _moment = moment;
        if (mode != DockMode.Hover && wasHover) CollapseHover();
        if (mode == DockMode.Hover) ExpandHover();
        Refresh();
        if (mode != DockMode.Moment)
        {
            ReplayPendingToast();
            if (_cards.Current is null && _cards.WaitingCount > 0) Dispatcher.InvokeAsync(TryShowCard);
        }
    }

    /// <summary>Where the dock settles when a Moment ends: the mic outranks a
    /// waiting card, a card outranks the pill.</summary>
    void Settle()
    {
        if (_dictationActive || _assistantActive)
        {
            ShowMoment(MomentKind.Listening, _listenRow);
            return;
        }
        if (_cards.Current is { } card && CanShowCard)
        {
            Present(card);
            return;
        }
        SetMode(_pill.IsMouseOver && CanHover && !OnCall ? DockMode.Hover : DockMode.Rest);
    }

    void ShowMoment(MomentKind kind, FrameworkElement content)
    {
        _hoverIntent.Stop();
        _collapseDelay.Stop();
        var sameKind = _mode == DockMode.Moment && _moment == kind;
        if (!ReferenceEquals(_momentHost.Child, content))
        {
            // Moment → moment: the new content rises in over the re-morph.
            _momentHost.Child = content;
            if (_mode == DockMode.Moment) DockKit.RiseIn(_momentHost, 0);
        }
        if (sameKind)
        {
            ApplyShape(animate: true);
            return;
        }
        SetMode(DockMode.Moment, kind);
    }

    /// <summary>Picks the visible layer and morphs the pill to fit it.</summary>
    void Refresh()
    {
        UIElement layer = _mode == DockMode.Moment ? _momentHost : OnCall ? _callRow : _restRow;
        ShowLayer(layer, animate: true);
        _callGlass.BeginAnimation(OpacityProperty, DockMotion.To(OnCall && _mode != DockMode.Moment || _moment == MomentKind.CallChips ? 1 : 0, 600));
        _pill.BorderBrush = OnCall ? DockPalette.CallStroke : DockPalette.Stroke;
        _pill.BeginAnimation(OpacityProperty, DockMotion.To(_callState == CallState.Disabled && _mode == DockMode.Rest ? DisabledOpacity : 1, 400));
        ApplyShape(animate: true);
    }

    void ShowLayer(UIElement layer, bool animate)
    {
        if (ReferenceEquals(_shownLayer, layer)) return;
        var outgoing = _shownLayer;
        _shownLayer = layer;
        layer.Visibility = Visibility.Visible;
        if (!animate)
        {
            if (outgoing is not null) outgoing.Visibility = Visibility.Collapsed;
            layer.Opacity = 1;
            return;
        }
        if (outgoing is not null)
        {
            var exit = DockMotion.To(0, DockMotion.FadeOut);
            exit.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_shownLayer, outgoing)) outgoing.Visibility = Visibility.Collapsed;
            };
            outgoing.BeginAnimation(OpacityProperty, exit);
        }
        DockKit.RiseIn(layer);
    }

    /// <summary>The target geometry for the current state.</summary>
    (double W, double H, double R) TargetShape()
    {
        switch (_mode)
        {
            case DockMode.Moment:
            {
                var width = _moment switch
                {
                    MomentKind.Card => _cardWidth,
                    MomentKind.Quick => 600,
                    MomentKind.CallChips => 600,
                    _ => Measure(_momentHost).Width,
                };
                width = Math.Min(width, WindowWidth - 40);
                _momentHost.Measure(new Size(width - 2, double.PositiveInfinity));
                var height = Math.Min(Math.Ceiling(_momentHost.DesiredSize.Height) + 2, WindowHeight - 30);
                var radius = _moment is MomentKind.Card ? 28 : Math.Min(28, height / 2);
                return (width, height, radius);
            }
            case DockMode.Hover:
                return (Math.Max(160, Measure(_restRow).Width), HoverHeight, HoverHeight / 2);
            default:
                if (OnCall) return (CallWidth, CallHeight, CallHeight / 2);
                return (Math.Max(120, Measure(_restRow).Width), RestHeight, RestHeight / 2);
        }
    }

    static Size Measure(UIElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(Math.Min(Math.Ceiling(content.DesiredSize.Width) + 2, WindowWidth - 40), Math.Ceiling(content.DesiredSize.Height));
    }

    void ApplyShape(bool animate, int ms = DockMotion.Morph)
    {
        var (w, h, r) = TargetShape();
        if (!animate)
        {
            _pill.BeginAnimation(WidthProperty, null);
            _pill.BeginAnimation(HeightProperty, null);
            BeginAnimation(PillRadiusProperty, null);
            _pill.Width = w;
            _pill.Height = h;
            PillRadius = r;
            return;
        }
        // From wherever the pill is right now: a morph interrupting a morph continues smoothly.
        if (Math.Abs(_pill.ActualWidth - w) > 0.5 || _pill.ActualWidth == 0)
            _pill.BeginAnimation(WidthProperty, DockMotion.FromTo(_pill.ActualWidth > 0 ? _pill.ActualWidth : w, w, ms, DockMotion.Shape));
        if (Math.Abs(_pill.ActualHeight - h) > 0.5 || _pill.ActualHeight == 0)
            _pill.BeginAnimation(HeightProperty, DockMotion.FromTo(_pill.ActualHeight > 0 ? _pill.ActualHeight : h, h, ms, DockMotion.Shape));
        BeginAnimation(PillRadiusProperty, DockMotion.FromTo(PillRadius, Math.Min(r, h / 2), ms, DockMotion.Shape));
    }

    /// <summary>A quick re-fit inside the same state (label swaps, rows appearing).</summary>
    void Refit() => ApplyShape(animate: true, DockMotion.MorphQuick * 2);

    readonly Border[] _radiusTargets;

    void SetPillRadius(double radius)
    {
        radius = Math.Max(0, radius);
        _pill.CornerRadius = new CornerRadius(radius);
        var inner = new CornerRadius(Math.Max(0, radius - 1));
        if (_radiusTargets is null) return;
        foreach (var b in _radiusTargets) b.CornerRadius = inner;
    }

    /// <summary>Animatable corner radius — CornerRadius can't take a
    /// DoubleAnimation, so this DP proxies into SetPillRadius.</summary>
    public static readonly DependencyProperty PillRadiusProperty = DependencyProperty.Register(
        nameof(PillRadius), typeof(double), typeof(DockWindow),
        new PropertyMetadata(RestHeight / 2, (d, e) => ((DockWindow)d).SetPillRadius((double)e.NewValue)));

    public double PillRadius
    {
        get => (double)GetValue(PillRadiusProperty);
        set => SetValue(PillRadiusProperty, value);
    }

    // ---- drag --------------------------------------------------------------------

    void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_moment == MomentKind.Quick) return; // the field owns the mouse
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
        else if (_mode == DockMode.Rest && CanHover && !OnCall)
        {
            _hoverIntent.Stop();
            SetMode(DockMode.Hover); // click beats the hover-intent wait
        }
    }

    // ---- positioning ----------------------------------------------------------------

    void Reposition()
    {
        if (_dragging) return;
        // Never throw: a throw here (headless RDP, no screens) would leave the
        // window parked off-screen with no retry.
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
                hide = quns is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE;
        }
        catch
        {
            // Treat failures as "not fullscreen".
        }
        if (hide == _fullscreenHidden) return;
        _fullscreenHidden = hide;
        if (hide && _moment == MomentKind.Quick) CloseQuick(restoreFocus: false);
        Visibility = hide ? Visibility.Hidden : Visibility.Visible;
        if (hide) return;
        Reposition();
        // A hide mid-morph froze the pill where it was — settle it whole.
        ApplyShape(animate: false);
        TryShowCard();
        ReplayPendingToast();
    }
}
