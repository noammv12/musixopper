using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Palon.Interop;

namespace Palon;

/// <summary>
/// Renders the tray glyph at runtime — the Palon "P" drawn as a rounded
/// stem plus one right-side bowl (crisp at 16 px, no font fallback;
/// same geometry as assets/make_icon.py), white on a dark taskbar and
/// near-black on a light one, with an amber dot while on a call.
/// </summary>
static class TrayIconRenderer
{
    static readonly Dictionary<(CallState State, bool LightTaskbar, int Size), Icon> Cache = new();

    public static Icon Get(CallState state, bool lightTaskbar)
    {
        int size = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
        if (size < 16) size = 16;

        var key = (state, lightTaskbar, size);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var icon = Render(state, lightTaskbar, size);
        Cache[key] = icon;
        return icon;
    }

    static Icon Render(CallState state, bool lightTaskbar, int size)
    {
        var color = lightTaskbar
            ? Color.FromArgb(230, 28, 28, 30)
            : Color.FromArgb(242, 255, 255, 255);
        if (state == CallState.Disabled)
            color = Color.FromArgb(color.A * 45 / 100, color);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float u = size / 16f;

            using var pen = new Pen(color, 2.0f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            // P-mark: stem at x=6.6 (y 3.3–12.7) + one right bowl r=2.6
            // centered (6.6,5.9) on the 16-grid.
            g.DrawLine(pen, 6.6f * u, 3.3f * u, 6.6f * u, 12.7f * u);
            g.DrawArc(pen, (6.6f - 2.6f) * u, (5.9f - 2.6f) * u, 5.2f * u, 5.2f * u, -90f, 180f); // bowl

            if (state == CallState.OnCall)
            {
                // Amber "on a call" dot, knocked out of the glyph for a 1 px gap.
                g.CompositingMode = CompositingMode.SourceCopy;
                using var clear = new SolidBrush(Color.Transparent);
                g.FillEllipse(clear, 9.2f * u, 9.2f * u, 7.6f * u, 7.6f * u);
                g.CompositingMode = CompositingMode.SourceOver;
                using var amber = new SolidBrush(Color.FromArgb(255, 159, 10));
                g.FillEllipse(amber, 10.2f * u, 10.2f * u, 5.6f * u, 5.6f * u);
            }
        }

        // The HICON stays alive for the process lifetime (small, bounded cache).
        return Icon.FromHandle(bmp.GetHicon());
    }
}
