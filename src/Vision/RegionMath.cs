namespace Palon.Vision;

/// <summary>An integer rectangle in physical (device) pixels.</summary>
readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PxRect Intersect(PxRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : new PxRect(left, top, right - left, bottom - top);
    }
}

/// <summary>A visible top-level window at freeze time, in z-order (topmost first).</summary>
readonly record struct WindowBox(IntPtr Handle, string Title, PxRect Bounds);

/// <summary>
/// The pure geometry behind the region picker. Everything the capture code
/// needs is expressed in physical pixels of the virtual screen (the process
/// is Per-Monitor-V2 aware, so Win32 hands back physical coordinates);
/// each per-monitor overlay converts its own DIPs with its own DPI scale.
/// </summary>
static class RegionMath
{
    /// <summary>Selections smaller than this (either side, physical px) are treated as a click, not a drag.</summary>
    public const int MinSidePx = 8;

    /// <summary>A drag between two points (any direction) as a DIP rect.</summary>
    public static (double X, double Y, double W, double H) FromDrag(double x1, double y1, double x2, double y2) =>
        (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>A drag between two physical cursor points (inclusive of both pixels) as a physical rect.</summary>
    public static PxRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);

    /// <summary>
    /// A DIP rect inside one monitor's overlay → physical virtual-screen
    /// pixels. <paramref name="scale"/> is that monitor's DPI / 96; the
    /// origin is the monitor's top-left in physical pixels (negative for
    /// monitors left of / above the primary). Outward rounding: the crop
    /// never loses a sliver of what the user framed.
    /// </summary>
    public static PxRect DipToPhysical(double x, double y, double w, double h, double scale, int originX, int originY)
    {
        if (scale <= 0) scale = 1;
        var left = (int)Math.Floor(x * scale + 1e-9) + originX;
        var top = (int)Math.Floor(y * scale + 1e-9) + originY;
        var right = (int)Math.Ceiling((x + w) * scale - 1e-9) + originX;
        var bottom = (int)Math.Ceiling((y + h) * scale - 1e-9) + originY;
        return new PxRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>A physical point → a DIP point inside a monitor's overlay (inverse of the above).</summary>
    public static (double X, double Y) PhysicalToDip(int x, int y, double scale, int originX, int originY)
    {
        if (scale <= 0) scale = 1;
        return ((x - originX) / scale, (y - originY) / scale);
    }

    /// <summary>
    /// A physical virtual-screen rect → the matching rect inside the frozen
    /// virtual-screen bitmap (pixel 0,0 = the virtual screen's top-left),
    /// clamped to the bitmap. Empty when nothing overlaps.
    /// </summary>
    public static PxRect ToBitmap(PxRect physical, int virtualLeft, int virtualTop, int bitmapWidth, int bitmapHeight)
    {
        var shifted = new PxRect(physical.X - virtualLeft, physical.Y - virtualTop, physical.Width, physical.Height);
        return shifted.Intersect(new PxRect(0, 0, bitmapWidth, bitmapHeight));
    }

    public static bool IsUsable(PxRect rect) => rect.Width >= MinSidePx && rect.Height >= MinSidePx;

    /// <summary>The topmost window whose bounds contain the point (z-order: first wins).</summary>
    public static WindowBox? HitTest(IReadOnlyList<WindowBox> zOrder, int x, int y)
    {
        foreach (var window in zOrder)
            if (!window.Bounds.IsEmpty && window.Bounds.Contains(x, y)) return window;
        return null;
    }

    /// <summary>
    /// Upload size: long edge capped at maxEdge and total pixels at
    /// maxPixels, aspect kept, never upscaled. Receipts stay legible at a
    /// 2000 px long edge; bigger only costs tokens and latency.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxEdge = 2000, long maxPixels = 3_000_000)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        var factor = 1.0;
        var longEdge = Math.Max(width, height);
        if (longEdge > maxEdge) factor = (double)maxEdge / longEdge;
        var pixels = (double)width * height * factor * factor;
        if (pixels > maxPixels) factor *= Math.Sqrt(maxPixels / pixels);
        if (factor >= 1) return (width, height);
        return (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
    }
}
