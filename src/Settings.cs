using System.Globalization;
using Microsoft.Win32;

namespace Saley;

enum TriggerMode
{
    /// <summary>Pause whenever any app opens the microphone.</summary>
    Microphone = 0,

    /// <summary>Pause only on explicit call-answered signals from the
    /// softphone's event handlers (via "Saley.exe pause/resume").</summary>
    SoftphoneEvents = 1,
}

static class Settings
{
    const string KeyPath = @"SOFTWARE\Saley";
    const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Saley";

    public static TriggerMode Trigger
    {
        get => Read("TriggerMode") == "SoftphoneEvents" ? TriggerMode.SoftphoneEvents : TriggerMode.Microphone;
        set => WriteValue("TriggerMode", value.ToString());
    }

    public static bool Enabled
    {
        get => Read("Enabled") != "0";
        set => WriteValue("Enabled", value ? "1" : "0");
    }

    public static bool OnboardingDone
    {
        get => Read("OnboardingDone") == "1";
        set => WriteValue("OnboardingDone", value ? "1" : "0");
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (!value)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            else if (Environment.ProcessPath is { } path)
            {
                // No path (unusual host) -> leave any existing entry alone
                // rather than silently deleting what the user asked to enable.
                key.SetValue(RunValueName, $"\"{path}\"");
            }
        }
    }

    /// <summary>Dock pill center, as a fraction of the work-area width.</summary>
    public static double DockX
    {
        get => double.TryParse(Read("DockX"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0.0, 1.0)
            : 0.5;
        set => WriteValue("DockX", value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>Set when this run inherited settings from a Musixopper install.</summary>
    public static bool JustMigrated { get; private set; }

    /// <summary>One-time copy of settings from the app's previous identity.</summary>
    public static void MigrateFromMusixopper()
    {
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath))
                if (existing is not null) return; // Saley settings already exist

            using var old = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Musixopper");
            if (old is null) return; // fresh install

            using var dest = Registry.CurrentUser.CreateSubKey(KeyPath);
            foreach (var name in new[] { "TriggerMode", "Enabled", "OnboardingDone" })
                if (old.GetValue(name)?.ToString() is { } value)
                    dest.SetValue(name, value);

            // Carry over autostart, and drop the retired exe's Run entry so
            // it doesn't keep launching at boot and fighting this app.
            using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (run?.GetValue("Musixopper") is not null)
                {
                    run.DeleteValue("Musixopper", throwOnMissingValue: false);
                    if (Environment.ProcessPath is { } path) run.SetValue(RunValueName, $"\"{path}\"");
                }
            }

            JustMigrated = true; // old settings key deliberately left in place
            Log.Write("Migrated settings from Musixopper");
        }
        catch (Exception ex)
        {
            Log.Write($"Settings migration failed: {ex.Message}");
        }
    }

    static string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name)?.ToString();
    }

    static void WriteValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value);
    }
}
