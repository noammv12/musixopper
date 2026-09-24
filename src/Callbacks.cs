using System.IO;
using System.Text.Json;

namespace Palon;

enum CallbackStatus
{
    Open,
    Snoozed,   // still owed — due time was pushed at least once
    Done,
    Cancelled,
}

enum CallbackSource
{
    Manual,    // flyout form
    Voice,     // Ask Palon / create_reminder tool
    AutoCall,  // accepted from a call note's detected promise
    Migrated,  // carried over from a v1 reminders.json
}

/// <summary>
/// A call-back the user owes someone: who (name/phone, both optional), what
/// (the note — what to do or what was discussed) and when. Text-only, link
/// and phone callbacks are all fine; see <see cref="CallbackStore.IsValid"/>.
/// </summary>
sealed record Callback(
    string Id,
    DateTime DueAtUtc,
    string Note,
    CallbackStatus Status = CallbackStatus.Open,
    CallbackSource Source = CallbackSource.Manual,
    string? Name = null,
    string? Phone = null,
    string Url = "",
    DateTime CreatedUtc = default,
    DateTime? CompletedUtc = null,
    int SnoozeCount = 0,
    string? CallNoteId = null)
{
    /// <summary>Open or snoozed — still owed.</summary>
    public bool IsActive => Status is CallbackStatus.Open or CallbackStatus.Snoozed;

    /// <summary>True when there's a link to open; false for text-only callbacks.</summary>
    public bool HasUrl => Url.Length > 0;

    public bool HasPhone => !string.IsNullOrEmpty(Phone);

    /// <summary>Who to call: name, else phone, else the note/link.</summary>
    public string DisplayLabel =>
        !string.IsNullOrEmpty(Name) ? Name
        : HasPhone ? Phone!
        : Note.Length > 0 ? FirstLine(Note)
        : Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host
        : Url.Length > 0 ? Url
        : "Callback";

    /// <summary>"Name · note" for one-line surfaces (dock, lists, tools).</summary>
    public string DisplayLine
    {
        get
        {
            var who = !string.IsNullOrEmpty(Name) ? Name : HasPhone ? Phone! : null;
            var what = Note.Length > 0 ? FirstLine(Note) : null;
            return who is not null && what is not null ? $"{who} · {what}" : DisplayLabel;
        }
    }

    static string FirstLine(string text)
    {
        var nl = text.IndexOf('\n');
        return (nl < 0 ? text : text[..nl]).Trim();
    }
}

/// <summary>
/// Callbacks: %LOCALAPPDATA%\Palon\reminders.json (the v1 file name is kept
/// so older builds' backups/migration code find it), same atomic store
/// pattern as snippets. v1 reminder files are migrated on first load — a
/// copy of the original is kept as reminders.v1.bak.json. Saves are user or
/// scheduler initiated; a corrupt file is never overwritten with defaults.
/// </summary>
static class CallbackStore
{
    public const int MaxActive = 200;
    public const int CurrentVersion = 2;
    const int MaxNoteLength = 500;
    const int MaxNameLength = 60;
    static readonly TimeSpan KeepFinished = TimeSpan.FromDays(30);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");
    static string FilePath => Path.Combine(Dir, "reminders.json");
    static string BackupPath => Path.Combine(Dir, "reminders.v1.bak.json");

    public static event Action? Changed;

    // v2 on disk. The v1 fields (Label, State) are read too so one Entry type
    // parses both shapes; they're never written back.
    sealed class Envelope
    {
        public int Version { get; set; } = CurrentVersion;
        public List<Entry>? Callbacks { get; set; }
        public List<Entry>? Reminders { get; set; } // v1
    }

    sealed class Entry
    {
        public string Id { get; set; } = "";
        public DateTime DueAtUtc { get; set; }
        public string? Note { get; set; }
        public string? Name { get; set; }
        public string? Phone { get; set; }
        public string? Url { get; set; }
        public string? Status { get; set; }
        public string? Source { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public int SnoozeCount { get; set; }
        public string? CallNoteId { get; set; }
        // v1 only
        public string? Label { get; set; }
        public string? State { get; set; }
    }

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A callback needs something to act on — a note, a name, a
    /// phone or a valid link. An empty link is fine; a malformed one never is.</summary>
    public static bool IsValid(string url, string note, string? name = null, string? phone = null)
    {
        url = (url ?? "").Trim();
        if (url.Length > 0 && !IsValidUrl(url)) return false;
        return url.Length > 0 || Clamp(note, MaxNoteLength).Length > 0 ||
               Clamp(name, MaxNameLength).Length > 0 || NormalizePhone(phone) is not null;
    }

    /// <summary>Dialer-formatted but sanitized ("050-123-4567"); null when not a number.</summary>
    public static string? NormalizePhone(string? raw) => CurrentCall.Sanitize(raw);

