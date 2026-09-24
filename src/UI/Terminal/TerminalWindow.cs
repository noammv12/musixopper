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
/// The Terminal: Palon's full workspace. A borderless dark-glass window
/// (Win11 acrylic backdrop when available, solid black otherwise) with a
/// thin menu bar, five screens, a floating dock and the Ask overlay.
/// One instance at a time — open it with <see cref="ShowSingleton"/>.
/// </summary>
sealed class TerminalWindow : Window
{
    static TerminalWindow? _instance;

    /// <summary>The open Terminal, if any (for sheets opened from outside, e.g. the dock).</summary>
    internal static TerminalWindow? Instance => _instance;
    static Func<CallState> _callState = () => CallState.Idle;
    static Func<string?> _currentNumber = () => null;

    /// <summary>Set once by Shell so the menu bar and Ask know the call state.</summary>
    public static void Configure(Func<CallState> callState, Func<string?> currentNumber)
    {
        _callState = callState;
        _currentNumber = currentNumber;
    }

    /// <summary>Opens the Terminal, or brings it to the front (optionally on a page).</summary>
    public static void ShowSingleton(TerminalPage? page = null)
    {
        if (_instance is null)
        {
            _instance = new TerminalWindow();
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
        if (page is TerminalPage p) _instance.Navigate(p);
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

    internal Func<CallState> CallStateSource => _callState;
    internal Func<string?> CurrentNumberSource => _currentNumber;

    readonly Grid _root = new();
    readonly Border _ground = new();
    readonly Border _pageHost = new();
    readonly ScrollViewer _scroll = new();
    readonly Grid _overlay = new();
    readonly Grid _toastLayer = new();
    readonly Dictionary<TerminalPage, TerminalScreen> _screens = new();
    readonly Dictionary<TerminalPage, DockItem> _dockItems = new();
    readonly DispatcherTimer _clock;
    readonly DispatcherTimer _refresh;
    readonly DispatcherTimer _toastTimer;

    TerminalPage _page = TerminalPage.Today;
    TextBlock _menuTitle = null!, _menuStatus = null!, _menuProgress = null!, _menuPay = null!, _menuClock = null!;
    Border _menuDot = null!;
    Border _badge = null!;
    TextBlock _badgeText = null!;
    PalonAvatar _dockAvatar = null!;
    AskPanel? _ask;
    Border? _sheet;
    Action? _toastAction;
    bool _selfMutating;
    bool _glass;

    TerminalWindow()
    {
        Title = "Palon Terminal";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Tone.Ground;
        FontFamily = Font.Family;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 1040;
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
        Width = Math.Min(1320, work.Width * 0.92);
        Height = Math.Min(880, work.Height * 0.92);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;

        _root.FlowDirection = FlowDirection.RightToLeft;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _ground.Background = Tone.Aurora;
        Grid.SetRowSpan(_ground, 2);
        _root.Children.Add(_ground);

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        Ui.ThinScroll(_scroll);
        _pageHost.MaxWidth = 1180;
        _pageHost.Padding = new Thickness(64, 34, 64, 140);
        _scroll.Content = _pageHost;
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);

        var menu = BuildMenuBar();
        _root.Children.Add(menu);

        var dock = BuildDock();
        Grid.SetRow(dock, 1);
        _root.Children.Add(dock);

        Grid.SetRowSpan(_toastLayer, 2);
        _toastLayer.IsHitTestVisible = true;
        _toastLayer.VerticalAlignment = VerticalAlignment.Bottom;
        _toastLayer.HorizontalAlignment = HorizontalAlignment.Center;
        _toastLayer.Margin = new Thickness(0, 0, 0, 112);
        _root.Children.Add(_toastLayer);

        Grid.SetRowSpan(_overlay, 2);
        _overlay.Visibility = Visibility.Collapsed;
        _root.Children.Add(_overlay);

        Content = _root;

        _screens[TerminalPage.Today] = new TodayScreen(this);
        _screens[TerminalPage.Callbacks] = new CallbacksScreen(this);
        _screens[TerminalPage.Month] = new MonthScreen(this);
        _screens[TerminalPage.Clients] = new ClientsScreen(this);
        _screens[TerminalPage.Templates] = new TemplatesScreen(this);
        _screens[TerminalPage.Coaching] = new CoachingScreen(this);
        _screens[TerminalPage.Memory] = new MemoryScreen(this);

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
        Memory.MemoryStore.Changed += OnStoreChanged;
        Memory.MemoryStore.Remembered += OnRemembered;
        Memory.MemoryStore.Forgotten += OnForgotten;

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => ApplyBackdrop();
        Loaded += (_, _) =>
        {
            if (_pageHost.Child is null) Navigate(_page, force: true);
            _clock.Start();
        };
        StateChanged += (_, _) =>
            // Maximized borderless windows overhang the screen by the resize border.
            _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
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
        };
    }

