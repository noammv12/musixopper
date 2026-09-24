using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Palon.Notes;
using Palon.Sales;
using Palon.Terminal;

namespace Palon.UI;

/// <summary>
/// The Terminal: Palon's workspace. A borderless dark-glass window (Win11
/// acrylic backdrop when available, solid black otherwise) with a thin top
/// bar, ONE screen — Now — and side sheets (Month, Callbacks, Clients,
/// Templates, Coaching, Memory, Calls) that slide in from the icon rail while
/// Now scales back. Modal sheets, the Ask overlay and the rituals' curtain
/// sit above. One instance at a time — open it with <see cref="ShowSingleton"/>.
/// </summary>
sealed class TerminalWindow : Window
{
    static TerminalWindow? _instance;

    /// <summary>The open Terminal, if any (for sheets opened from outside, e.g. the dock).</summary>
    internal static TerminalWindow? Instance => _instance;
    static Func<CallState> _callState = () => CallState.Idle;
    static Func<string?> _currentNumber = () => null;

    /// <summary>Set once by Shell so the top bar and Ask know the call state.</summary>
    public static void Configure(Func<CallState> callState, Func<string?> currentNumber)
    {
        _callState = callState;
        _currentNumber = currentNumber;
    }

    /// <summary>Shown when the Terminal fails to open (Shell wires the dock toast).</summary>
    internal static Action<string>? ReportFailure;

