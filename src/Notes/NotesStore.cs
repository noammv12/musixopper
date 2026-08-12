using System.IO;
using System.Text;
using System.Text.Json;

namespace Saley.Notes;

sealed record CallNote(
    string Id,
    DateTime StartedUtc,
    int DurationSec,
    string? Summary,
    string Transcript,
    string State); // "ok" | "transcript-only" | "recovered"

/// <summary>
/// Call notes persistence: a JSON index of the last 50 notes for the UI,
/// plus an append-only daily Markdown file the user can open and search.
/// </summary>
static class NotesStore
{
    const int MaxNotes = 50;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string NotesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Saley", "notes");
    public static string TmpDir => Path.Combine(NotesDir, "tmp");
    static string IndexPath => Path.Combine(NotesDir, "notes.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<CallNote> Notes { get; set; } = new();
    }

    public static List<CallNote> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new List<CallNote>();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(IndexPath), JsonOptions);
            return envelope?.Notes ?? new List<CallNote>();
        }
        catch (Exception ex)
        {
            Log.Write($"Notes load failed: {ex.Message}");
            return new List<CallNote>();
        }
    }

    public static void Add(CallNote note)
    {
        try
        {
            Directory.CreateDirectory(NotesDir);
            var notes = Load();
            notes.Add(note);
            if (notes.Count > MaxNotes) notes.RemoveRange(0, notes.Count - MaxNotes);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Envelope { Notes = notes }, JsonOptions));
            File.Move(tmp, IndexPath, overwrite: true);
            AppendDaily(note);
        }
        catch (Exception ex)
        {
            Log.Write($"Notes save failed: {ex.Message}");
        }
        Changed?.Invoke();
    }

    static void AppendDaily(CallNote note)
    {
        var local = note.StartedUtc.ToLocalTime();
        var path = Path.Combine(NotesDir, $"{local:yyyy-MM-dd}.md");
        var sb = new StringBuilder();
        sb.AppendLine($"## {local:HH:mm} · {Math.Max(1, note.DurationSec / 60)} min" +
                      (note.State == "recovered" ? " · recovered" : ""));
        if (note.Summary is { } summary)
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }
        sb.AppendLine("Transcript:");
        sb.AppendLine(note.Transcript);
        sb.AppendLine();
        sb.AppendLine("---");
        File.AppendAllText(path, sb.ToString());
    }
}
