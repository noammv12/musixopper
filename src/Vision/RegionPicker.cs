using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
using Palon.UI;

namespace Palon.Vision;

/// <summary>What the user framed: a physical virtual-screen rect, and the
/// window it came from when they clicked a window instead of dragging.</summary>
sealed record PickedRegion(PxRect Bounds, string? WindowTitle);

/// <summary>
/// The region picker: the frozen screen shown dimmed, one borderless
/// topmost window per monitor (each at its monitor's own DPI). Drag frames
/// a region — across monitors too, since all hit math runs on physical
/// cursor positions; a click picks the window under the cursor (highlighted
/// on hover); Esc or right-click cancels. Nothing leaves this class but a
/// rectangle.
/// </summary>
sealed class RegionPicker
{
    readonly BitmapSource _frozen;
    readonly PxRect _virtual;
    readonly IReadOnlyList<WindowBox> _windows;
    readonly bool _windowMode;
    readonly List<Surface> _surfaces = new();
    readonly TaskCompletionSource<PickedRegion?> _done = new();
    (int X, int Y)? _anchor;
    PxRect? _drag;
    WindowBox? _hover;

    public RegionPicker(BitmapSource frozen, PxRect virtualScreen, IReadOnlyList<WindowBox> windows, bool windowMode)
    {
        _frozen = frozen;
        _virtual = virtualScreen;
        _windows = windows;
        _windowMode = windowMode;
    }

    public Task<PickedRegion?> PickAsync(IReadOnlyList<(PxRect Bounds, double Scale)> monitors)
    {
        var primary = true;
        foreach (var (bounds, _) in monitors)
        {
            var surface = new Surface(this, bounds, primary);
            primary = false;
            _surfaces.Add(surface);
            surface.Open();
        }
        if (_surfaces.Count == 0) _done.TrySetResult(null);
        else _surfaces[0].Window.Activate();
        Update();
        return _done.Task;
    }

    static (int X, int Y) Cursor()
    {
        CaptureNative.GetCursorPos(out var p);
        return (p.X, p.Y);
    }

    void Finish(PickedRegion? result)
    {
        if (_done.Task.IsCompleted) return;
        foreach (var s in _surfaces) s.Window.Close();
        _done.TrySetResult(result);
    }

    void OnDown()
    {
        _anchor = Cursor();
        _drag = null;
    }

    void OnMove()
    {
        var (x, y) = Cursor();
        if (_anchor is { } a && !_windowMode)
        {
            var rect = RegionMath.FromPoints(a.X, a.Y, x, y);
            _drag = RegionMath.IsUsable(rect) ? rect : null;
        }
        _hover = _drag is null ? RegionMath.HitTest(_windows, x, y) : null;
        Update();
    }

    void OnUp()
    {
        if (_anchor is null) return;
        _anchor = null;
        if (_drag is { } drag)
        {
            Finish(new PickedRegion(drag.Intersect(_virtual), null));
            return;
        }
        var (x, y) = Cursor();
        if (RegionMath.HitTest(_windows, x, y) is { } window)
        {
            var clipped = window.Bounds.Intersect(_virtual);
            if (RegionMath.IsUsable(clipped)) Finish(new PickedRegion(clipped, window.Title));
        }
    }

    void Update()
    {
        var focus = _drag ?? _hover?.Bounds;
        foreach (var s in _surfaces) s.Show(focus, _drag is not null);
    }

    /// <summary>One monitor's overlay window.</summary>
    sealed class Surface
    {
        readonly RegionPicker _owner;
        readonly PxRect _monitor;
        readonly Path _dim;
        readonly Border _frame;
        readonly Border _sizeTag;
        readonly TextBlock _sizeText;
        readonly Canvas _canvas = new();
        public readonly Window Window;

