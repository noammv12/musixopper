using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace Musixopper;

/// <summary>
/// Light/dark palette management. Windows keep up automatically because
/// every color is consumed via SetResourceReference (DynamicResource).
/// AppsUseLightTheme drives the windows; SystemUsesLightTheme drives the
/// tray glyph (the taskbar theme is independent of the app theme).
/// </summary>
static class Theme
{
    public static bool AppsLight { get; private set; } = true;
    public static bool SystemLight { get; private set; }

    public static event Action? Changed;

    public static void Initialize()
    {
        ReadValues();
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var before = (AppsLight, SystemLight);
        ReadValues();
        if (before == (AppsLight, SystemLight)) return;
        Apply();
        Changed?.Invoke();
    }

    static void ReadValues()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            AppsLight = (key?.GetValue("AppsUseLightTheme") as int?) != 0;
            SystemLight = (key?.GetValue("SystemUsesLightTheme") as int?) == 1;
        }
        catch
        {
            AppsLight = true;
            SystemLight = false;
        }
    }

    static void Apply()
    {
        var r = Application.Current.Resources;
        if (AppsLight)
        {
            Set(r, "SurfaceBrush", "#FCFFFFFF");
            Set(r, "TextPrimaryBrush", "#FF1D1D1F");
            Set(r, "TextSecondaryBrush", "#FF6E6E73");
            Set(r, "DividerBrush", "#12000000");
            Set(r, "ControlFillBrush", "#0C000000");
            Set(r, "AccentBrush", "#FF28A745");
            Set(r, "OnAccentBrush", "#FFFFFFFF");
            Set(r, "SegThumbBrush", "#FFFFFFFF");
            Set(r, "SwitchOffBrush", "#FFE9E9EB");
            Set(r, "AmberBrush", "#FFFF9500");
        }
        else
        {
            Set(r, "SurfaceBrush", "#F52C2C2E");
            Set(r, "TextPrimaryBrush", "#FFF5F5F7");
            Set(r, "TextSecondaryBrush", "#FF98989D");
            Set(r, "DividerBrush", "#1FFFFFFF");
            Set(r, "ControlFillBrush", "#14FFFFFF");
            Set(r, "AccentBrush", "#FF30D158");
            Set(r, "OnAccentBrush", "#FF0A2E14");
            Set(r, "SegThumbBrush", "#FF48484A");
            Set(r, "SwitchOffBrush", "#FF39393D");
            Set(r, "AmberBrush", "#FFFF9F0A");
        }
    }

    static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
