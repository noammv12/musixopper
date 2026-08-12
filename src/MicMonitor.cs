using Microsoft.Win32;

namespace Saley;

/// <summary>
/// Detects whether any app currently has the microphone open, using the
/// per-app usage records Windows keeps for the microphone capability.
/// A record with LastUsedTimeStart set and LastUsedTimeStop == 0 means
/// that app has the mic open right now.
/// </summary>
static class MicMonitor
{
    const string ConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static bool IsMicInUse(out List<string> users)
    {
        users = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (root is null) return false;

        Scan(root, users); // packaged (Store) apps
        using var nonPackaged = root.OpenSubKey("NonPackaged");
        if (nonPackaged is not null) Scan(nonPackaged, users); // classic desktop apps

        return users.Count > 0;
    }

    static void Scan(RegistryKey key, List<string> users)
    {
        foreach (var name in key.GetSubKeyNames())
        {
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            using var sub = key.OpenSubKey(name);
            if (sub?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                sub.GetValue("LastUsedTimeStop") is long stop && stop == 0)
            {
                users.Add(name.Replace('#', '\\'));
            }
        }
    }
}
