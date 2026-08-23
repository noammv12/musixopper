using System.Windows;
using System.Windows.Media;

namespace Palon.UI;

/// <summary>
/// The app's geometry vocabulary: one type ramp, one spacing grid, one radius
/// scale. Everything sized or spaced goes through these (via the Ui factories
/// where possible) so the whole product measures as one thing — the same deal
/// Motion makes for time and Theme makes for color.
/// </summary>
static class Font
{
    // Five roles. Half-points below Caption and between roles are retired;
    // the ramp is the whole story.
    public const double Caption = 10.5; // section captions, tiny labels — SemiBold
    public const double Small = 11;     // descriptions, metadata, secondary lines
    public const double Body = 12;      // default row text, inputs, links
    public const double Lead = 13;      // card titles, buttons, status headline — SemiBold
    public const double Title = 15;     // panel headers — SemiBold

    public const string Stack = "Segoe UI Variable Display, Segoe UI, sans-serif";
    public const string MonoStack = "Cascadia Mono, Consolas, monospace";

    public static readonly FontFamily Family = new(Stack);
    public static readonly FontFamily Mono = new(MonoStack);
}

/// <summary>4/8 spacing grid. 1–2px optical nudges stay literal.</summary>
static class Space
{
    public const double Tight = 4;   // caption → content, lines within one group
    public const double Row = 8;     // sibling rows, card → card
    public const double Section = 12; // section → section
    public const double Block = 16;  // window padding, big seams
    public const double Edge = 20;   // shadow margins, outermost gutters
}

/// <summary>The shared paddings — a card is a card everywhere.</summary>
static class Pad
{
    public static readonly Thickness Card = new(10, 8, 10, 8);
    public static readonly Thickness CardLoose = new(12, 10, 12, 10);
    public static readonly Thickness Input = new(6, 4, 6, 4);
    // Chips get +1px bottom: single-line text in a pill sits optically high
    // without it. This is the only sanctioned asymmetric padding.
    public static readonly Thickness Chip = new(10, 4, 10, 5);
}

/// <summary>Corner radii. PillSwitch (height/2) and the dock pill (dynamic) are excluded by design.</summary>
static class Radius
{
    public const double Hairline = 3; // progress bars, scrollbar thumb
    public const double Small = 6;    // segmented thumb, fine details
    public const double Control = 8;  // buttons, inputs, command rows
    public const double Card = 10;    // cards and chips
    public const double Panel = 12;   // the flyout root, dock pill max
}
