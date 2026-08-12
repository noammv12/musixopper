using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Musixopper.Interop;
using ShapePath = System.Windows.Shapes.Path;
using WinF = System.Windows.Forms;

namespace Musixopper.UI;

/// <summary>
/// The pill HUD: a small dark capsule that fades in bottom-center when
/// music pauses or resumes, then fades away. It never takes focus and is
/// fully click-through (WS_EX_NOACTIVATE | TOOLWINDOW | TRANSPARENT).
/// Only ever Hide() this window — Close() would destroy the ex-styles.
/// </summary>
sealed class HudWindow : Window
{
    static readonly Geometry PauseGlyph = Geometry.Parse("M0,0 H3.6 V11 H0 Z M6.4,0 H10 V11 H6.4 Z");
    static readonly Geometry PlayGlyph = Geometry.Parse("M0,0 L10,5.5 L0,11 Z");

    readonly Border _pill;
    readonly ScaleTransform _scale = new(1, 1);
    readonly TextBlock _text;
    readonly ShapePath _icon;
    readonly DispatcherTimer _hideTimer;

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif");

        var white = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
        white.Freeze();

        _icon = new ShapePath
        {
            Width = 10,
            Height = 11,
            Fill = white,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text = new TextBlock
        {
            Foreground = white,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_icon);
        content.Children.Add(_text);

        var pillBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E));
        pillBackground.Freeze();
        _pill = new Border
        {
            CornerRadius = new CornerRadius(17),
            Background = pillBackground,
            Padding = new Thickness(14, 8, 16, 9),
            Margin = new Thickness(24),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Opacity = 0,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black,
            },
            Child = content,
        };
        Content = _pill;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            AnimateOut();
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    public void ShowMessage(string text, bool paused)
    {
        if (!CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ShowMessage(text, paused));
            return;
        }

        _text.Text = text;
        _icon.Data = paused ? PauseGlyph : PlayGlyph;

        if (!IsVisible) Show();
        UpdateLayout();
        Position();

        // Restart the entrance from scratch (also cancels a running fade-out).
        _pill.BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pill.Opacity = 0;
        _scale.ScaleX = _scale.ScaleY = 0.92;

        _pill.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    void AnimateOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            // A retrigger may have restarted the entrance mid-fade.
            if (_pill.Opacity < 0.05) Hide();
        };
        _pill.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    void Position()
    {
        var wa = (WinF.Screen.PrimaryScreen ?? WinF.Screen.AllScreens[0]).WorkingArea;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Rough-move onto the target monitor first so GetDpiForWindow
        // reports that monitor's DPI (PerMonitorV2), not the DPI of
        // wherever the window happens to sit.
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            wa.Left + wa.Width / 2, wa.Top + wa.Height / 2, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        // Pill bottom sits 56 DIP above the work-area bottom (24 = shadow margin).
        double left = wa.Left + (wa.Width - w) / 2;
        double top = wa.Bottom - h - (56 - 24) * scale;
        Left = left / scale;
        Top = top / scale;
    }
}
