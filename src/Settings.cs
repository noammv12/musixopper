using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Bridget;

enum TriggerMode
{
    /// <summary>Pause whenever any app opens the microphone.</summary>
    Microphone = 0,

    /// <summary>Pause only on explicit call-answered signals from the
    /// softphone's event handlers (via "Bridget.exe pause/resume").</summary>
    SoftphoneEvents = 1,
}

static class Settings
{
    const string KeyPath = @"SOFTWARE\Bridget";
    const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Bridget";

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
    /// Dictation hotkey as "modifiers,vk" (RegisterHotKey values, decimal) or
    /// "off"; null/unset means the default combo. Parsed by Hotkey.Parse.
    /// </summary>
    public static string? DictationHotkey
    {
        get => Read("DictationHotkey");
        set
        {
            if (value is null) DeleteValue("DictationHotkey");
            else WriteValue("DictationHotkey", value);
        }
    }

    /// <summary>Ctrl+Alt+1–9 pastes snippets 1–9. Opt-in: Ctrl+Alt+digit
    /// doubles as AltGr+digit on some keyboard layouts.</summary>
    public static bool SnippetHotkeys
    {
        get => Read("SnippetHotkeys") == "1";
        set => WriteValue("SnippetHotkeys", value ? "1" : "0");
    }

    /// <summary>
    /// Ask-Bridget hotkey, same format as DictationHotkey; null/unset means
    /// the default combo (Ctrl+Alt+B).
    /// </summary>
    public static string? AssistantHotkey
    {
        get => Read("AssistantHotkey");
        set
        {
            if (value is null) DeleteValue("AssistantHotkey");
            else WriteValue("AssistantHotkey", value);
        }
    }

    /// <summary>ElevenLabs API key — activates the premium voice tier.</summary>
    public static string? ElevenLabsKey
    {
        get => GetProtectedValue("ElevenLabsKey");
        set => SetProtectedValue("ElevenLabsKey", value);
    }

    /// <summary>ElevenLabs voice id; empty means their premade "Rachel".</summary>
    public static string ElevenLabsVoiceId
    {
        get => Read("ElevenLabsVoiceId") is { Length: > 0 } id ? id : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) DeleteValue("ElevenLabsVoiceId");
            else WriteValue("ElevenLabsVoiceId", value.Trim());
        }
    }

    /// <summary>"auto" (cloud neural voice, offline fallback) or "windows"
    /// (offline voice only, nothing leaves the PC for speech).</summary>
    public static string VoicePreference
    {
        get => Read("VoicePreference") == "windows" ? "windows" : "auto";
        set => WriteValue("VoicePreference", value == "windows" ? "windows" : "auto");
    }

    /// <summary>Bridget speaks her answers out loud (never during a
    /// recorded call — the TTS would end up in the transcript).</summary>
    public static bool VoiceEnabled
    {
        get => Read("VoiceEnabled") != "0";
        set => WriteValue("VoiceEnabled", value ? "1" : "0");
    }

    /// <summary>Clean up dictated text with DeepSeek before typing it.</summary>
    public static bool DictationPolish
    {
        get => Read("DictationPolish") == "1";
        set => WriteValue("DictationPolish", value ? "1" : "0");
    }

    /// <summary>Polish variant: smooth phrasing into a professional tone.</summary>
    public static bool DictationProfessional
    {
        get => Read("DictationProfessional") == "1";
        set => WriteValue("DictationProfessional", value ? "1" : "0");
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

    /// <summary>Gemini API key — the free-tier AI provider, tried first.</summary>
    public static string? GeminiKey
    {
        get => GetProtectedValue("GeminiKey");
        set => SetProtectedValue("GeminiKey", value);
    }

    /// <summary>Gemini model id; blank falls back to the -latest flash alias
    /// so Google's model renames don't need an app update.</summary>
    public static string GeminiModel
    {
        get => Read("GeminiModel") is { Length: > 0 } model ? model : "gemini-flash-latest";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) DeleteValue("GeminiModel");
            else WriteValue("GeminiModel", value.Trim());
        }
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

    /// <summary>Set when this run inherited settings from a Saley install.</summary>
    public static bool JustMigrated { get; private set; }

    /// <summary>
    /// One-time takeover of the app's previous identity: every registry
    /// value copies raw (DPAPI blobs are bound to the Windows user, not the
    /// app, so they still decrypt), the autostart entry is re-pointed, and
    /// the whole %LOCALAPPDATA% tree — notes, snippets, reminders, stats,
    /// and the 466 MB voice model — moves across. Each step is independent:
    /// one failing must not take the others down.
    /// </summary>
    public static void MigrateFromSaley()
    {
        try
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (existing is null)
                {
                    // Two identities back: a straight Musixopper→Bridget jump
                    // (never installed Saley) still deserves its settings.
                    using var old = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Saley")
                        ?? Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Musixopper");
                    if (old is null) return; // fresh install — nothing else to migrate either
                    using var dest = Registry.CurrentUser.CreateSubKey(KeyPath);
                    foreach (var name in old.GetValueNames())
                        dest.SetValue(name, old.GetValue(name)!, old.GetValueKind(name));
                    JustMigrated = true; // old settings key deliberately left in place
                    Log.Write("Migrated settings from the previous install");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Settings migration failed: {ex.Message}");
        }

        try
        {
            // Drop the retired exes' Run entries so they don't keep launching
            // at boot and fighting this app; re-point autostart at us.
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var hadOld = false;
            foreach (var retired in new[] { "Saley", "Musixopper" })
            {
                if (run?.GetValue(retired) is null) continue;
                run.DeleteValue(retired, throwOnMissingValue: false);
                hadOld = true;
            }
            if (hadOld && Environment.ProcessPath is { } path)
                run!.SetValue(RunValueName, $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Write($"Autostart migration failed: {ex.Message}");
        }

        try
        {
            // Log.Init() has already created (and written into) the Bridget
            // folder by the time this runs, so a whole-folder Directory.Move
            // would never fire — move the old tree's contents item by item
            // instead. Idempotent: existing entries win, leftovers retry on
            // the next launch until the old folder is deleted.
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldDir = System.IO.Path.Combine(local, "Saley");
            var newDir = System.IO.Path.Combine(local, "Bridget");
            if (System.IO.Directory.Exists(oldDir))
            {
                System.IO.Directory.CreateDirectory(newDir);
                var moved = 0;
                foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(oldDir))
                {
                    var dest = System.IO.Path.Combine(newDir, System.IO.Path.GetFileName(entry));
                    if (System.IO.File.Exists(dest) || System.IO.Directory.Exists(dest)) continue;
                    System.IO.Directory.Move(entry, dest); // moves files too
                    moved++;
                }
                if (moved > 0) Log.Write($"Moved {moved} data item(s) from Saley to Bridget");
            }
        }
        catch (Exception ex)
        {
            // Locked (old app still running?) or partial — whatever moved is
            // in place; the rest stays in the old folder for the next launch.
            Log.Write($"Data folder migration failed: {ex.Message}");
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
