using System.IO;
using System.Text.Json;

namespace Palon.Agentic;

/// <summary>
/// The few things the agentic layer needs from the UI, as settable hooks.
/// Shell wires them once at startup; every hook has a safe default so the
/// brain works (and tests run) without a window. Hooks may be invoked from
/// any thread — implementations marshal to their dispatcher.
/// </summary>
static class AgenticHost
{
    /// <summary>True while the user is on a call. Proactive work (nudges, AI drafts) stays silent then.</summary>
    public static Func<bool> IsOnCall { get; set; } = () => false;

    /// <summary>
    /// Opens a page of the main window. Keys: "today", "callbacks", "month", "clients",
    /// "templates", "coaching", "memory", "settings". <c>arg</c> is optional (a client name).
    /// </summary>
    public static Action<string, string?>? OpenPage { get; set; }

    /// <summary>Puts text on the clipboard; returns false when it failed.</summary>
    public static Func<string, bool> CopyText { get; set; } = DefaultCopy;

    /// <summary>Shows a longer piece of text (a draft, a brief, a client summary): title, body.</summary>
    public static Action<string, string>? ShowText { get; set; }

    /// <summary>Starts the Salesforce log-call preview for a note (the approve sheet lives in the UI).</summary>
    public static Action<Palon.Notes.CallNote, DateTime?>? LogToSalesforce { get; set; }

    static bool DefaultCopy(string text)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null) return false;
            app.Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Agentic: copy failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Small persisted state for the agentic layer (%LOCALAPPDATA%\Palon\agentic.json):
/// focus mode, the nudge ledger (dedup across restarts) and the follow-up draft cache.
/// Atomic writes; a broken file just starts fresh (nothing here is irreplaceable).
/// </summary>
sealed class AgenticState
{
    public DateTime? FocusUntilUtc { get; set; }
    /// <summary>False = the user turned proactive nudges off entirely (reminders still come).</summary>
    public bool NudgesEnabled { get; set; } = true;
    /// <summary>Nudge key → when it was raised (UTC). Trimmed to 21 days.</summary>
    public Dictionary<string, DateTime> Shown { get; set; } = new();
    /// <summary>Nudge key → when the user dismissed it.</summary>
    public Dictionary<string, DateTime> Dismissed { get; set; } = new();
    public List<CachedDraft> Drafts { get; set; } = new();

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Test hook: redirects the file.</summary>
    internal static string? PathOverride { get; set; }
    static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "agentic.json");

    static AgenticState? _cache;

    public static AgenticState Load()
    {
        lock (Gate)
        {
            if (_cache is not null && PathOverride is null) return _cache;
            AgenticState state;
            try
            {
                state = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<AgenticState>(File.ReadAllText(FilePath), Json) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Log.Write($"Agentic: state unreadable, starting fresh: {ex.Message}");
                state = new();
            }
            state.Shown ??= new();
            state.Dismissed ??= new();
            state.Drafts ??= new();
            if (PathOverride is null) _cache = state;
            return state;
        }
    }

    public static void Mutate(Action<AgenticState> change)
    {
        lock (Gate)
        {
            var state = Load();
            change(state);
            var cutoff = DateTime.UtcNow.AddDays(-21);
            foreach (var k in state.Shown.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) state.Shown.Remove(k);
            foreach (var k in state.Dismissed.Where(p => p.Value < cutoff).Select(p => p.Key).ToList()) state.Dismissed.Remove(k);
            state.Drafts.RemoveAll(d => d.CreatedUtc < DateTime.UtcNow - FollowUpDrafts.MaxAge);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Agentic: state save failed: {ex.Message}");
            }
        }
    }

    /// <summary>Test hook: forget the in-memory copy.</summary>
    internal static void ResetCache()
    {
        lock (Gate) _cache = null;
    }
}

/// <summary>"Focus" — Palon keeps quiet (only reminders the user set get through) until a time.</summary>
static class Focus
{
    public static bool IsOn(DateTime nowUtc) => AgenticState.Load().FocusUntilUtc is { } until && until > nowUtc;

    public static DateTime? Until => AgenticState.Load().FocusUntilUtc is { } u && u > DateTime.UtcNow ? u : null;

    public static void Set(TimeSpan length) =>
        AgenticState.Mutate(s => s.FocusUntilUtc = DateTime.UtcNow + (length <= TimeSpan.Zero ? TimeSpan.FromHours(1) : length));

    public static void Clear() => AgenticState.Mutate(s => s.FocusUntilUtc = null);

    public static bool NudgesEnabled
    {
        get => AgenticState.Load().NudgesEnabled;
        set => AgenticState.Mutate(s => s.NudgesEnabled = value);
    }
}
