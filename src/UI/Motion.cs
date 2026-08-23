using System.Windows.Media.Animation;

namespace Palon.UI;

/// <summary>
/// The app's motion vocabulary: three durations, three curves. Everything
/// animated goes through these so the whole product moves as one thing.
/// </summary>
static class Motion
{
    public const int Fast = 120;
    public const int Base = 180;
    public const int Slow = 240;

    public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Overshoot = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });

    public static DoubleAnimation Fade(double to, int ms, IEasingFunction? ease = null) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out };

    public static DoubleAnimation FromTo(double from, double to, int ms, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out };

    static IEasingFunction Freeze(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
