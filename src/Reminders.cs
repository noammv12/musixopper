using System.IO;
using System.Text.Json;

namespace Palon;

enum ReminderState
{
    Pending,
    Done,
    Dismissed,
}

sealed record Reminder(string Id, string Url, string Label, DateTime DueAtUtc, ReminderState State)
{
    /// <summary>True when there's a link to open; false for text-only reminders.</summary>
    public bool HasUrl => Url.Length > 0;

    public string DisplayLabel =>
        Label.Length > 0 ? Label
        : Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host
        : Url.Length > 0 ? Url
        : "Reminder";
}

/// <summary>
/// Call-back reminders: %LOCALAPPDATA%\Palon\reminders.json, same atomic
/// store pattern as snippets. Saves are user/scheduler initiated; a corrupt
/// file is never overwritten with defaults.
/// </summary>
static class ReminderStore
{
    public const int MaxPending = 50;
    const int MaxLabelLength = 40;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");
    static string FilePath => Path.Combine(Dir, "reminders.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<Entry> Reminders { get; set; } = new();
    }

    sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Label { get; set; } = "";
        public DateTime DueAtUtc { get; set; }
        public string State { get; set; } = nameof(ReminderState.Pending);
    }

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>A reminder needs a valid link, a label, or both — an empty
    /// link is fine (text-only reminder), a malformed one never is.</summary>
    public static bool IsValidReminder(string url, string label) =>
        url.Length == 0 ? Clamp(label).Length > 0 : IsValidUrl(url);

    public static List<Reminder> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<Reminder>();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Reminders is not { } entries) throw new JsonException("no reminders array");
            return entries
                .Where(e => IsValidReminder(e.Url ?? "", e.Label ?? ""))
                .Select(e => new Reminder(
                    string.IsNullOrEmpty(e.Id) ? Guid.NewGuid().ToString("n") : e.Id,
                    e.Url,
                    Clamp(e.Label),
                    DateTime.SpecifyKind(e.DueAtUtc, DateTimeKind.Utc),
                    Enum.TryParse<ReminderState>(e.State, out var s) ? s : ReminderState.Pending))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Write($"Reminders load failed: {ex.Message}");
            return new List<Reminder>();
        }
    }

    /// <summary>Adds a pending reminder — with a link, text-only, or both;
    /// returns null on invalid input or when full.</summary>
    public static Reminder? Add(string url, string label, DateTime dueAtUtc)
    {
        url = url.Trim();
        if (!IsValidReminder(url, label)) return null;
        var all = Load();
        if (all.Count(r => r.State == ReminderState.Pending) >= MaxPending) return null;
        var reminder = new Reminder(Guid.NewGuid().ToString("n"), url, Clamp(label), dueAtUtc, ReminderState.Pending);
        all.Add(reminder);
        return Save(all) ? reminder : null;
    }

    public static void Update(Reminder reminder)
    {
        var all = Load();
        var index = all.FindIndex(r => r.Id == reminder.Id);
        if (index < 0) return;
        all[index] = reminder;
        Save(all);
    }

    public static void Remove(string id)
    {
        var all = Load();
        if (all.RemoveAll(r => r.Id == id) > 0) Save(all);
    }

    static bool Save(List<Reminder> reminders)
    {
        try
        {
            // Prune finished reminders older than a week.
            var cutoff = DateTime.UtcNow.AddDays(-7);
            reminders.RemoveAll(r => r.State != ReminderState.Pending && r.DueAtUtc < cutoff);

            Directory.CreateDirectory(Dir);
            var envelope = new Envelope
            {
                Reminders = reminders.Select(r => new Entry
                {
                    Id = r.Id,
                    Url = r.Url,
                    Label = r.Label,
                    DueAtUtc = r.DueAtUtc,
                    State = r.State.ToString(),
                }).ToList(),
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Reminders save failed: {ex.Message}");
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    static string Clamp(string? label)
    {
        var trimmed = (label ?? "").Trim();
        return trimmed.Length > MaxLabelLength ? trimmed[..MaxLabelLength] : trimmed;
    }
}
