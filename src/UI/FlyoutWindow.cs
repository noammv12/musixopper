using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Musixopper.Interop;
using WinF = System.Windows.Forms;

namespace Musixopper.UI;

/// <summary>
/// The single window of the app: status, trigger mode, toggles — plus the
/// two first-run onboarding panels. Opens near the tray icon, dismisses on
/// focus loss.
/// </summary>
sealed class FlyoutWindow : Window
{
    const double ShadowMargin = 20;  // room around the card for the drop shadow
    const double EdgeGap = 8;        // visual gap between the card and the taskbar

    readonly CallEngine _engine;
    readonly Border _root;
    readonly TranslateTransform _rootSlide = new();

    // main panel
    readonly StackPanel _mainPanel;
    Ellipse _statusDot = null!;
    Ellipse _statusHalo = null!;
    ScaleTransform _haloScale = null!;
    TextBlock _statusText = null!;
    Segmented _segmented = null!;
    TextBlock _caption = null!;
    TextBlock _setupLink = null!;
    PillSwitch _switchEnabled = null!;
    PillSwitch _switchStartup = null!;

    // onboarding
    readonly StackPanel _welcomePanel;
    readonly StackPanel _softphonePanel;
    Border _cardMic = null!;
    Border _cardEvents = null!;
    int _welcomeSelection;

    readonly DispatcherTimer _statusOverrideTimer;
    string? _statusOverride;
    DateTime _lastHiddenAt = DateTime.MinValue;
    bool _hiding;
    bool _pulsing;

    public event Action? QuitRequested;