    public static List<Callback> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Callback>();
            var json = File.ReadAllText(FilePath);
            var callbacks = Parse(json, DateTime.UtcNow, out var migrated);
            if (migrated)
            {
                // Keep the untouched v1 file, then rewrite as v2 right away so
                // ids minted during migration stay stable across loads.
                if (!File.Exists(BackupPath)) File.Copy(FilePath, BackupPath);
                Log.Write($"Callbacks: migrated {callbacks.Count} v1 reminder(s) (backup: {Path.GetFileName(BackupPath)})");
                WriteFile(callbacks);
            }
            return callbacks;
        }
        catch (Exception ex)
        {
            Log.Write($"Callbacks load failed: {ex.Message}");
            return new List<Callback>();
        }
    }

    /// <summary>Parses a v1 or v2 file. <paramref name="migrated"/> is true
    /// when the input was v1 (or ids had to be minted) and should be rewritten.
    /// Throws on a file that's not an envelope at all.</summary>
    internal static List<Callback> Parse(string json, DateTime nowUtc, out bool migrated)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonOptions)
                       ?? throw new JsonException("empty file");
        var v1 = envelope.Callbacks is null;
        var entries = envelope.Callbacks ?? envelope.Reminders ?? throw new JsonException("no callbacks array");
        migrated = v1 || envelope.Version < CurrentVersion;

        var result = new List<Callback>();
        foreach (var e in entries)
        {
            var url = (e.Url ?? "").Trim();
            var note = Clamp(v1 ? e.Label : e.Note ?? e.Label, MaxNoteLength);
            var name = NullIfEmpty(Clamp(e.Name, MaxNameLength));
            // v1 phone reminders were stored as wa.me links — surface the number.
            var phone = NormalizePhone(e.Phone) ?? (v1 ? PhoneFromWaMe(url) : null);
            if (!IsValid(url, note, name, phone)) continue;

            if (string.IsNullOrEmpty(e.Id)) migrated = true;
            var due = DateTime.SpecifyKind(e.DueAtUtc, DateTimeKind.Utc);
            var status = v1 ? FromV1State(e.State)
                : Enum.TryParse<CallbackStatus>(e.Status, out var s) ? s : CallbackStatus.Open;
            var source = v1 ? CallbackSource.Migrated
                : Enum.TryParse<CallbackSource>(e.Source, out var src) ? src : CallbackSource.Manual;
            DateTime? completed = e.CompletedUtc is { } c ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null;
            if (v1 && status is CallbackStatus.Done or CallbackStatus.Cancelled) completed ??= due;

            result.Add(new Callback(
                string.IsNullOrEmpty(e.Id) ? Guid.NewGuid().ToString("n") : e.Id,
                due,
                note,
                status,
                source,
                name,
                phone,
                url,
                e.CreatedUtc is { } created ? DateTime.SpecifyKind(created, DateTimeKind.Utc)
                    : due < nowUtc ? due : nowUtc, // v1 had no created stamp
                completed,
                Math.Max(0, e.SnoozeCount),
                NullIfEmpty(e.CallNoteId)));
        }
        return result;
    }

    internal static string Serialize(List<Callback> callbacks) =>
        JsonSerializer.Serialize(new Envelope
        {
            Callbacks = callbacks.Select(c => new Entry
            {
                Id = c.Id,
                DueAtUtc = c.DueAtUtc,
                Note = c.Note,
                Name = c.Name,
                Phone = c.Phone,
                Url = c.Url,
                Status = c.Status.ToString(),
                Source = c.Source.ToString(),
                CreatedUtc = c.CreatedUtc,
                CompletedUtc = c.CompletedUtc,
                SnoozeCount = c.SnoozeCount,
                CallNoteId = c.CallNoteId,
            }).ToList(),
        }, JsonOptions);

    static CallbackStatus FromV1State(string? state) => state switch
    {
        "Done" => CallbackStatus.Done,
        "Dismissed" => CallbackStatus.Cancelled,
        _ => CallbackStatus.Open,
    };

    static string? PhoneFromWaMe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals("wa.me", StringComparison.OrdinalIgnoreCase)
            ? NormalizePhone("+" + uri.AbsolutePath.Trim('/'))
            : null;

    /// <summary>Adds an open callback; null on invalid input or when full.</summary>
    public static Callback? Add(
        string note, DateTime dueAtUtc, string? name = null, string? phone = null, string url = "",
        CallbackSource source = CallbackSource.Manual, string? callNoteId = null)
    {
        url = (url ?? "").Trim();
        if (!IsValid(url, note, name, phone)) return null;
        var all = Load();
        if (all.Count(c => c.IsActive) >= MaxActive) return null;
        var callback = new Callback(
            Guid.NewGuid().ToString("n"),
            dueAtUtc,
            Clamp(note, MaxNoteLength),
            CallbackStatus.Open,
            source,
            NullIfEmpty(Clamp(name, MaxNameLength)),
            NormalizePhone(phone),
            url,
            DateTime.UtcNow,
            CallNoteId: callNoteId);
        all.Add(callback);
        return Save(all) ? callback : null;
    }

    public static void Update(Callback callback)
    {
        var all = Load();
        var index = all.FindIndex(c => c.Id == callback.Id);
        if (index < 0) return;
        all[index] = callback;
        Save(all);
    }

    /// <summary>Loads, applies a pure transition to one callback, saves.</summary>
    public static Callback? Mutate(string id, Func<Callback, Callback> change)
    {
        var all = Load();
        var index = all.FindIndex(c => c.Id == id);
        if (index < 0) return null;
        all[index] = change(all[index]);
        return Save(all) ? all[index] : null;
    }

    public static void Remove(string id)
    {
        var all = Load();
        if (all.RemoveAll(c => c.Id == id) > 0) Save(all);
    }

    static bool Save(List<Callback> callbacks)
    {
        try
        {
            // Prune finished callbacks after a month (history for undo/recap).
            var cutoff = DateTime.UtcNow - KeepFinished;
            callbacks.RemoveAll(c => !c.IsActive && (c.CompletedUtc ?? c.DueAtUtc) < cutoff);
            WriteFile(callbacks);
        }
        catch (Exception ex)
        {
            Log.Write($"Callbacks save failed: {ex.Message}");
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    static void WriteFile(List<Callback> callbacks)
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(callbacks));
        File.Move(tmp, FilePath, overwrite: true);
    }

    static string Clamp(string? text, int max)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
