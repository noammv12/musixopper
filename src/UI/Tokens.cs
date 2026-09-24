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

/// <summary>
/// The Terminal's black→silver palette. Pure black ground, glass that is a
/// few percent of white, one silver accent, and semantic color only where
/// it means something (done/deposits green, overdue red, calls amber, the
/// warm tier ramp). Brushes are frozen singletons — share, never mutate.
/// </summary>
static class Tone
{
    public static readonly SolidColorBrush Ground = B("#FF000000");
    public static readonly SolidColorBrush GroundSolid = B("#FF0B0B0D");
    public static readonly SolidColorBrush Text = B("#FFF5F5F7");
    public static readonly SolidColorBrush TextSoft = B("#FFD1D1D6");
    public static readonly SolidColorBrush Body = B("#FFB4B4BA");
    public static readonly SolidColorBrush Muted = B("#FF8E8E93");
    public static readonly SolidColorBrush MutedSoft = B("#FFA1A1A6");
    public static readonly SolidColorBrush Faint = B("#FF6E6E73");
    public static readonly SolidColorBrush Accent = B("#FFD8D8DD");
    public static readonly SolidColorBrush AccentSoft = B("#21D8D8DD");  // 13%
    public static readonly SolidColorBrush AccentLine = B("#47D8D8DD");  // 28%
    public static readonly SolidColorBrush Primary = B("#FFF5F5F7");
    public static readonly SolidColorBrush PrimaryHover = B("#FFFFFFFF");
    public static readonly SolidColorBrush OnPrimary = B("#FF000000");
    public static readonly SolidColorBrush Fill = B("#17FFFFFF");        // 9%  secondary button
    public static readonly SolidColorBrush FillHover = B("#29FFFFFF");   // 16%
    public static readonly SolidColorBrush FillSoft = B("#0FFFFFFF");    // 6%  tracks, chips
    public static readonly SolidColorBrush FillSelected = B("#24FFFFFF"); // 14% segmented thumb
    public static readonly SolidColorBrush RowHover = B("#0BFFFFFF");    // 4.5%
    public static readonly SolidColorBrush Track = B("#14FFFFFF");       // 8%
    public static readonly SolidColorBrush Hairline = B("#16FFFFFF");
    public static readonly SolidColorBrush GlassStroke = B("#16FFFFFF"); // 8.5%
    public static readonly SolidColorBrush GlassStrokeHover = B("#24FFFFFF");
    public static readonly SolidColorBrush Scrim = B("#99000000");
    public static readonly SolidColorBrush Green = B("#FF32D74B");
    public static readonly SolidColorBrush GreenText = B("#FF5CE07A");
    public static readonly SolidColorBrush Red = B("#FFFF453A");
    public static readonly SolidColorBrush RedText = B("#FFFF6961");
    public static readonly SolidColorBrush Amber = B("#FFFF9F0A");
    public static readonly SolidColorBrush AmberText = B("#FFFFB340");
    public static readonly SolidColorBrush Graphite = B("#FF5A5A60");
    public static readonly SolidColorBrush Dim = B("#FF48484A");

    /// <summary>Top-lit glass: 5.5% white fading to 2%.</summary>
    public static readonly LinearGradientBrush Glass = G("#0EFFFFFF", "#05FFFFFF");
    /// <summary>The machined rim: brighter on the top edge, like the design's inset highlight.</summary>
    public static readonly LinearGradientBrush GlassRim = G3("#30FFFFFF", "#16FFFFFF", "#0CFFFFFF");
    /// <summary>Sheets, the dock, the Ask panel — deeper, more opaque glass.</summary>
    public static readonly LinearGradientBrush GlassDeep = G("#F028282F", "#F5121216");
    public static readonly LinearGradientBrush GlassDeepRim = G3("#40FFFFFF", "#1FFFFFFF", "#14FFFFFF");
    /// <summary>The page ground: a faint graphite glow from the top, then black.</summary>
    public static readonly RadialGradientBrush Aurora = Radial();

    public static SolidColorBrush Tier(string tier) => tier switch
    {
        "Bronze" => TierBronze,
        "Silver" => TierSilver,
        "Gold" => TierGold,
        "VIP" => Text,
        _ => MutedSoft,
    };

    static readonly SolidColorBrush TierBronze = B("#FFE3A36F");
    static readonly SolidColorBrush TierSilver = B("#FFD6DAE0");
    static readonly SolidColorBrush TierGold = B("#FFF4CD5C");

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }

    static LinearGradientBrush G(string top, string bottom)
    {
        var b = new LinearGradientBrush(C(top), C(bottom), new Point(0, 0), new Point(0, 1));
        b.Freeze();
        return b;
    }

    static LinearGradientBrush G3(string top, string mid, string bottom)
    {
        var b = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(C(top), 0), new GradientStop(C(mid), 0.08), new GradientStop(C(bottom), 1),
        }, new Point(0, 0), new Point(0, 1));
        b.Freeze();
        return b;
    }

    static RadialGradientBrush Radial()
    {
        var b = new RadialGradientBrush(new GradientStopCollection
        {
            new GradientStop(C("#FF151518"), 0), new GradientStop(C("#FF000000"), 0.55),
        })
        {
            Center = new Point(0.5, -0.1), GradientOrigin = new Point(0.5, -0.1), RadiusX = 1.2, RadiusY = 0.9,
        };
        b.Freeze();
        return b;
    }
}

/// <summary>The Terminal's larger type roles — a workspace, not a flyout.</summary>
static class Display
{
    public const double Hero = 34;     // screen titles — SemiBold
    public const double Greeting = 30; // Today's hello — SemiBold
    public const double Sheet = 22;    // sheet titles — SemiBold
    public const double Section = 17;  // card titles — SemiBold
    public const double Body = 15;     // rows
    public const double Meta = 13;     // secondary lines
    public const double Tiny = 12.5;   // captions, pills
    public const double NumXL = 58;    // the Month ring — Light
    public const double NumL = 44;     // the Today ring — Light
    public const double NumM = 40;     // pay/deposit widgets — Light

    public const double CardRadius = 32;
    public const double ListRadius = 28;
    public const double RowRadius = 18;
    public const double Gap = 20;
}