    // ---- data refresh ------------------------------------------------------------

    void OnStoreChanged()
    {
        // Own edits already updated their rows in place (and are mid-animation):
        // just refresh the chrome. Anything else re-renders the page.
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

    /// <summary>Re-renders the current page from disk (no entrance).</summary>
    public void Refresh() => Render(entrance: false);

    void Render(bool entrance)
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
        try
        {
            Kit.Entering = entrance;
            _screens[_page].Render(data, entrance);
        }
        catch (Exception ex)
        {
            Log.Write($"Terminal render ({_page}) failed: {ex}");
        }
        finally
        {
            Kit.Entering = false;
        }
    }

    public void Navigate(TerminalPage page, bool force = false)
    {
        if (page == _page && !force && _pageHost.Child is not null) return;
        if (page != _page) CallbackRows.RecentlyDone.Clear(); // checked-off rows stay only while you're on the page
        _page = page;
        _pageHost.Child = _screens[page];
        _scroll.ScrollToTop();
        foreach (var (key, item) in _dockItems) item.SetActive(key == page);
        Render(entrance: true);
    }

    // ---- menu bar ----------------------------------------------------------------

    UIElement BuildMenuBar()
    {
        var bar = new Border
        {
            Background = Tone.B("#59000000"),
            BorderBrush = Tone.B("#0FFFFFFF"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 0, 10, 0),
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = Kit.T("Palon", 13, Tone.Text, FontWeights.SemiBold);
        brand.FlowDirection = FlowDirection.LeftToRight;
        brand.VerticalAlignment = VerticalAlignment.Center;
        brand.Cursor = System.Windows.Input.Cursors.Hand;
        brand.ToolTip = "המוח של Palon";
        Kit.Clickable(brand, () => BrainSheet.Show(this));
        g.Children.Add(brand);

        _menuTitle = Kit.T("", 13, Tone.MutedSoft);
        _menuTitle.Margin = new Thickness(18, 0, 0, 0);
        _menuTitle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_menuTitle, 1);
        g.Children.Add(_menuTitle);

        _menuDot = Kit.Dot(Tone.Green, 7);
        _menuStatus = Kit.T("", 13, Tone.MutedSoft);
        _menuStatus.Margin = new Thickness(7, 0, 0, 0);
        _menuProgress = Kit.Num("", 13, Tone.MutedSoft);
        _menuPay = Kit.Num("", 13, Tone.MutedSoft);
        _menuClock = Kit.Num("", 13, Tone.Text);
        _menuClock.FlowDirection = FlowDirection.RightToLeft;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var model = ModelPicker.Pill(this, compact: true);
        model.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(model);
        var gear = Kit.IconButton(Icons.Gear, 26, () => BrainSheet.Show(this), icon: 14);
        gear.ToolTip = "המוח של Palon";
        gear.Margin = new Thickness(0, 0, 16, 0);
        right.Children.Add(gear);
        right.Children.Add(_menuDot);
        right.Children.Add(_menuStatus);
        foreach (var t in new[] { _menuProgress, _menuPay, _menuClock })
        {
            t.Margin = new Thickness(18, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(t);
        }
        var salesforce = Kit.Pill("Salesforce", PillKind.Ghost, () => SalesforceSheets.Settings(this), height: 26, fontSize: 12);
        salesforce.Margin = new Thickness(18, 0, 0, 0);
        right.Children.Add(salesforce);
        var min = Kit.IconButton(Icons.Minimize, 28, () => WindowState = WindowState.Minimized, icon: 14);
        min.Margin = new Thickness(18, 0, 0, 0);
        var max = Kit.IconButton(Icons.Maximize, 28, ToggleMaximize, icon: 12);
        max.Margin = new Thickness(4, 0, 0, 0);
        var close = Kit.IconButton(Icons.Close, 28, Close, icon: 14);
        close.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(min);
        right.Children.Add(max);
        right.Children.Add(close);
        Grid.SetColumn(right, 3);
        g.Children.Add(right);
        bar.Child = g;

        // The bar is the drag region; double-click toggles maximize.
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, right) && !ReferenceEquals(d, right)) return;
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
        _menuTitle.Text = _screens[_page].Title;
        var state = _callState();
        (_menuDot.Background, _menuStatus.Text) = state switch
        {
            CallState.OnCall => ((Brush)Tone.Amber, "בשיחה"),
            CallState.Disabled => (Tone.Dim, "כבוי"),
            _ => (Tone.Green, "מוכן לשיחה"),
        };
        if (data.Stats is { } s)
        {
            _menuProgress.Text = s.Target is int t ? $"{s.Count}/{t}" : $"{s.Count}";
            _menuPay.Text = "₪" + He.N(s.ExpectedPayIls);
        }
        else
        {
            _menuProgress.Text = "";
            _menuPay.Text = "";
        }
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
        _menuClock.Text = He.MenuClock(DateTime.Now);
        var state = _callState();
        _menuDot.Background = state == CallState.OnCall ? Tone.Amber : state == CallState.Disabled ? Tone.Dim : Tone.Green;
    }

    // ---- dock --------------------------------------------------------------------

    sealed class DockItem
    {
        public required Border Tile;
        public required Border ActiveDot;
        public required FrameworkElement Glyph;
        public required SolidColorBrush Fill;

        public void SetActive(bool on)
        {
            Fill.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(
                on ? Tone.B("#29FFFFFF").Color : Tone.B("#0FFFFFFF").Color, TimeSpan.FromMilliseconds(300)) { EasingFunction = Feel.Expo });
            ActiveDot.BeginAnimation(OpacityProperty, Feel.To(on ? 1 : 0, 300));
            Glyph.Opacity = on ? 1 : 0.8;
        }
    }

    UIElement BuildDock()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(TerminalPage page, string tip, string icon)
        {
            var (item, el) = DockButton(tip, Kit.Icon(icon, 23, Tone.Text), () => Navigate(page));
            _dockItems[page] = item;
            if (row.Children.Count > 0) el.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(el);
            if (page == TerminalPage.Callbacks)
            {
                _badgeText = Kit.Num("", 11.5, Tone.Text, FontWeights.Bold);
                _badgeText.HorizontalAlignment = HorizontalAlignment.Center;
                _badgeText.VerticalAlignment = VerticalAlignment.Center;
                _badge = new Border
                {
                    MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 0, 5, 0),
                    Background = Tone.Red, BorderBrush = Tone.B("#FF121216"), BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -5, -5, 0), Child = _badgeText, Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                };
                item.Tile.Child = new Grid { Children = { item.Glyph, _badge } };
            }
        }
        Add(TerminalPage.Today, "היום", Icons.Today);
        Add(TerminalPage.Callbacks, "חזרות", Icons.Callbacks);
        Add(TerminalPage.Month, "החודש", Icons.Month);
        Add(TerminalPage.Clients, "לקוחות", Icons.Clients);
        Add(TerminalPage.Templates, "תבניות", Icons.Templates);
        Add(TerminalPage.Coaching, "אימון", Icons.Coach);
        Add(TerminalPage.Memory, "זיכרון", Icons.Memory);

        row.Children.Add(new Border { Width = 1, Height = 40, Margin = new Thickness(14, 0, 4, 14), Background = Tone.B("#24FFFFFF"), VerticalAlignment = VerticalAlignment.Bottom });

        _dockAvatar = new PalonAvatar { Width = 44, Height = 44, Mood = PalonMood.Idle };
        var (palon, palonEl) = DockButton("שאל את Palon · Ctrl K", _dockAvatar, OpenAsk);
        palon.ActiveDot.Visibility = Visibility.Hidden;
        palonEl.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(palonEl);

        var pill = Kit.DeepGlass(28, new Thickness(12, 10, 12, 6));
        pill.Child = row;
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.VerticalAlignment = VerticalAlignment.Bottom;
        pill.Margin = new Thickness(0, 0, 0, 22);
        return pill;
    }

    (DockItem Item, FrameworkElement Element) DockButton(string tip, FrameworkElement glyph, Action onClick)
    {
        var fill = new SolidColorBrush(Tone.B("#0FFFFFFF").Color);
        var tile = new Border
        {
            Width = 50, Height = 50, CornerRadius = new CornerRadius(16), Background = fill,
            BorderBrush = Tone.GlassRim, BorderThickness = new Thickness(1), Child = glyph,
        };
        var dot = new Border
        {
            Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Tone.Text,
            Margin = new Thickness(0, 5, 0, 0), Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var tipText = Kit.T(tip, 12, Tone.Text, FontWeights.Medium);
        var tipBox = new Border
        {
            Background = Tone.B("#E61E1E22"), BorderBrush = Tone.B("#1AFFFFFF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 5, 10, 5), Child = tipText,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-60, -40, -60, 0), Opacity = 0, IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(0, 4),
        };
        var stack = new StackPanel();
        stack.Children.Add(tile);
        stack.Children.Add(dot);
        var host = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null };
        host.Children.Add(stack);
        host.Children.Add(tipBox);
        Kit.Magnify(stack);
        Kit.Press(tile, 0.92);
        Kit.Clickable(host, onClick);
        host.MouseEnter += (_, _) =>
        {
            tipBox.BeginAnimation(OpacityProperty, Feel.To(1, 200));
            ((TranslateTransform)tipBox.RenderTransform).BeginAnimation(TranslateTransform.YProperty, Feel.To(0, 250));
        };
        host.MouseLeave += (_, _) =>
        {
            tipBox.BeginAnimation(OpacityProperty, Feel.To(0, 150));
            ((TranslateTransform)tipBox.RenderTransform).BeginAnimation(TranslateTransform.YProperty, Feel.To(4, 250));
        };
        AutomationProperties(host, tip);
        return (new DockItem { Tile = tile, ActiveDot = dot, Glyph = glyph, Fill = fill }, host);
    }

    static void AutomationProperties(UIElement el, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(el, name);

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

    /// <summary>The bottom-center glass pill with a popping green check.
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
        var move = new TranslateTransform(0, 20);
        pill.RenderTransform = move;
        pill.BeginAnimation(OpacityProperty, Feel.FromTo(0, 1, 300));
        move.BeginAnimation(TranslateTransform.YProperty, Feel.FromTo(20, 0, Feel.Sheet));
        Kit.Pop(check);
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

    /// <summary>Palon's little hop — on the dock and wherever else he's on screen.</summary>
    public void Cheer()
    {
        _dockAvatar.Cheer();
        (_screens[TerminalPage.Today] as TodayScreen)?.Cheer();
    }

    // ---- keyboard ----------------------------------------------------------------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && OverlayOpen)
        {
            CloseOverlay();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.K)
            {
                OpenAsk();
                e.Handled = true;
                return;
            }
            var page = e.Key switch
            {
                Key.D1 => TerminalPage.Today,
                Key.D2 => TerminalPage.Callbacks,
                Key.D3 => TerminalPage.Month,
                Key.D4 => TerminalPage.Clients,
                Key.D5 => (TerminalPage?)TerminalPage.Templates,
                _ => null,
            };
            if (page is TerminalPage p && !OverlayOpen)
            {
                Navigate(p);
                e.Handled = true;
            }
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
