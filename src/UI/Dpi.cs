using System.Windows;
using System.Windows.Interop;
using Saley.Interop;

namespace Saley.UI;

static class Dpi
{
    /// <summary>
    /// Rough-moves the window onto the monitor owning <paramref name="workArea"/>
    /// so GetDpiForWindow reports that monitor's DPI (PerMonitorV2), then
    /// returns the px-per-DIP scale to use for positioning math.
    /// </summary>
    public static double MoveToAndGetScale(Window window, System.Drawing.Rectangle workArea)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        return scale <= 0 ? 1 : scale;
    }
}
