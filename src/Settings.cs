using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Call notes are strictly opt-in.</summary>
    public static bool NotesEnabled
    {
        get => Read("NotesEnabled") == "1";
        set => WriteValue("NotesEnabled", value ? "1" : "0");
    }

    /// <summary>"he" (default) or "auto".</summary>
    public static string NotesLanguage
    {
        get => Read("NotesLanguage") == "auto" ? "auto" : "he";
        set => WriteValue("NotesLanguage", value == "auto" ? "auto" : "he");
    }

    /// <summary>
    /// DeepSeek API key, DPAPI-encrypted and bound to this Windows user —
    /// it never exists in plaintext outside this machine (the repo is public).
    /// </summary>
    public static string? DeepSeekKey
    {
        get => GetProtectedValue("DeepSeekKey");
        set => SetProtectedValue("DeepSeekKey", value);
    }

    /// <summary>Groq API key for fast cloud transcription.</summary>
    public static string? GroqKey
    {
        get => GetProtectedValue("GroqKey");
        set => SetProtectedValue("GroqKey", value);
    }

    /// <summary>DPAPI-protected secret, bound to this Windows user; unreadable values read as unset.</summary>
    static string? GetProtectedValue(string name)
    {
        try
        {
            var stored = Read(name);
            if (string.IsNullOrEmpty(stored)) return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(bytes);
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    static void SetProtectedValue(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            DeleteValue(name);
            return;
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
        WriteValue(name, Convert.ToBase64String(protectedBytes));
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

    static void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.DeleteValue(name, throwOnMissingValue: false);
    }
}
