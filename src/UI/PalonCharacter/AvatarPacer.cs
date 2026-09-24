namespace Palon.UI;

/// <summary>
/// Frame budget for one live Palon avatar (pure; unit-tested). The avatar used to redraw
/// every instance at the monitor rate forever via CompositionTarget.Rendering, which kept
/// WPF's whole render loop hot and re-rasterized any ancestor effect every frame. Now each
/// instance asks for only what its current motion needs.
/// </summary>
static class AvatarPacer
{
    /// <summary>Instances at least this big (DIPs) get 60 fps during quick motion.</summary>
    public const double LargeSize = 96;

    public const double Fps60 = 1.0 / 60, Fps30 = 1.0 / 30, Fps20 = 1.0 / 20, Fps12 = 1.0 / 12;

    /// <summary>Seconds between frames; 0 = paused (no frames at all).</summary>
    /// <param name="size">Smaller rendered side, DIPs.</param>
    /// <param name="busy">Something quick is moving: mood morph, blink, cheer hop, gaze easing, "noticed you".</param>
    /// <param name="windowActive">The host window is the foreground window.</param>
    /// <param name="windowMinimized">The host window is minimized (still "IsVisible" to WPF).</param>
    public static double FrameInterval(double size, bool busy, bool windowActive, bool windowMinimized)
    {
        if (windowMinimized || !(size > 0)) return 0;
        bool large = size >= LargeSize;
        if (busy) return large && windowActive ? Fps60 : Fps30;
        // Idle wander/breathing is slow: 30 fps reads as smooth, and less for small/background ones.
        if (!windowActive) return Fps12;
        return large ? Fps30 : Fps20;
    }

    /// <summary>Shared timer interval: the tightest budget among live instances (paused ones ignored);
    /// 0 when every instance is paused, so the clock can stop.</summary>
    public static double SharedInterval(IEnumerable<double> intervals)
    {
        double best = 0;
        foreach (var i in intervals)
            if (i > 0 && (best == 0 || i < best)) best = i;
        return best;
    }

    /// <summary>True when an instance whose last frame was at <paramref name="last"/> is due at <paramref name="now"/>.
    /// A small slack absorbs timer jitter so a 30 fps budget on a 33 ms timer never skips to 15.</summary>
    public static bool Due(double now, double last, double interval) =>
        interval > 0 && now - last >= interval * 0.8;
}
