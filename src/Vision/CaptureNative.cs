using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Palon.Vision;

/// <summary>Win32 for the screen freeze: GDI BitBlt of the virtual screen,
/// the monitor list and the z-ordered window list (DWM frame bounds, cloaked
/// windows skipped). All coordinates physical — the process is PerMonitorV2.</summary>
static class CaptureNative
{
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const int SRCCOPY = 0x00CC0020;
    const int CAPTUREBLT = 0x40000000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int DWMWA_CLOAKED = 14;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const long WS_EX_TRANSPARENT = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
    delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);

    public static PxRect VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>The whole virtual screen as an HBITMAP (caller deletes it).</summary>
    public static IntPtr CaptureVirtualScreen(PxRect vs)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, vs.Width, vs.Height);
        var old = SelectObject(mem, bmp);
        // CAPTUREBLT includes layered (transparent / acrylic) windows.
        BitBlt(mem, 0, 0, vs.Width, vs.Height, screen, vs.X, vs.Y, SRCCOPY | CAPTUREBLT);
        SelectObject(mem, old);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return bmp;
    }

    /// <summary>Each monitor's physical bounds and effective DPI scale.</summary>
    public static List<(PxRect Bounds, double Scale)> Monitors()
    {
        var list = new List<(PxRect, double)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var r = info.rcMonitor;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            list.Add((new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), scale));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Visible, uncloaked, non-minimized top-level windows of other
    /// processes, topmost first, with their visible frame bounds.</summary>
    public static List<WindowBox> WindowsInZOrder()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<WindowBox>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == own) return true;
                var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_TRANSPARENT) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0
                    && !GetWindowRect(hwnd, out r)) return true;
                var bounds = new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                if (bounds.Width < 40 || bounds.Height < 30) return true;
                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                // Untitled tool windows are tooltips, shadows and overlays — not pickable.
                if (title.Length == 0 && (ex & WS_EX_TOOLWINDOW) != 0) return true;
                list.Add(new WindowBox(hwnd, title.ToString(), bounds));
            }
            catch (Exception ex2)
            {
                Debug.WriteLine(ex2.Message);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
