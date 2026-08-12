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
    const double RestingOpacity = 0.6;
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

    readonly DispatcherTimer _hoverIntent;
    readonly DispatcherTimer _collapseDelay;
    readonly DispatcherTimer _toastTimer;
    readonly DispatcherTimer _fullscreenPoll;

    DockState _state = DockState.Collapsed;
    CallState _callState = CallState.Idle;
    bool _fullscreenHidden;

    // drag
    bool _dragging;
    int _dragStartCursorX;
    double _dragStartLeft;
    double _dragScale = 1;

    public event Action? OpenFlyoutRequested;

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
        _collapsedDot.SetResourceReference(Shape.FillProperty, "AccentBrush");

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
        _statusDot.SetResourceReference(Shape.FillProperty, "AccentBrush");
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

        var host = new Grid();
        host.Children.Add(_collapsedDot);
        host.Children.Add(_expandedContent);
        host.Children.Add(_toastContent);

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
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.30,
                Color = Colors.Black,
            },
            Child = host,
        };
        _pill.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        Content = _pill;

        ApplyContentVisibility();

        _hoverIntent = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverIntent.Tick += (_, _) =>
        {
            _hoverIntent.Stop();
            if (_pill.IsMouseOver) SetState(DockState.Expanded);
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
            if (_state == DockState.Toast) SetState(_pill.IsMouseOver ? DockState.Expanded : DockState.Collapsed);
        };
        _fullscreenPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _fullscreenPoll.Tick += (_, _) => UpdateFullscreenHidden();

        _pill.MouseEnter += (_, _) =>
        {
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
        _hoverIntent.Stop();
        _collapseDelay.Stop();
        _toastTimer.Stop();
        _fullscreenPoll.Stop();
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
        _callState = state;
        var dotKey = state switch
        {
            CallState.OnCall => "AmberBrush",
            CallState.Disabled => "TextSecondaryBrush",
            _ => "AccentBrush",
        };
        _collapsedDot.SetResourceReference(Shape.FillProperty, dotKey);
        _statusDot.SetResourceReference(Shape.FillProperty, dotKey);
        _statusText.Text = state switch
        {
            CallState.OnCall => "On a call — music paused",
            CallState.Disabled => "Paused",
            _ => "Listening for calls",
        };
        if (_state == DockState.Collapsed)
            _pill.BeginAnimation(OpacityProperty, Anim(RestingOpacityFor(), 120, easeOut: true));
    }

    public void ShowToast(string text, bool paused)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowToast(text, paused));
            return;
        }
        if (_fullscreenHidden || !IsVisible) return;

        _toastText.Text = text;
        _toastIcon.Data = paused ? PauseGlyph : PlayGlyph;
        if (_state == DockState.Expanded) return; // status text already tells the story

        _toastTimer.Stop();
        _toastTimer.Start();
        if (_state == DockState.Toast)
        {
            // Toast replacing a toast: resize the pill for the new text.
            AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, 150, easeOut: true);
            return;
        }
        SetState(DockState.Toast);
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

        var more = MakeChipShell("…");
        more.MouseLeftButtonUp += (_, _) => OpenFlyoutRequested?.Invoke();
        _chipsPanel.Children.Add(more);

        if (_state == DockState.Expanded) AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, 150, easeOut: true);
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
        chip.MouseEnter += (_, _) => chip.Opacity = 0.8;
        chip.MouseLeave += (_, _) => chip.Opacity = 1.0;
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
        _state = state;
        ApplyContentVisibility();

        switch (state)
        {
            case DockState.Collapsed:
                _pill.CornerRadius = new CornerRadius(5);
                AnimatePillTo(CollapsedWidth, CollapsedHeight, 200, easeOut: false);
                _pill.BeginAnimation(OpacityProperty, Anim(RestingOpacityFor(), 200, easeOut: false));
                break;
            case DockState.Expanded:
                Reposition();
                _pill.CornerRadius = new CornerRadius(20);
                AnimatePillTo(MeasureExpandedWidth(), ExpandedHeight, 200, easeOut: true);
                _pill.BeginAnimation(OpacityProperty, Anim(1.0, 200, easeOut: true));
                FadeInContent(_expandedContent);
                break;
            case DockState.Toast:
                _pill.CornerRadius = new CornerRadius(18);
                AnimatePillTo(MeasureWidth(_toastContent), ToastHeight, 200, easeOut: true);
                _pill.BeginAnimation(OpacityProperty, Anim(1.0, 200, easeOut: true));
                FadeInContent(_toastContent);
                break;
        }
    }

    void ApplyContentVisibility()
    {
        _collapsedDot.Visibility = _state == DockState.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _expandedContent.Visibility = _state == DockState.Expanded ? Visibility.Visible : Visibility.Collapsed;
        _toastContent.Visibility = _state == DockState.Toast ? Visibility.Visible : Visibility.Collapsed;
    }

    double RestingOpacityFor() => _callState == CallState.Disabled ? DisabledOpacity : RestingOpacity;

    double MeasureExpandedWidth() => MeasureWidth(_expandedContent);

    double MeasureWidth(UIElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Min(content.DesiredSize.Width + 8, WindowWidth - 40);
    }

    void AnimatePillTo(double width, double height, int ms, bool easeOut)
    {
        _pill.BeginAnimation(WidthProperty, SizeAnim(_pill.ActualWidth, width, ms, easeOut));
        _pill.BeginAnimation(HeightProperty, SizeAnim(_pill.ActualHeight, height, ms, easeOut));
    }

    static void PrepareFade(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
    }

    void FadeInContent(UIElement content)
    {
        PrepareFade(content);
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            BeginTime = TimeSpan.FromMilliseconds(60),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    static DoubleAnimation SizeAnim(double from, double to, int ms, bool easeOut) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn },
        };

    static DoubleAnimation Anim(double to, int ms, bool easeOut) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn },
        };

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
        if (!hide) Reposition();
    }
}
