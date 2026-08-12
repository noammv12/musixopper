using Microsoft.Win32;

namespace Musixopper;

enum TriggerMode
{
    /// <summary>Pause whenever any app opens the microphone.</summary>
    Microphone = 0,

    /// <summary>Pause only on explicit call-answered signals from the
    /// softphone's event handlers (via "Musixopper.exe pause/resume").</summary>
    SoftphoneEvents = 1,
}

static class Settings
{
    const string KeyPath = @"SOFTWARE\Musixopper";
    const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Musixopper";

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
            if (value && Environment.ProcessPath is { } path) key.SetValue(RunValueName, $"\"{path}\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
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
