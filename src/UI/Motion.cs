using System.Windows.Media.Animation;

namespace Palon.UI;

/// <summary>
/// The app's motion vocabulary: a three-duration spine, three curves, and a
/// handful of named roles for the beats that fall off the spine. Everything
/// animated goes through these so the whole product moves as one thing.
/// </summary>
static class Motion
{
    public const int Fast = 120;
    public const int Base = 180;
    public const int Slow = 240;

    public const int Dip = 80;        // press-down — the only thing quicker than Fast
    public const int Exit = 90;       // fade-outs and dismissals — exits run faster than entrances
    public const int PulseFast = 700; // dictation dot breathing
    public const int PulseSlow = 1200; // status halo breathing
    public const int Revert = 1200;   // feedback timers ("Copied ✓" → back), DispatcherTimer ms
    public const int Linger = 4000;   // error/status lines hold long enough to read, then clear

    public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Overshoot = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    public static readonly IEasingFunction Sine = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut }); // breathing — organic, no hard edges

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

/// <summary>
/// The Terminal's motion: exponential ease-out everywhere (fast start,
/// long settle, no overshoot) plus one tiny spring on press release.
/// Nothing moves unless the user did something — no ambient loops.
/// </summary>
static class Feel
{
    public const double Press = 0.965;  // press scale
    public const int PressDown = 120;
    public const int PressUp = 450;
    public const int Hover = 300;       // hover fades
    public const int Lift = 450;        // card lift on hover
    public const int Entrance = 550;    // screen stagger, per element
    public const int Step = 30;         // stagger step
    public const int Sheet = 500;
    public const int Count = 1100;      // numbers/rings counting up
    public const int Toast = 2200;

    /// <summary>≈ cubic-bezier(.16,1,.3,1).</summary>
    public static readonly IEasingFunction Expo = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 });
    /// <summary>The press release: a whisper of overshoot, never more.</summary>
    public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });

    public static DoubleAnimation To(double to, int ms, IEasingFunction? ease = null, int delay = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Expo, BeginTime = TimeSpan.FromMilliseconds(delay) };

    public static DoubleAnimation FromTo(double from, double to, int ms, IEasingFunction? ease = null, int delay = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Expo, BeginTime = TimeSpan.FromMilliseconds(delay) };

    static IEasingFunction Freeze(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
