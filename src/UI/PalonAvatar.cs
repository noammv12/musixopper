using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Palon.UI;

/// <summary>What Palon is doing — the avatar's whole emotional range.</summary>
enum PalonMood { Idle, Listen, Think, Talk, Happy }

/// <summary>
/// Palon, drawn live: a dark glossy sphere whose cut-out blue eyes do the
/// acting. Size it with Width/Height (square); set Mood and it eases there.
/// This is the shared contract the Terminal and the dock build against.
/// </summary>
/// <remarks>
/// Frames come from <see cref="PalonEngine"/> (a port of docs/design/engine.js) and are drawn in
/// engine units (R=100, centred on 0,0) inside a 220x250 box trimmed from the web viewBox, uniformly fitted into the element.
/// All instances share one DispatcherTimer clock (not CompositionTarget.Rendering, which forces
/// WPF to compose every window at the monitor rate). Each instance is paced by <see cref="AvatarPacer"/>:
/// 60 fps only for large ones mid-motion in the active window, 12-30 fps otherwise, none while its
/// window is minimized. The clock stops when no instance needs frames.
/// </remarks>
sealed class PalonAvatar : FrameworkElement
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(PalonMood), typeof(PalonAvatar),
        new FrameworkPropertyMetadata(PalonMood.Idle, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PalonAvatar)d).ApplyMood()));

    public PalonMood Mood
    {
        get => (PalonMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>
    /// Where Palon should look, in element-relative units: (0,0) is straight ahead, x/y in -1..1
    /// (right/down positive; values beyond are clamped). null = no host target, fall back to the
    /// pointer when <see cref="FollowPointer"/> is on. Eased, never jumps.
    /// </summary>
    public static readonly DependencyProperty GazeProperty = DependencyProperty.Register(
        nameof(Gaze), typeof(Point?), typeof(PalonAvatar), new PropertyMetadata(null, (d, _) => ((PalonAvatar)d).Wake()));

    public Point? Gaze
    {
        get => (Point?)GetValue(GazeProperty);
        set => SetValue(GazeProperty, value);
    }

    /// <summary>When true (default), the eyes drift toward the mouse pointer while it is near the avatar,
    /// with a small "noticed you" glance when it arrives.</summary>
    public static readonly DependencyProperty FollowPointerProperty = DependencyProperty.Register(
        nameof(FollowPointer), typeof(bool), typeof(PalonAvatar), new PropertyMetadata(true));

    public bool FollowPointer
    {
        get => (bool)GetValue(FollowPointerProperty);
        set => SetValue(FollowPointerProperty, value);
    }

    /// <summary>True while the avatar is hooked to the frame clock (loaded and visible).</summary>
    public bool IsAnimating { get; private set; }

    // ---- look (docs/design/palon_notes.md) ----
    // Fitted box: the web viewBox (-125,-150,250,275) trimmed to what Palon can reach (tuft tip, hop), so small instances read bigger.
    const double VbX = -110, VbY = -140, VbW = 220, VbH = 250;
    static readonly Brush ShadeBrush = Frozen(new RadialGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0xa2, 0xa2, 0xa3), 0), new(Color.FromRgb(0x3b, 0x3b, 0x3d), 0.32),
        new(Color.FromRgb(0x0a, 0x0a, 0x0c), 0.72), new(Color.FromRgb(0x0a, 0x0a, 0x0c), 1),
    })
    {
        MappingMode = BrushMappingMode.Absolute, Center = new Point(-50.56, -63.2),
        GradientOrigin = new Point(-50.56, -63.2), RadiusX = 244.9, RadiusY = 244.9,
    });
    static readonly Brush EyeBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xA9, 0xD8, 0xFF), Color.FromRgb(0x3C, 0x86, 0xFF), new Point(0, -45), new Point(0, 55))
        { MappingMode = BrushMappingMode.Absolute });
    static readonly Brush HairBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0xE0, 0xAE, 0x4C), Color.FromRgb(0xFF, 0xE9, 0xA8), new Point(0, -60), new Point(0, -150))
        { MappingMode = BrushMappingMode.Absolute });
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ---- shared clock ----
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static double Now => Clock.Elapsed.TotalSeconds;
    static readonly List<PalonAvatar> Live = new();
    static DispatcherTimer? _clock;

    /// <summary>Diagnostics: live instances and whether the shared clock is running.</summary>
    public static (int Avatars, bool ClockOn) Probe() => (Live.Count, _clock?.IsEnabled == true);
    static int _instances;
    static bool _reducedMotion = !SystemParameters.ClientAreaAnimation;

    // ---- per instance ----
    readonly PalonState _state;
    readonly PalonFrame _frame = new();
    double _now, _lastTick;
    bool _cheering; double _cheerUntil;
    double _gYaw, _gPitch;            // eased gaze offsets (degrees)
    bool _pointerNear; double _noticeAt = -10;
    bool _dirty = true;
    double _lastFrame = -10, _interval;
    Window? _window;
    bool _windowActive = true, _windowMinimized;
    // pointer hit-test cache: screen origin + device px per DIP, refreshed a few times a second
    Point _scrOrigin; double _scrScale; double _scrAt = -10;

    public PalonAvatar()
    {
        _now = _lastTick = Now;
        _state = new PalonState(PalonMood.Idle, _now) { Seed = 1.7 + (_instances++ % 17) * 2.9 }; // desynced blinks
        Loaded += (_, _) => { AttachWindow(); UpdateHook(); };
        Unloaded += (_, _) => { DetachWindow(); UpdateHook(); };
        IsVisibleChanged += (_, _) => UpdateHook();
    }

    /// <summary>Plays the happy hop once, then settles back to the current mood.</summary>
    public void Cheer()
    {
        var now = Now;
        _cheering = true;
        _cheerUntil = now + (_state.Cur == PalonMood.Happy ? 0.95 : PalonEngine.Morph + 0.95); // one full hop, landing included
        if (_state.Cur == PalonMood.Happy) _state.BlinkAt = now;
        else PalonEngine.SetMood(_state, PalonMood.Happy, now, Motion);
        _dirty = true; InvalidateVisual(); Wake();
    }

    static double Motion => _reducedMotion ? 0 : 1;

    void ApplyMood()
    {
        if (_cheering) return; // the cheer settles into the new Mood when it ends
        PalonEngine.SetMood(_state, Mood, Now, Motion);
        _dirty = true; Wake();
    }

    // ---- host window: pause while minimized, slow down while in the background ----
    void AttachWindow()
    {
        var w = Window.GetWindow(this);
        if (ReferenceEquals(w, _window)) return;
        DetachWindow();
        _window = w;
        if (w is null) { _windowActive = true; _windowMinimized = false; return; }
        w.StateChanged += OnWindowChanged;
        w.Activated += OnWindowChanged;
        w.Deactivated += OnWindowChanged;
        ReadWindow();
    }

    void DetachWindow()
    {
        if (_window is null) return;
        _window.StateChanged -= OnWindowChanged;
        _window.Activated -= OnWindowChanged;
        _window.Deactivated -= OnWindowChanged;
        _window = null;
    }

    void ReadWindow()
    {
        if (_window is null) return;
        _windowMinimized = _window.WindowState == WindowState.Minimized;
        // Topmost tool windows (the dock) are rarely "active" but always in view.
        _windowActive = _window.IsActive || _window.Topmost;
    }

    void OnWindowChanged(object? sender, EventArgs e)
    {
        ReadWindow();
        _scrAt = -10;
        if (!_windowMinimized) { _lastTick = Now; _dirty = true; InvalidateVisual(); }
        Wake();
    }

    /// <summary>Something changed: make sure the clock is running at a rate this instance can use.</summary>
    void Wake()
    {
        if (!IsAnimating) return;
        _interval = AvatarPacer.FrameInterval(Math.Min(RenderSize.Width, RenderSize.Height), true, _windowActive, _windowMinimized);
        Reclock();
    }

    static void Reclock()
    {
        double best = 0;
        foreach (var a in Live) if (a._interval > 0 && (best == 0 || a._interval < best)) best = a._interval;
        if (best == 0) { _clock?.Stop(); return; }
        if (_clock is null)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Render);
            _clock.Tick += OnClock;
        }
        var iv = TimeSpan.FromSeconds(best);
        if (_clock.Interval != iv) _clock.Interval = iv;
        if (!_clock.IsEnabled) _clock.Start();
    }

    // ---- frame clock: one DispatcherTimer shared by every live avatar ----
    void UpdateHook()
    {
        bool want = IsLoaded && IsVisible;
        if (want == IsAnimating) return;
        IsAnimating = want;
        if (want)
        {
            _lastTick = Now; // no gaze-easing jump after being hidden
            _scrAt = -10;
            Live.Add(this);
            if (Live.Count == 1)
            {
                _reducedMotion = !SystemParameters.ClientAreaAnimation;
                SystemParameters.StaticPropertyChanged += OnSystemParameters;
            }
            _dirty = true; InvalidateVisual();
            Wake();
        }
        else
        {
            Live.Remove(this);
            _interval = 0;
            if (Live.Count == 0) SystemParameters.StaticPropertyChanged -= OnSystemParameters;
            Reclock();
        }
    }

    static void OnSystemParameters(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) _reducedMotion = !SystemParameters.ClientAreaAnimation;
    }

    static void OnClock(object? sender, EventArgs e)
    {
        double now = Now;
        bool? cursorOk = null; POINT cursor = default;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var a = Live[i];
            if (!AvatarPacer.Due(now, a._lastFrame, a._interval)) continue;
            a._lastFrame = now;
            a.Tick(now, ref cursorOk, ref cursor);
        }
        Reclock();
    }

    void Tick(double now, ref bool? cursorOk, ref POINT cursor)
    {
        double dt = Math.Clamp(now - _lastTick, 0, 0.1); _lastTick = now; _now = now;
        bool reduced = _reducedMotion;

        if (_cheering && now >= _cheerUntil)
        {
            _cheering = false;
            PalonEngine.SetMood(_state, Mood, now, Motion);
        }

        // gaze target: host Gaze wins, else the pointer when it is near
        double tYaw = 0, tPitch = 0; bool near = false;
        var g = Gaze;
        if (g is Point gp) { tYaw = Math.Clamp(gp.X, -1, 1) * 16; tPitch = -Math.Clamp(gp.Y, -1, 1) * 11; }
        else if (FollowPointer && !reduced && RenderSize.Width > 0)
        {
            cursorOk ??= GetCursorPos(out cursor);
            if (cursorOk == true && ScreenCache(now))
            {
                try
                {
                    var p = new Point((cursor.X - _scrOrigin.X) / _scrScale, (cursor.Y - _scrOrigin.Y) / _scrScale);
                    double r = Math.Min(RenderSize.Width, RenderSize.Height) / 2;
                    double dx = (p.X - RenderSize.Width / 2) / r, dy = (p.Y - RenderSize.Height / 2) / r;
                    double dist = Math.Sqrt(dx * dx + dy * dy), reach = Math.Max(3.2, 260 / Math.Max(r, 1)); // small avatars look farther
                    if (dist < reach)
                    {
                        near = true;
                        double k = Math.Min(1, dist / 2.2) / Math.Max(dist, 1e-6);
                        double falloff = 1 - PalonEngine.Clamp((dist - reach * 0.7) / (reach * 0.3)); // fade at the edge of reach
                        tYaw = dx * k * 14 * falloff; tPitch = -dy * k * 10 * falloff;
                    }
                }
                catch (InvalidOperationException) { }
            }
        }
        if (near && !_pointerNear && !reduced && now - _noticeAt > 2.5)
        {
            _noticeAt = now;                                     // "noticed you": quick blink + perk up
            if (now - _state.BlinkAt > 0.6) _state.BlinkAt = now;
        }
        _pointerNear = near;

        double rate = now - _noticeAt < 0.35 ? 14 : 5;           // snappier during the glance
        double a = 1 - Math.Exp(-dt * rate);
        double oy = _gYaw, op = _gPitch;
        _gYaw += (tYaw - _gYaw) * a; _gPitch += (tPitch - _gPitch) * a;
        bool gazeMoving = Math.Abs(_gYaw - oy) + Math.Abs(_gPitch - op) > 0.01;

        bool active = !reduced || _dirty || gazeMoving || _cheering || now - _noticeAt < 0.7
            || PalonEngine.IsMorphing(_state, now) || PalonEngine.IsBlinking(_state, now);
        if (active || _wasActive) InvalidateVisual(); // reduced motion: draw only while something changes, plus one settle frame
        bool busy = _dirty || gazeMoving || _cheering || now - _noticeAt < 0.7
            || PalonEngine.IsMorphing(_state, now) || PalonEngine.IsBlinking(_state, now);
        _wasActive = active; _dirty = false;
        // Next frame budget. Reduced motion with nothing moving needs no clock at all (Wake restarts it).
        _interval = reduced && !active
            ? 0
            : AvatarPacer.FrameInterval(Math.Min(RenderSize.Width, RenderSize.Height), busy || near, _windowActive, _windowMinimized);
    }
    bool _wasActive;

    /// <summary>Refreshes the element's screen origin/scale (for the pointer test) a few times a second
    /// instead of a PointFromScreen per frame per instance.</summary>
    bool ScreenCache(double now)
    {
        if (now - _scrAt < 0.25) return _scrScale > 0;
        _scrAt = now; _scrScale = 0;
        if (PresentationSource.FromVisual(this) == null) return false;
        try
        {
            var o = PointToScreen(new Point(0, 0));
            var x = PointToScreen(new Point(100, 0));
            _scrOrigin = o;
            _scrScale = Math.Abs(x.X - o.X) / 100;
        }
        catch (InvalidOperationException) { }
        return _scrScale > 0;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;
        double s = Math.Min(w / VbW, h / VbH);
        if (s <= 0 || !double.IsFinite(s)) return;
        if (!IsAnimating) _now = Now;

        double now = _now;
        double notice = now - _noticeAt, perk = notice < 0.6 ? Math.Sin(Math.PI * notice / 0.6) * 4 : 0;
        var x = new PalonExtras
        {
            Motion = Motion,
            GazeYaw = _gYaw,
            GazePitch = _gPitch + perk,
            HairWidth = Math.Max(1, 2.2 / (15 * s)), // thinnest strand stays >= ~2.2 device-independent px
        };
        PalonEngine.Sample(_state, now, _frame, 100, x);

        // Fit the viewBox, centred; draw in viewBox units so the frozen Absolute brushes line up.
        if (_fit is null || w != _fitW || h != _fitH)
        {
            _fit = new MatrixTransform(s, 0, 0, s, (w - VbW * s) / 2 - VbX * s, (h - VbH * s) / 2 - VbY * s);
            _fit.Freeze(); _fitW = w; _fitH = h;
        }
        dc.PushTransform(_fit);

        var hair = new StreamGeometry();
        using (var c = hair.Open())
        {
            var H = _frame.Hair;
            for (int j = 0; j < 3; j++)
            {
                int o = j * 10;
                c.BeginFigure(new Point(H[o], H[o + 1]), true, true);
                c.QuadraticBezierTo(new Point(H[o + 2], H[o + 3]), new Point(H[o + 4], H[o + 5]), true, true);
                c.QuadraticBezierTo(new Point(H[o + 6], H[o + 7]), new Point(H[o + 8], H[o + 9]), true, true);
            }
        }
        hair.Freeze();

        var eyes = new StreamGeometry();
        using (var c = eyes.Open()) { Catmull(c, _frame.EyeL); Catmull(c, _frame.EyeR); }
        eyes.Freeze();

        // Body with the eyes as holes: eyes lie inside the body, so EvenOdd cuts them out
        // (cheap equivalent of the web mask / CombinedGeometry.Exclude).
        var shade = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var c = shade.Open()) { Catmull(c, _frame.Body); Catmull(c, _frame.EyeL); Catmull(c, _frame.EyeR); }
        shade.Freeze();

        dc.DrawGeometry(HairBrush, null, hair);   // tuft behind the body
        dc.DrawGeometry(EyeBrush, null, eyes);    // blue shows through the holes
        dc.DrawGeometry(ShadeBrush, null, shade);
        dc.Pop();
    }

    MatrixTransform? _fit; double _fitW, _fitH;

    /// <summary>Closed Catmull-Rom (tension 1/6) through interleaved points, as engine.js catmull().</summary>
    static void Catmull(StreamGeometryContext c, double[] P)
    {
        int n = P.Length / 2;
        c.BeginFigure(new Point(P[0], P[1]), true, true);
        for (int i = 0; i < n; i++)
        {
            int i0 = (i - 1 + n) % n, i2 = (i + 1) % n, i3 = (i + 2) % n;
            c.BezierTo(
                new Point(P[i * 2] + (P[i2 * 2] - P[i0 * 2]) / 6, P[i * 2 + 1] + (P[i2 * 2 + 1] - P[i0 * 2 + 1]) / 6),
                new Point(P[i2 * 2] - (P[i3 * 2] - P[i * 2]) / 6, P[i2 * 2 + 1] - (P[i3 * 2 + 1] - P[i * 2 + 1]) / 6),
                new Point(P[i2 * 2], P[i2 * 2 + 1]), true, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT p);
}