    /// <summary>Opens the Terminal, or brings it to the front (optionally with a page's side sheet open).</summary>
    public static void ShowSingleton(TerminalPage? page = null)
    {
        TerminalWindow? created = null;
        try
        {
            if (_instance is null)
            {
                Log.Write("Terminal: opening");
                created = new TerminalWindow();
                _instance = created;
                created.Closed += (_, _) => { if (ReferenceEquals(_instance, created)) _instance = null; };
                created.Show();
                Log.Write("Terminal: opened");
            }
            var w = _instance!;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            if (page is TerminalPage p) w.Navigate(p);
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal: failed: {ex}");
            if (created is not null)
            {
                try { created.Close(); } catch { }
                _instance = null;
            }
            var msg = "הטרמינל לא נפתח: " + ex.Message;
            try
            {
                if (ReportFailure is { } report) report(msg);
                else MessageBox.Show(msg, "Palon", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception inner)
            {
                Log.Write($"Terminal: failure report failed: {inner.Message}");
            }
        }
    }

    TerminalScreen MakeScreen(TerminalPage page, Func<TerminalScreen> make)
    {
        try
        {
            return make();
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal screen {page} failed to build: {ex}");
            return new FailedScreen(this, page.ToString());
        }
    }

    sealed class FailedScreen : TerminalScreen
    {
        readonly string _title;

        public FailedScreen(TerminalWindow host, string title) : base(host)
        {
            _title = title;
            Children.Add(Kit.T("המסך הזה נכשל בטעינה — ראה לוג", Display.Section, Tone.RedText, FontWeights.SemiBold, wrap: true));
        }

        public override string Title => _title;
        public override void Render(TermData data, bool entrance) { }
    }

    /// <summary>Opens the Terminal with the Ask overlay up.</summary>
    public static void ShowAsk()
    {
        ShowSingleton();
        _instance?.OpenAsk();
    }

    /// <summary>
    /// The dock chip / hotkey screen read: capture → privacy gate first
    /// (nothing opens or sends before the user approves), then the Terminal
    /// opens on Ask and reads the approved image there, with Copy.
    /// </summary>
    public static async void ReadScreenFromShortcut()
    {
        try
        {
            var (shot, error) = await Vision.ScreenReader.CaptureAsync(windowMode: false,
                "Palon יקרא את מה שבתמונה ויסכם. אפשר לשאול עליו שאלות המשך ב-Ask.");
            if (shot is null && error is null) return; // cancelled — nothing happens
            ShowAsk();
            if (_instance?._ask is not { } ask) return;
            if (shot is null) ask.ShowNotice("קרא מהמסך", error!);
            else ask.ReadApprovedShot(shot, receipt: false);
        }
        catch (Exception ex)
        {
            Log.Write($"Screen read shortcut failed: {ex}");
        }
    }

    /// <summary>Receipt → deal: Ask opens and starts the receipt capture (privacy gate first).</summary>
    public void ReadReceipt()
    {
        OpenAsk();
        _ask?.StartReadScreen(receipt: true);
    }

    internal Func<CallState> CallStateSource => _callState;
    internal Func<string?> CurrentNumberSource => _currentNumber;

    readonly Grid _root = new();
    readonly Border _ground = new();
    readonly Border _stage = new();
    readonly ScaleTransform _stageScale = new(1, 1);
    readonly TranslateTransform _stageMove = new();
    readonly Grid _sideLayer = new();
    readonly Border _sideScrim = new();
    readonly Border _side;
    readonly TranslateTransform _sideMove = new();
    readonly ScrollViewer _sideScroll = new();
    readonly TextBlock _sideKicker;
    readonly Grid _curtain = new();
    readonly Grid _overlay = new();
    readonly Grid _toastLayer = new();
    // Side-sheet screens are built on first open, not all at window open.
    readonly LazyRegistry<TerminalPage, TerminalScreen> _screens = new();
    readonly Dictionary<TerminalPage, Border> _railItems = new();
    readonly DispatcherTimer _clock;
    readonly DispatcherTimer _refresh;
    readonly DispatcherTimer _toastTimer;
    readonly TerminalScreen _now;

    TerminalPage? _sidePage;
    TextBlock _menuStatus = null!, _menuClock = null!, _menuDate = null!;
    Border _menuDot = null!;
    Border _holiday = null!;
    Border _badge = null!;
    TextBlock _badgeText = null!;
    AskPanel? _ask;
    Border? _sheet;
    Action? _toastAction;
    Action? _curtainClosed;
    bool _selfMutating;
    bool _glass;
    bool _loaded;

    TerminalWindow()
    {
        Title = "Palon";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Tone.Ground;
        FontFamily = Font.Family;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 1080;
        MinHeight = 700;
        ShowInTaskbar = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1440, work.Width * 0.94);
        Height = Math.Min(900, work.Height * 0.94);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;

        _root.FlowDirection = FlowDirection.RightToLeft;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _ground.Background = NowGround();
        Grid.SetRowSpan(_ground, 2);
        _root.Children.Add(_ground);

        // Now, on a stage that scales back when a side sheet opens.
        _stage.RenderTransformOrigin = new Point(0.5, 0.5);
        _stage.RenderTransform = new TransformGroup { Children = { _stageScale, _stageMove } };
        Grid.SetRow(_stage, 1);
        _root.Children.Add(_stage);

        _root.Children.Add(BuildMenuBar());

        var rail = BuildRail();
        Grid.SetRow(rail, 1);
        _root.Children.Add(rail);

        // Side sheets: from the visual left (the rail's side), with a scrim over Now.
        Grid.SetRowSpan(_sideLayer, 2);
        _sideLayer.Visibility = Visibility.Collapsed;
        _sideScrim.Background = Tone.B("#8C000000");
        _sideScrim.MouseLeftButtonDown += (_, _) => CloseSide();
        _sideLayer.Children.Add(_sideScrim);
        _side = Fx.Glass2(32, new Thickness(0));
        _side.HorizontalAlignment = HorizontalAlignment.Right; // RTL: the visual left edge
        _side.Margin = new Thickness(14);
        _side.RenderTransform = _sideMove;
        _side.MouseLeftButtonDown += (_, e) => e.Handled = true;
        var sideGrid = new Grid();
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sideKicker = Kit.T("", 12.5, Tone.Muted, FontWeights.Medium);
        _sideKicker.VerticalAlignment = VerticalAlignment.Center;
        var sideClose = Kit.IconButton(Icons.Close, 32, CloseSide, icon: 14);
        System.Windows.Automation.AutomationProperties.SetName(sideClose, "סגור");
        var sideHead = Kit.Bar(_sideKicker, sideClose);
        sideHead.Margin = new Thickness(30, 16, 16, 0);
        sideGrid.Children.Add(sideHead);
        _sideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _sideScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _sideScroll.Focusable = false;
        _sideScroll.Padding = new Thickness(30, 6, 30, 30);
        Ui.ThinScroll(_sideScroll);
        Grid.SetRow(_sideScroll, 1);
        sideGrid.Children.Add(_sideScroll);
        _side.Child = sideGrid;
        _sideLayer.Children.Add(_side);
        _root.Children.Add(_sideLayer);

        Grid.SetRowSpan(_curtain, 2);
        _curtain.Visibility = Visibility.Collapsed;
        _root.Children.Add(_curtain);

        Grid.SetRowSpan(_toastLayer, 2);
        _toastLayer.IsHitTestVisible = true;
        _toastLayer.VerticalAlignment = VerticalAlignment.Top;
        _toastLayer.HorizontalAlignment = HorizontalAlignment.Center;
        _toastLayer.Margin = new Thickness(0, 54, 0, 0);
        _root.Children.Add(_toastLayer);

        Grid.SetRowSpan(_overlay, 2);
        _overlay.Visibility = Visibility.Collapsed;
        _root.Children.Add(_overlay);

        Content = _root;

        _now = MakeScreen(TerminalPage.Today, () => new NowScreen(this));
        _screens.Set(TerminalPage.Today, _now);
        _stage.Child = _now;
        _screens.Register(TerminalPage.Callbacks, () => MakeScreen(TerminalPage.Callbacks, () => new CallbacksScreen(this)));
        _screens.Register(TerminalPage.Month, () => MakeScreen(TerminalPage.Month, () => new MonthScreen(this)));
        _screens.Register(TerminalPage.Clients, () => MakeScreen(TerminalPage.Clients, () => new ClientsScreen(this)));
        _screens.Register(TerminalPage.Templates, () => MakeScreen(TerminalPage.Templates, () => new TemplatesScreen(this)));
        _screens.Register(TerminalPage.Coaching, () => MakeScreen(TerminalPage.Coaching, () => new CoachingScreen(this)));
        _screens.Register(TerminalPage.Memory, () => MakeScreen(TerminalPage.Memory, () => new MemoryScreen(this)));
        _screens.Register(TerminalPage.Calls, () => MakeScreen(TerminalPage.Calls, () => new CallsScreen(this)));
        _screens.Register(TerminalPage.Settings, () => MakeScreen(TerminalPage.Settings, () => new SettingsScreen(this)));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => UpdateClock();
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Render(entrance: false);
        };
        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) => HideToast();

        CallbackStore.Changed += OnStoreChanged;
        NotesStore.Changed += OnStoreChanged;
        SalesStore.Changed += OnStoreChanged;
        TemplatesStore.Changed += OnStoreChanged;
        TemplateLearningStore.Changed += OnStoreChanged;
        CallStatsStore.Changed += OnStoreChanged;
        SnippetStore.Changed += OnStoreChanged;
        Memory.MemoryStore.Changed += OnStoreChanged;
        Memory.MemoryStore.Remembered += OnRemembered;
        Memory.MemoryStore.Forgotten += OnForgotten;

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => ApplyBackdrop();
        Loaded += (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                Render(entrance: true);
            }
            _clock.Start();
        };
        StateChanged += (_, _) =>
            // Maximized borderless windows overhang the screen by the resize border.
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        SizeChanged += (_, _) => SizeSide();
        Closed += (_, _) =>
        {
            _clock.Stop();
            _refresh.Stop();
            _toastTimer.Stop();
            _ask?.Cancel();
            CallbackStore.Changed -= OnStoreChanged;
            Memory.MemoryStore.Changed -= OnStoreChanged;
            Memory.MemoryStore.Remembered -= OnRemembered;
            Memory.MemoryStore.Forgotten -= OnForgotten;
            NotesStore.Changed -= OnStoreChanged;
            SalesStore.Changed -= OnStoreChanged;
            TemplatesStore.Changed -= OnStoreChanged;
            TemplateLearningStore.Changed -= OnStoreChanged;
            CallStatsStore.Changed -= OnStoreChanged;
            SnippetStore.Changed -= OnStoreChanged;
        };
    }

    static Brush NowGround()
    {
        // The design's ground: a graphite glow top-right, a faint one bottom-left, black.
        var group = new DrawingBrush
        {
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(Fx.Vertical("#FF0A0A0C", "#FF000000"), null, new RectangleGeometry(new Rect(0, 0, 1, 1))),
                    new GeometryDrawing(new RadialGradientBrush(Tone.C("#FF1D1E23"), Tone.C("#00141418")) { Center = new Point(0.16, 0.14), GradientOrigin = new Point(0.16, 0.14), RadiusX = 0.62, RadiusY = 0.7 }, null, new RectangleGeometry(new Rect(0, 0, 1, 1))),
                    new GeometryDrawing(new RadialGradientBrush(Tone.C("#FF121317"), Tone.C("#00000000")) { Center = new Point(0.7, 1.1), GradientOrigin = new Point(0.7, 1.1), RadiusX = 0.76, RadiusY = 0.78 }, null, new RectangleGeometry(new Rect(0, 0, 1, 1))),
                },
            },
            Stretch = Stretch.Fill,
        };
        group.Freeze();
        return group;
    }

    // ---- data refresh ------------------------------------------------------------

    void OnStoreChanged()
    {
        // Own edits already updated their rows in place (and are mid-animation):
        // just refresh the chrome. Anything else re-renders.
        if (_selfMutating && Dispatcher.CheckAccess())
        {
            RenderChrome(TermData.Load());
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            _refresh.Stop();
            _refresh.Start();
        });
    }

    /// <summary>Runs a store write whose result the caller already shows.</summary>
    public T Quietly<T>(Func<T> write)
    {
        _selfMutating = true;
        try
        {
            return write();
        }
        finally
        {
            _selfMutating = false;
        }
    }

    public void Quietly(Action write) => Quietly(() => { write(); return 0; });

    /// <summary>Re-renders Now (and the open side sheet) from disk (no entrance).</summary>
    public void Refresh() => Render(entrance: false);

    void Render(bool entrance, bool sideEntrance = false)
    {
        TermData data;
        try
        {
            data = TermData.Load();
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal load failed: {ex.Message}");
            return;
        }
        RenderChrome(data);
        RenderScreen(TerminalPage.Today, data, entrance);
        if (_sidePage is TerminalPage side) RenderScreen(side, data, sideEntrance);
    }

    void RenderScreen(TerminalPage page, TermData data, bool entrance)
    {
        try
        {
            Kit.Entering = entrance && Fx.Allowed(this);
            _screens[page].Render(data, entrance);
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal render ({page}) failed: {ex}");
        }
        finally
        {
            Kit.Entering = false;
        }
    }

    /// <summary>Today = Now (closes any side sheet); any other page opens as a side sheet.</summary>
    public void Navigate(TerminalPage page, bool force = false)
    {
        if (page == TerminalPage.Today)
        {
            CloseSide();
            return;
        }
        if (page == _sidePage && !force) return;
        if (page != _sidePage) CallbackRows.RecentlyDone.Clear(); // checked-off rows stay only while their sheet is open
        OpenSide(page);
    }

    /// <summary>Navigate plus an optional argument (a client name for Clients).</summary>
    public void Navigate(TerminalPage page, string? arg)
    {
        Navigate(page);
        if (page == TerminalPage.Clients && !string.IsNullOrWhiteSpace(arg) && _screens[page] is ClientsScreen clients)
            clients.ShowClient(arg);
    }

    /// <summary>The Now screen (for agentic presentation: result cards, nudges).</summary>
    internal NowScreen? Now => _now as NowScreen;

    // ---- side sheets -------------------------------------------------------------

    static string SideKicker(TerminalPage page) => page switch
    {
        TerminalPage.Month => He.Month(DateTime.Now.Month) + " " + DateTime.Now.Year,
        TerminalPage.Callbacks => "מה שקבעת",
        TerminalPage.Clients => "אנשים",
        TerminalPage.Templates => "מוכנות להעתקה",
        TerminalPage.Coaching => "מה עובד לך",
        TerminalPage.Memory => "מה Palon זוכר",
        TerminalPage.Calls => "השיחות האחרונות",
        TerminalPage.Settings => "Palon שלך",
        _ => "",
    };

    void SizeSide()
    {
        var w = ActualWidth;
        if (w <= 0) return;
        _side.Width = Math.Clamp(w * 0.64, 640, 940);
    }

    void OpenSide(TerminalPage page)
    {
        var wasOpen = _sidePage is not null;
        _sidePage = page;
        _sideKicker.Text = SideKicker(page);
        _sideScroll.Content = _screens[page];
        _sideScroll.ScrollToTop();
        foreach (var (key, item) in _railItems) SetRailActive(item, key == page);
        SizeSide();
        _sideLayer.Visibility = Visibility.Visible;
        Render(entrance: false, sideEntrance: true);
        if (wasOpen) return;
        var moving = Fx.Allowed(this);
        var from = _side.Width + 40;
        if (moving)
        {
            _sideMove.BeginAnimation(TranslateTransform.XProperty, Feel.FromTo(from, 0, Feel.Sheet + 100));
            _side.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 300));
            _sideScrim.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 350));
        }
        Depth(0.965, 0.72, -18, moving);
    }

    public void CloseSide()
    {
        if (_sidePage is null) return;
        _sidePage = null;
        CallbackRows.RecentlyDone.Clear();
        foreach (var item in _railItems.Values) SetRailActive(item, false);
        var moving = Fx.Allowed(this);
        Depth(1, 1, 0, moving);
        if (!moving)
        {
            _sideLayer.Visibility = Visibility.Collapsed;
            _sideScroll.Content = null;
            return;
        }
        var slide = Feel.To(_side.ActualWidth + 40, 360, Motion.InOut);
        slide.Completed += (_, _) =>
        {
            if (_sidePage is not null) return; // reopened meanwhile
            _sideLayer.Visibility = Visibility.Collapsed;
            _sideScroll.Content = null;
        };
        _sideMove.BeginAnimation(TranslateTransform.XProperty, slide);
        _sideScrim.BeginAnimation(OpacityProperty, Feel.To(0, 300));
    }

    /// <summary>Now's depth: scale + dim + a nudge away from whatever slid in.</summary>
    void Depth(double scale, double opacity, double shift, bool animate)
    {
        if (animate)
        {
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.To(scale, 600));
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.To(scale, 600));
            _stageMove.BeginAnimation(TranslateTransform.XProperty, Feel.To(shift, 600));
            _stage.BeginAnimation(OpacityProperty, Feel.To(opacity, 450));
            return;
        }
        _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _stageMove.BeginAnimation(TranslateTransform.XProperty, null);
        _stage.BeginAnimation(OpacityProperty, null);
        _stageScale.ScaleX = _stageScale.ScaleY = scale;
        _stageMove.X = shift;
        _stage.Opacity = opacity;
    }

    // ---- rituals' curtain ------------------------------------------------------------

    /// <summary>A full-window ritual (morning briefing, day recap): Now sinks back
    /// behind it; closing lets Now rise forward. Returns the close action.</summary>
    public Action ShowCurtain(FrameworkElement content, bool dark, Action? onClosed)
    {
        _curtain.BeginAnimation(OpacityProperty, null);
        _curtain.Opacity = 1;
        _curtain.Children.Clear();
        _curtain.Background = dark ? Brushes.Black : Fx.Glow("#FA1B1C20", "#FA040405");
        content.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        content.RenderTransform = scale;
        _curtain.Children.Add(content);
        _curtain.Visibility = Visibility.Visible;
        _curtainClosed = onClosed;
        var moving = Fx.Allowed(this);
        Depth(0.93, 0.2, 0, animate: false);
        if (moving)
        {
            _curtain.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 500));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(1.04, 1, 800));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.FromTo(1.04, 1, 800));
        }
        return () =>
        {
            if (_curtain.Visibility != Visibility.Visible || !_curtain.Children.Contains(content)) return;
            Depth(_sidePage is null ? 1 : 0.965, _sidePage is null ? 1 : 0.72, _sidePage is null ? 0 : -18, Fx.Allowed(this));
            var closed = _curtainClosed;
            _curtainClosed = null;
            if (!Fx.Allowed(this))
            {
                _curtain.Visibility = Visibility.Collapsed;
                _curtain.Children.Clear();
                closed?.Invoke();
                return;
            }
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.To(1.12, 900));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.To(1.12, 900));
            var fade = Feel.To(0, 700, Motion.Out);
            fade.Completed += (_, _) =>
            {
                if (!_curtain.Children.Contains(content)) return;
                _curtain.Visibility = Visibility.Collapsed;
                _curtain.Children.Clear();
                closed?.Invoke();
            };
            _curtain.BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>Lights-out: the curtain fades to pure black.</summary>
    public void DarkenCurtain()
    {
        if (_curtain.Visibility != Visibility.Visible) return;
        _curtain.Background = Brushes.Black;
    }

    bool CurtainOpen => _curtain.Visibility == Visibility.Visible;

    // ---- top bar -------------------------------------------------------------------

    UIElement BuildMenuBar()
    {
        var bar = new Border
        {
            Background = Tone.B("#40000000"),
            BorderBrush = Tone.B("#0DFFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(26, 0, 10, 0),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _menuDot = Kit.Dot(Tone.Green, 7);
        var brand = Kit.T("Palon", 13.5, Tone.Text, FontWeights.SemiBold);
        brand.FlowDirection = FlowDirection.LeftToRight;
        brand.Margin = new Thickness(8, 0, 0, 0);
        brand.Cursor = Cursors.Hand;
        brand.ToolTip = "המוח של Palon";
        Kit.Clickable(brand, () => BrainSheet.Show(this));
        _menuStatus = Kit.T("", 13, Tone.Muted);
        _menuStatus.Margin = new Thickness(12, 0, 0, 0);
        var start = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        start.Children.Add(_menuDot);
        start.Children.Add(brand);
        start.Children.Add(_menuStatus);
        g.Children.Add(start);

        var end = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var holidayText = Kit.T("", 12.5, Tone.B("#FFE8CFA0"), FontWeights.Medium);
        _holiday = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 16, 0),
            Background = Tone.B("#1AE8BE6E"), BorderBrush = Tone.B("#33E8BE6E"), BorderThickness = new Thickness(1),
            Child = Kit.Row(6, Kit.Dot(Tone.B("#FFE8BE6E"), 6), holidayText), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
        };
        if (NowRituals.Holiday(DateTime.Now) is { } h)
        {
            holidayText.Text = h.Greeting;
            _holiday.Visibility = Visibility.Visible;
        }
        end.Children.Add(_holiday);
        _menuDate = Kit.T("", 13, Tone.Muted);
        _menuDate.VerticalAlignment = VerticalAlignment.Center;
        end.Children.Add(_menuDate);
        _menuClock = Kit.Num("", 13, Tone.TextSoft);
        _menuClock.FlowDirection = FlowDirection.LeftToRight;
        _menuClock.Margin = new Thickness(12, 0, 0, 0);
        _menuClock.VerticalAlignment = VerticalAlignment.Center;
        end.Children.Add(_menuClock);
        var model = ModelPicker.Pill(this, compact: true);
        model.Margin = new Thickness(18, 0, 0, 0);
        end.Children.Add(model);
        var min = Kit.IconButton(Icons.Minimize, 28, () => WindowState = WindowState.Minimized, icon: 14);
        min.Margin = new Thickness(14, 0, 0, 0);
        var max = Kit.IconButton(Icons.Maximize, 28, ToggleMaximize, icon: 12);
        max.Margin = new Thickness(4, 0, 0, 0);
        var close = Kit.IconButton(Icons.Close, 28, Close, icon: 14);
        close.Margin = new Thickness(4, 0, 0, 0);
        end.Children.Add(min);
        end.Children.Add(max);
        end.Children.Add(close);
        Grid.SetColumn(end, 2);
        g.Children.Add(end);
        bar.Child = g;

        // The bar is the drag region; double-click toggles maximize.
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && (IsInside(d, end) || IsInside(d, brand))) return;
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        };
        return bar;
    }

    static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, container)) return true;
        return false;
    }

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void RenderChrome(TermData data)
    {
        UpdateClock();
        var badge = data.Counts.Badge;
        _badgeText.Text = badge > 99 ? "99+" : badge.ToString();
        if (badge > 0 && _badge.Visibility != Visibility.Visible)
        {
            _badge.Visibility = Visibility.Visible;
            Kit.Pop(_badge);
        }
        else if (badge == 0) _badge.Visibility = Visibility.Collapsed;
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        _menuClock.Text = He.Clock(now);
        _menuDate.Text = NowRituals.LongDate(now, withClock: false);
        var state = _callState();
        (_menuDot.Background, _menuStatus.Text) = state switch
        {
            CallState.OnCall => ((Brush)Tone.Amber, "בשיחה · מקשיב ורושם"),
            CallState.Disabled => (Tone.Dim, "כבוי"),
            _ => (Tone.Green, "מקשיב לשיחות"),
        };
    }

    // ---- the icon rail ----------------------------------------------------------------

    UIElement BuildRail()
    {
        var col = new StackPanel();
        void Add(string tip, string icon, Action run, TerminalPage? page)
        {
            var glyph = Kit.Icon(icon, 20, Tone.TextSoft);
            var tile = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Child = glyph, Cursor = Cursors.Hand,
                Focusable = true, FocusVisualStyle = null, Margin = new Thickness(0, col.Children.Count > 0 ? 6 : 0, 0, 0),
                ToolTip = new ToolTip { Content = tip, Placement = System.Windows.Controls.Primitives.PlacementMode.Right },
            };
            Kit.HoverFill(tile, Tone.B("#00FFFFFF"), Tone.FillHover);
            Kit.Press(tile, 0.9);
            Kit.Clickable(tile, run);
            System.Windows.Automation.AutomationProperties.SetName(tile, tip);
            if (page is TerminalPage p)
            {
                _railItems[p] = tile;
                if (p == TerminalPage.Callbacks)
                {
                    _badgeText = Kit.Num("", 10.5, Tone.Text, FontWeights.Bold);
                    _badgeText.HorizontalAlignment = HorizontalAlignment.Center;
                    _badgeText.VerticalAlignment = VerticalAlignment.Center;
                    _badge = new Border
                    {
                        MinWidth = 17, Height = 17, CornerRadius = new CornerRadius(8.5), Padding = new Thickness(4, 0, 4, 0),
                        Background = Tone.Red, BorderBrush = Tone.B("#FF121216"), BorderThickness = new Thickness(1.5),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, -4, -4, 0), Child = _badgeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
                    };
                    tile.Child = null;
                    tile.Child = new Grid { Children = { glyph, _badge } };
                }
            }
            col.Children.Add(tile);
        }
        Add("החודש · Ctrl 2", Icons.Month, () => ToggleSide(TerminalPage.Month), TerminalPage.Month);
        Add("חזרות · Ctrl 3", Icons.Callbacks, () => ToggleSide(TerminalPage.Callbacks), TerminalPage.Callbacks);
        Add("לקוחות · Ctrl 4", Icons.Clients, () => ToggleSide(TerminalPage.Clients), TerminalPage.Clients);
        Add("תבניות · Ctrl 5", Icons.Templates, () => ToggleSide(TerminalPage.Templates), TerminalPage.Templates);
        Add("אימון · Ctrl 6", Icons.Coach, () => ToggleSide(TerminalPage.Coaching), TerminalPage.Coaching);
        Add("זיכרון · Ctrl 7", Icons.Memory, () => ToggleSide(TerminalPage.Memory), TerminalPage.Memory);
        Add("שיחות · Ctrl 8", NowIcons.Wave, () => ToggleSide(TerminalPage.Calls), TerminalPage.Calls);
        col.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 8, 8, 2), Background = Tone.Hairline });
        Add("Salesforce", NowIcons.Cloud, () => SalesforceSheets.Settings(this), null);
        Add("הגדרות · Ctrl 9", Icons.Gear, () => ToggleSide(TerminalPage.Settings), TerminalPage.Settings);

        var pill = Kit.Glass(28, new Thickness(8));
        pill.Child = col;
        pill.HorizontalAlignment = HorizontalAlignment.Right; // RTL: visual left
        pill.VerticalAlignment = VerticalAlignment.Center;
        pill.Margin = new Thickness(0, 0, 22, 0);
        return pill;
    }

    void ToggleSide(TerminalPage page)
    {
        if (_sidePage == page) CloseSide();
        else Navigate(page);
    }

    static void SetRailActive(Border tile, bool on)
    {
        tile.BorderBrush = on ? Tone.B("#33FFFFFF") : null;
        tile.BorderThickness = new Thickness(on ? 1 : 0);
        if (tile.Background is SolidColorBrush b && !b.IsFrozen)
            b.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(
                on ? Tone.C("#29FFFFFF") : Tone.C("#00FFFFFF"), TimeSpan.FromMilliseconds(300)) { EasingFunction = Feel.Expo });
    }

    // ---- overlays: sheets, Ask, toast ------------------------------------------

    /// <summary>Shows a modal glass sheet; returns its close action.</summary>
    public Action ShowSheet(FrameworkElement body, double width, double? maxHeight = null)
    {
        CloseOverlay(immediate: true);
        var card = Kit.DeepGlass(32, new Thickness(28));
        card.Width = width;
        if (maxHeight is double mh) card.MaxHeight = Math.Min(mh, ActualHeight - 80);
        else card.MaxHeight = Math.Max(300, ActualHeight - 80);
        card.Child = body;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        OpenOverlay(card, top: false);
        _sheet = card;
        return () => CloseOverlay(immediate: false);
    }

    public void OpenAsk()
    {
        if (_ask is not null && _overlay.Visibility == Visibility.Visible) return;
        CloseOverlay(immediate: true);
        _ask = new AskPanel(this);
        _ask.HorizontalAlignment = HorizontalAlignment.Center;
        _ask.VerticalAlignment = VerticalAlignment.Top;
        _ask.Margin = new Thickness(0, 70, 0, 0);
        _ask.MouseLeftButtonDown += (_, e) => e.Handled = true;
        OpenOverlay(_ask, top: true);
        _ask.FocusInput();
    }

    void OpenOverlay(FrameworkElement content, bool top)
    {
        _overlay.BeginAnimation(OpacityProperty, null);
        _overlay.Opacity = 1;
        _overlay.Children.Clear();
        var scrim = new Border { Background = top ? Tone.B("#B3000000") : Tone.Scrim };
        scrim.MouseLeftButtonDown += (_, _) => CloseOverlay(immediate: false);
        _overlay.Children.Add(scrim);
        _overlay.Children.Add(content);
        _overlay.Visibility = Visibility.Visible;
        if (!Fx.Allowed(this)) return;
        scrim.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 350));
        var move = new TranslateTransform(0, 30);
        var scale = new ScaleTransform(0.95, 0.95);
        content.RenderTransformOrigin = new Point(0.5, 0.5);
        content.RenderTransform = new TransformGroup { Children = { scale, move } };
        content.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 350));
        move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(30, 0, Feel.Sheet));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Feel.FromTo(0.95, 1, Feel.Sheet));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Feel.FromTo(0.95, 1, Feel.Sheet));
    }

    public bool OverlayOpen => _overlay.Visibility == Visibility.Visible;

    public void CloseOverlay(bool immediate = false)
    {
        if (_overlay.Visibility != Visibility.Visible) return;
        _ask?.Cancel();
        _ask = null;
        _sheet = null;
        if (immediate)
        {
            _overlay.Children.Clear();
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = Feel.To(0, Motion.Exit + 60, Motion.Out);
        fade.Completed += (_, _) =>
        {
            if (_sheet is not null || _ask is not null) return; // a new overlay opened meanwhile
            _overlay.BeginAnimation(OpacityProperty, null);
            _overlay.Opacity = 1;
            _overlay.Children.Clear();
            _overlay.Visibility = Visibility.Collapsed;
        };
        _overlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The top-center glass pill with a popping green check.
    /// With an action ("בטל"), it stays long enough to use it.</summary>
    public void Toast(string message, string? actionLabel = null, Action? action = null,
        string? secondLabel = null, Action? second = null)
    {
        _toastTimer.Stop();
        _toastLayer.Children.Clear();
        var check = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Tone.Green,
            Child = Kit.Icon(Icons.Check, 13, Tone.OnPrimary, 3),
        };
        var text = Kit.T(message, 14, Tone.Text, FontWeights.Medium);
        text.Margin = new Thickness(10, 0, 0, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        text.MaxWidth = 560;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(check);
        row.Children.Add(Kit.Auto(text));
        _toastAction = action;
        if (actionLabel is not null && action is not null)
        {
            var undo = Kit.Pill(actionLabel, PillKind.Ghost, () =>
            {
                var a = _toastAction;
                _toastAction = null;
                HideToast();
                a?.Invoke();
            }, height: 30, fontSize: 13.5);
            undo.Margin = new Thickness(12, 0, -8, 0);
            row.Children.Add(undo);
        }
        if (secondLabel is not null && second is not null)
        {
            var extra = Kit.Pill(secondLabel, PillKind.Ghost, () =>
            {
                _toastAction = null;
                HideToast();
                second();
            }, height: 30, fontSize: 13.5);
            extra.Margin = new Thickness(12, 0, -8, 0);
            row.Children.Add(extra);
        }
        var pill = new Border
        {
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Padding = new Thickness(16, 0, 20, 0),
            Background = Tone.B("#EB1E1E22"),
            BorderBrush = Tone.B("#1FFFFFFF"),
            BorderThickness = new Thickness(1),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 50, ShadowDepth = 16, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        row.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(pill, System.Windows.Automation.AutomationLiveSetting.Polite);
        _toastLayer.Children.Add(pill);
        if (Fx.Allowed(this))
        {
            var move = new TranslateTransform(0, -16);
            pill.RenderTransform = move;
            pill.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 300));
            move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(-16, 0, Feel.Sheet));
            Kit.Pop(check);
        }
        _toastTimer.Interval = TimeSpan.FromMilliseconds(action is null ? Feel.Toast : 4500);
        _toastTimer.Start();
    }

    void HideToast()
    {
        _toastTimer.Stop();
        if (_toastLayer.Children.Count == 0) return;
        var pill = (FrameworkElement)_toastLayer.Children[0];
        var fade = Feel.To(0, 180, Motion.Out);
        fade.Completed += (_, _) =>
        {
            if (_toastLayer.Children.Count > 0 && ReferenceEquals(_toastLayer.Children[0], pill)) _toastLayer.Children.Clear();
        };
        pill.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>An agentic command that can be reversed (CommandRouter.UndoOffered):
    /// the toast with "בטל"; the undo's own line confirms once reversed.</summary>
    public void OfferUndo(string label, Func<Task<string>> undo) => Toast(label, "בטל", async () =>
    {
        try
        {
            Toast(await undo());
        }
        catch (Exception ex)
        {
            Log.Write($"Undo failed: {ex.Message}");
            Toast("הביטול לא הצליח — ראה לוג");
        }
    });

    /// <summary>True while the Terminal is the window the user is looking at —
    /// memory toasts then show here (with Undo/Edit) instead of on the dock.</summary>
    internal static bool IsForeground => _instance is { IsActive: true };

    void OnRemembered(Memory.ProfileItem item, string changeId) => Dispatcher.InvokeAsync(() =>
    {
        if (!IsActive) return;
        Toast($"נשמר בזיכרון: {ClipText(item.Text)}", "בטל", () => Memory.MemoryStore.Undo(changeId), "ערוך", () =>
        {
            Navigate(TerminalPage.Memory);
            (_screens[TerminalPage.Memory] as MemoryScreen)?.EditById(item.Id);
        });
    });

    void OnForgotten(Memory.ProfileItem item, string changeId) => Dispatcher.InvokeAsync(() =>
    {
        if (IsActive) Toast($"נמחק מהזיכרון: {ClipText(item.Text)}", "בטל", () => Memory.MemoryStore.Undo(changeId));
    });

    static string ClipText(string s) => s.Length > 48 ? s[..48].TrimEnd() + "…" : s;

    public void CopyWithToast(string text, string message)
    {
        Kit.Copy(text);
        Toast(message);
    }

    /// <summary>Palon's little hop on Now.</summary>
    public void Cheer() => (_now as NowScreen)?.Cheer();

    // ---- keyboard ----------------------------------------------------------------

    void OnKey(object sender, KeyEventArgs e)
    {
        var bar = (_now as NowScreen)?.Bar;
        if (e.Key == Key.Escape)
        {
            if (OverlayOpen) CloseOverlay();
            else if (_sidePage is not null) CloseSide();
            else return;
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.K)
            {
                if (OverlayOpen) CloseOverlay(immediate: true);
                CloseSide();
                bar?.FocusInput();
                e.Handled = true;
                return;
            }
            var page = e.Key switch
            {
                Key.D1 => TerminalPage.Today,
                Key.D2 => TerminalPage.Month,
                Key.D3 => TerminalPage.Callbacks,
                Key.D4 => TerminalPage.Clients,
                Key.D5 => TerminalPage.Templates,
                Key.D6 => TerminalPage.Coaching,
                Key.D7 => TerminalPage.Memory,
                Key.D8 => TerminalPage.Calls,
                Key.D9 => (TerminalPage?)TerminalPage.Settings,
                _ => null,
            };
            if (page is TerminalPage p && !OverlayOpen && !CurtainOpen)
            {
                Navigate(p);
                e.Handled = true;
            }
            return;
        }
        // 1–5 copy a pinned template when nothing is being typed on Now.
        if (Keyboard.Modifiers == ModifierKeys.None && !OverlayOpen && !CurtainOpen && _sidePage is null
            && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase && bar is not null && !bar.HasText
            && CommandBar.DigitOf(e.Key) is int digit
            && CommandText.PinnedIndex("", digit, TemplatesStore.Load().Count) is int index)
        {
            (_now as NowScreen)?.CopyPinned(index);
            e.Handled = true;
        }
    }

    // ---- backdrop ----------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwaSystemBackdropType = 38;

    /// <summary>
    /// Win11 22H2+: a dark acrylic backdrop behind a translucent black
    /// ground, rounded corners from DWM. Anything older (or any failure):
    /// solid black, which is also the design's ground. Never throws.
    /// </summary>
    void ApplyBackdrop()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var build = Environment.OSVersion.Version.Build;
            if (!OperatingSystem.IsWindows() || build < 22000) return;
            int on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            if (build < 22621) return;
            int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
            if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref acrylic, sizeof(int)) != 0) return;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
            Background = Brushes.Transparent;
            _glass = true;
            // Glass shows through a deep black veil — depth without losing contrast.
            var veil = new RadialGradientBrush(new GradientStopCollection
            {
                new GradientStop(Tone.C("#D0151518"), 0), new GradientStop(Tone.C("#E6000000"), 0.6),
            })
            { Center = new Point(0.5, -0.1), GradientOrigin = new Point(0.5, -0.1), RadiusX = 1.2, RadiusY = 0.9 };
            veil.Freeze();
            _ground.Background = veil;
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal backdrop unavailable: {ex.Message}");
            _glass = false;
        }
    }

    internal bool HasGlass => _glass;
}