    public FlyoutWindow(CallEngine engine)
    {
        _engine = engine;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 292 + ShadowMargin * 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif");

        _statusOverrideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusOverrideTimer.Tick += (_, _) =>
        {
            _statusOverrideTimer.Stop();
            _statusOverride = null;
            SyncFromEngine();
        };

        _mainPanel = BuildMainPanel();
        _welcomePanel = BuildWelcomePanel();
        _softphonePanel = BuildSoftphonePanel();
        var host = new Grid();
        host.Children.Add(_mainPanel);
        host.Children.Add(_welcomePanel);
        host.Children.Add(_softphonePanel);

        _root = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(ShadowMargin),
            RenderTransform = _rootSlide,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.30,
                Color = Colors.Black,
            },
            Child = host,
        };
        _root.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Content = _root;

        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
    }

    // ---- panels ----------------------------------------------------------

    StackPanel BuildMainPanel()
    {
        var panel = new StackPanel();

        _statusHalo = new Ellipse
        {
            Width = 8,
            Height = 8,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _haloScale = new ScaleTransform(1, 1);
        _statusHalo.RenderTransform = _haloScale;
        _statusHalo.SetResourceReference(Shape.FillProperty, "AmberBrush");

        _statusDot = new Ellipse { Width = 8, Height = 8 };
        _statusDot.SetResourceReference(Shape.FillProperty, "AccentBrush");

        var dotHost = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        dotHost.Children.Add(_statusHalo);
        dotHost.Children.Add(_statusDot);

        _statusText = Ui.Text("Listening for calls", 13.5, "TextPrimaryBrush", FontWeights.SemiBold);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusText.Margin = new Thickness(8, 0, 0, 0);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(dotHost);
        statusRow.Children.Add(_statusText);
        panel.Children.Add(statusRow);

        panel.Children.Add(Ui.Divider(12, 12));

        panel.Children.Add(Ui.Text("Detect calls by", 11, "TextSecondaryBrush"));

        _segmented = new Segmented("Microphone", "Call events", (int)_engine.Mode)
        {
            Margin = new Thickness(0, 8, 0, 0),
        };
        _segmented.SelectionChanged += index =>
        {
            _engine.SetMode((TriggerMode)index);
            UpdateModeCaption();
        };
        panel.Children.Add(_segmented);

        _caption = Ui.Text("", 11, "TextSecondaryBrush");
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.Margin = new Thickness(2, 8, 2, 0);
        panel.Children.Add(_caption);

        _setupLink = Ui.Text("Set up Softphone.Pro…", 11, "AccentBrush", FontWeights.SemiBold);
        _setupLink.Cursor = Cursors.Hand;
        _setupLink.Margin = new Thickness(2, 6, 2, 0);
        _setupLink.MouseLeftButtonUp += (_, _) => ShowPanel(_softphonePanel);
        panel.Children.Add(_setupLink);

        panel.Children.Add(Ui.Divider(12, 12));

        _switchEnabled = new PillSwitch(_engine.Enabled);
        _switchEnabled.Toggled += on => _engine.SetEnabled(on);
        panel.Children.Add(Ui.ToggleRow("Pause music during calls", _switchEnabled));

        _switchStartup = new PillSwitch(Settings.StartWithWindows);
        _switchStartup.Toggled += on => Settings.StartWithWindows = on;
        var startupRow = Ui.ToggleRow("Start with Windows", _switchStartup);
        startupRow.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(startupRow);

        panel.Children.Add(Ui.Divider(12, 10));

        var footer = new Grid();
        var appName = Ui.Text($"Musixopper {Program.Version}", 10.5, "TextSecondaryBrush");
        appName.VerticalAlignment = VerticalAlignment.Center;
        var quit = Ui.Text("Quit", 11, "TextSecondaryBrush");
        quit.Cursor = Cursors.Hand;
        quit.HorizontalAlignment = HorizontalAlignment.Right;
        quit.MouseEnter += (_, _) => quit.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        quit.MouseLeave += (_, _) => quit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        quit.MouseLeftButtonUp += (_, _) => QuitRequested?.Invoke();
        footer.Children.Add(appName);
        footer.Children.Add(quit);
        panel.Children.Add(footer);

        UpdateModeCaption();
        return panel;
    }

    StackPanel BuildWelcomePanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Welcome to Musixopper", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var subtitle = Ui.Text("Your music pauses when a call starts, and comes back when it ends.", 11.5, "TextSecondaryBrush");
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(subtitle);

        var question = Ui.Text("How should calls be detected?", 12, "TextPrimaryBrush", FontWeights.SemiBold);
        question.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(question);

        _cardMic = Ui.OptionCard("Microphone", "Pauses whenever any app uses your mic. No setup.");
        _cardMic.Margin = new Thickness(0, 10, 0, 0);
        _cardMic.MouseLeftButtonUp += (_, _) => SelectWelcomeCard(0);
        panel.Children.Add(_cardMic);

        _cardEvents = Ui.OptionCard("Softphone events", "Pauses only on answered calls. Best for outbound sales.");
        _cardEvents.Margin = new Thickness(0, 8, 0, 0);
        _cardEvents.MouseLeftButtonUp += (_, _) => SelectWelcomeCard(1);
        panel.Children.Add(_cardEvents);

        var cont = Ui.PrimaryButton("Continue");
        cont.Margin = new Thickness(0, 14, 0, 0);
        cont.MouseLeftButtonUp += (_, _) => FinishWelcome();
        panel.Children.Add(cont);

        SelectWelcomeCard((int)_engine.Mode);
        return panel;
    }

    StackPanel BuildSoftphonePanel()
    {
        var panel = new StackPanel { Visibility = Visibility.Collapsed };

        panel.Children.Add(Ui.Text("Connect Softphone.Pro", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var body = Ui.Text("In Softphone.Pro, open Settings → Integration → Third-party systems and add three handlers:", 11.5, "TextSecondaryBrush");
        body.TextWrapping = TextWrapping.Wrap;
        body.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(body);

        // Softphone.Pro launches the string as "path + arguments" without
        // shell-style quote stripping, so quote only when unavoidable.
        var exe = Environment.ProcessPath ?? "Musixopper.exe";
        var hasSpaces = exe.Contains(' ');
        if (hasSpaces) exe = $"\"{exe}\"";
        var rows = new (string Caption, string Command)[]
        {
            ("Outgoing call answer", $"{exe} pause"),
            ("Incoming call answer", $"{exe} pause"),
            ("Call end", $"{exe} resume"),
        };
        double top = 12;
        foreach (var (caption, command) in rows)
        {
            var row = Ui.CommandRow(caption, command);
            row.Margin = new Thickness(0, top, 0, 0);
            top = 8;
            panel.Children.Add(row);
        }
        if (hasSpaces)
        {
            var spaceHint = Ui.Text("If a handler doesn't fire, move Musixopper.exe to a folder without spaces (e.g. C:\\Tools) — some softphones don't handle quoted paths.", 10.5, "TextSecondaryBrush");
            spaceHint.TextWrapping = TextWrapping.Wrap;
            spaceHint.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(spaceHint);
        }

        var hint = Ui.Text("Tip: run “Musixopper test” in a terminal — your music pauses for 8 seconds, then resumes.", 11, "TextSecondaryBrush");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(hint);

        var done = Ui.PrimaryButton("Done");
        done.Margin = new Thickness(0, 14, 0, 0);
        done.MouseLeftButtonUp += (_, _) => ShowPanel(_mainPanel);
        panel.Children.Add(done);

        var note = Ui.Text("You can change this anytime from this menu.", 10.5, "TextSecondaryBrush");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(note);

        return panel;
    }

    void SelectWelcomeCard(int index)
    {
        _welcomeSelection = index;
        Ui.SetCardSelected(_cardMic, index == 0);
        Ui.SetCardSelected(_cardEvents, index == 1);
    }

    void FinishWelcome()
    {
        Settings.OnboardingDone = true;
        var mode = _welcomeSelection == 1 ? TriggerMode.SoftphoneEvents : TriggerMode.Microphone;
        _engine.SetMode(mode);
        _segmented.Select((int)mode, animate: false);
        UpdateModeCaption();
        if (mode == TriggerMode.SoftphoneEvents)
        {
            ShowPanel(_softphonePanel);
        }
        else
        {
            ShowPanel(_mainPanel);
            ShowTransientStatus("You're all set.");
        }
    }

    void UpdateModeCaption()
    {
        bool events = _engine.Mode == TriggerMode.SoftphoneEvents;
        _caption.Text = events
            ? "Pauses only when your softphone reports an answered call. Ringing never interrupts the music."
            : "Pauses whenever any app starts using your microphone. No setup needed.";
        _setupLink.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowPanel(UIElement panel)
    {
        foreach (UIElement child in ((Grid)_root.Child).Children)
            child.Visibility = child == panel ? Visibility.Visible : Visibility.Collapsed;
        if (panel.Visibility == Visibility.Visible)
        {
            panel.Opacity = 0;
            panel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        }
    }

    void ShowTransientStatus(string text)
    {
        _statusOverride = text;
        _statusText.Text = text;
        _statusOverrideTimer.Stop();
        _statusOverrideTimer.Start();
    }

    // ---- state sync ------------------------------------------------------

    public void SyncFromEngine()
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(SyncFromEngine);
            return;
        }

        var state = _engine.State;
        _statusDot.SetResourceReference(Shape.FillProperty, state switch
        {
            CallState.OnCall => "AmberBrush",
            CallState.Disabled => "TextSecondaryBrush",
            _ => "AccentBrush",
        });
        if (_statusOverride is null)
        {
            _statusText.Text = state switch
            {
                CallState.OnCall => "On a call — music paused",
                CallState.Disabled => "Paused",
                _ => "Listening for calls",
            };
        }
        _switchEnabled.Set(_engine.Enabled, animate: false);
        UpdatePulse();
    }

    void UpdatePulse()
    {
        bool shouldPulse = _engine.State == CallState.OnCall && IsVisible;
        if (shouldPulse == _pulsing) return;
        _pulsing = shouldPulse;

        if (shouldPulse)
        {
            var grow = new DoubleAnimation(1, 2.1, TimeSpan.FromSeconds(1.2))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _statusHalo.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.55, 0, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            _haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _statusHalo.BeginAnimation(OpacityProperty, null);
            _haloScale.ScaleX = _haloScale.ScaleY = 1;
            _statusHalo.Opacity = 0;
        }
    }

    // ---- show / hide -----------------------------------------------------

    public void ShowFlyout(bool onboarding = false)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowFlyout(onboarding));
            return;
        }
        if (IsVisible)
        {
            // Already open (e.g. the user clicked the tray before the
            // first-run timer fired): still surface the requested panel.
            if (onboarding) ShowPanel(_welcomePanel);
            Activate();
            return;
        }
        // Clicking the tray icon while open fires Deactivated (hide) then
        // MouseUp (show) — without this guard the flyout flickers reopen.
        // Onboarding is never swallowed by it.
        if (!onboarding && (DateTime.UtcNow - _lastHiddenAt).TotalMilliseconds < 250) return;

        ShowPanel(onboarding ? _welcomePanel : _mainPanel);
        SyncFromEngine();

        Opacity = 0;
        Show();
        Dispatcher.InvokeAsync(() =>
        {
            PositionNearTray();
            BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            _rootSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            Activate();
            UpdatePulse();
        }, DispatcherPriority.Loaded);
    }

    void HideFlyout()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            _rootSlide.BeginAnimation(TranslateTransform.YProperty, null);
            _rootSlide.Y = 0;
            _hiding = false;
            _lastHiddenAt = DateTime.UtcNow;
            UpdatePulse();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    void PositionNearTray()
    {
        var cursor = WinF.Cursor.Position;
        var screen = WinF.Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Rough-move onto the target monitor first so GetDpiForWindow
        // reports that monitor's DPI (PerMonitorV2).
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            wa.Left + wa.Width / 2, wa.Top + wa.Height / 2, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        UpdateLayout();
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double overlap = (ShadowMargin - EdgeGap) * scale; // window may overhang the work area by the shadow

        bool topBar = wa.Top > bounds.Top;
        bool leftBar = wa.Left > bounds.Left;
        bool rightBar = wa.Right < bounds.Right;

        double left = cursor.X - w / 2;
        double top;
        if (topBar) top = wa.Top - overlap;
        else if (leftBar) { left = wa.Left - overlap; top = cursor.Y - h / 2; }
        else if (rightBar) { left = wa.Right - w + overlap; top = cursor.Y - h / 2; }
        else top = wa.Bottom - h + overlap; // bottom taskbar (default)

        left = Math.Min(Math.Max(left, wa.Left - overlap), wa.Right - w + overlap);
        top = Math.Min(Math.Max(top, wa.Top - overlap), wa.Bottom - h + overlap);

        Left = left / scale;
        Top = top / scale;
    }
}
