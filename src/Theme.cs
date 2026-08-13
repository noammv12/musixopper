using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Bridget;

/// <summary>
/// Bridget's signature look: one black/silver palette, always — near-black
/// graphite surfaces with a machined silver hairline, silver chrome for
/// interactive elements, and semantic status colors. All colors are
/// consumed via SetResourceReference so a future palette swap stays a
/// one-file change. The tray glyph is the only theme-adaptive part: it
/// follows the taskbar via SystemUsesLightTheme.
/// </summary>
static class Theme
{
    public static bool SystemLight { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        ReadTaskbarTheme();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var before = SystemLight;
        ReadTaskbarTheme();
        if (before == SystemLight) return;
        Changed?.Invoke();
    }

    static void ReadTaskbarTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            SystemLight = (key?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
        catch
        {
            SystemLight = false;
        }
    }

    static void Apply()
    {
        var r = Application.Current.Resources;

        SetGradient(r, "SurfaceBrush", "#FA1C1C21", "#FA0F0F12");       // graphite, lit from above
        SetGradient(r, "SurfaceStrokeBrush", "#2EFFFFFF", "#10FFFFFF"); // machined hairline
        Set(r, "TextPrimaryBrush", "#FFF2F2F5");
        Set(r, "TextSecondaryBrush", "#FF9B9BA4");
        Set(r, "DividerBrush", "#16FFFFFF");
        Set(r, "ControlFillBrush", "#14FFFFFF");
        Set(r, "ControlFillHoverBrush", "#24FFFFFF");
        Set(r, "AccentBrush", "#FFE3E3EA");   // silver: links, marks, buttons
        Set(r, "OnAccentBrush", "#FF15151A"); // near-black on silver
        Set(r, "SegThumbBrush", "#FF4A4A52");
        Set(r, "SwitchOffBrush", "#FF3A3A40");
        Set(r, "StatusGoodBrush", "#FF32D74B"); // semantic: listening / enabled
        Set(r, "AmberBrush", "#FFFF9F0A");      // semantic: on a call
    }

    static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush(Hex(hex));
        brush.Freeze();
        resources[key] = brush;
    }

    static void SetGradient(ResourceDictionary resources, string key, string topHex, string bottomHex)
    {
        var brush = new LinearGradientBrush(Hex(topHex), Hex(bottomHex), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        resources[key] = brush;
    }

    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