        public Surface(RegionPicker owner, PxRect monitor, bool primary)
        {
            _owner = owner;
            _monitor = monitor;
            var crop = RegionMath.ToBitmap(monitor, owner._virtual.X, owner._virtual.Y, owner._frozen.PixelWidth, owner._frozen.PixelHeight);
            var image = new Image
            {
                Source = crop.IsEmpty ? null : new CroppedBitmap(owner._frozen, new Int32Rect(crop.X, crop.Y, crop.Width, crop.Height)),
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false };
            _frame = new Border
            {
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _sizeText = new TextBlock { FontSize = 12, Foreground = Brushes.White, FontFamily = Font.Family };
            _sizeTag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x12, 0x12, 0x16)), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 3, 8, 3), Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            };
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_sizeTag);

            var root = new Grid { Background = Brushes.Black, Cursor = Cursors.Cross };
            root.Children.Add(image);
            root.Children.Add(_dim);
            root.Children.Add(_canvas);
            if (primary) root.Children.Add(Hint(owner._windowMode));

            Window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                ShowActivated = true, Content = root, Background = Brushes.Black, Title = "Palon — בחירת אזור",
                FlowDirection = FlowDirection.LeftToRight,
            };
            Window.MouseLeftButtonDown += (_, e) => { Window.CaptureMouse(); _owner.OnDown(); e.Handled = true; };
            Window.MouseMove += (_, _) => _owner.OnMove();
            Window.MouseLeftButtonUp += (_, e) => { Window.ReleaseMouseCapture(); _owner.OnUp(); e.Handled = true; };
            Window.MouseRightButtonUp += (_, _) => _owner.Finish(null);
            Window.KeyDown += (_, e) => { if (e.Key == Key.Escape) _owner.Finish(null); };
            Window.Deactivated += (_, _) =>
            {
                // Another app stole focus (not one of our own surfaces): cancel, never linger.
                Window.Dispatcher.BeginInvoke(() =>
                {
                    if (!_owner._surfaces.Any(s => s.Window.IsActive)) _owner.Finish(null);
                }, System.Windows.Threading.DispatcherPriority.Background);
            };
            Window.SizeChanged += (_, _) => _owner.Update();
        }

        static Border Hint(bool windowMode)
        {
            var text = new TextBlock
            {
                Text = windowMode
                    ? "לחץ על החלון לקריאה  ·  Esc לביטול"
                    : "גרור לסימון אזור  ·  לחיצה בוחרת חלון  ·  Esc לביטול",
                FontSize = 14, Foreground = Brushes.White, FontFamily = Font.Family, FlowDirection = FlowDirection.RightToLeft,
            };
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x1A)), CornerRadius = new CornerRadius(18),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 9, 18, 9), Child = text, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 28, 0, 0),
            };
        }

        public void Open()
        {
            var hwnd = new WindowInteropHelper(Window).EnsureHandle();
            const uint flags = 0x0004 | 0x0010; // NOZORDER | NOACTIVATE
            // Move first so the window adopts this monitor's DPI, then size it
            // in physical pixels again after Show (WPF re-applies its own size).
            CaptureNative.SetWindowPos(hwnd, IntPtr.Zero, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, flags);
            Window.Show();
            CaptureNative.SetWindowPos(hwnd, IntPtr.Zero, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, flags);
        }

        /// <summary>Physical px per DIP on this surface — measured, so it is right even mid-DPI-change.</summary>
        double Scale => Window.ActualWidth > 0 ? _monitor.Width / Window.ActualWidth : 1;

        public void Show(PxRect? focus, bool dragging)
        {
            var scale = Scale;
            var w = Window.ActualWidth;
            var h = Window.ActualHeight;
            var full = new RectangleGeometry(new Rect(0, 0, w, h));
            var local = focus is { } f ? f.Intersect(_monitor) : default;
            if (local.IsEmpty)
            {
                _dim.Data = full;
                _frame.Visibility = Visibility.Collapsed;
                _sizeTag.Visibility = Visibility.Collapsed;
                return;
            }
            var (x, y) = RegionMath.PhysicalToDip(local.X, local.Y, scale, _monitor.X, _monitor.Y);
            var hole = new Rect(x, y, local.Width / scale, local.Height / scale);
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));
            _frame.Width = hole.Width;
            _frame.Height = hole.Height;
            _frame.BorderBrush = dragging ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            Canvas.SetLeft(_frame, hole.X);
            Canvas.SetTop(_frame, hole.Y);
            _frame.Visibility = Visibility.Visible;
            if (focus is { } whole && dragging)
            {
                _sizeText.Text = $"{whole.Width} × {whole.Height}";
                Canvas.SetLeft(_sizeTag, hole.X);
                Canvas.SetTop(_sizeTag, hole.Y > 30 ? hole.Y - 28 : hole.Bottom + 6);
                _sizeTag.Visibility = Visibility.Visible;
            }
            else
            {
                _sizeTag.Visibility = Visibility.Collapsed;
            }
        }
    }
}
