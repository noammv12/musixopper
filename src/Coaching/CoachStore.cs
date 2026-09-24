using System.IO;
using System.Text.Json;

namespace Palon.Coaching;

/// <summary>
/// %LOCALAPPDATA%\Palon\coaching.json — per-call coaching records (numbers,
/// verified quotes, phrase sets; never audio or transcripts) for 180 days,
/// plus the cached one-line "why this was the best call" per week.
/// </summary>
static class CoachStore
{
    const int KeepDays = 180;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    static readonly object Gate = new();

    internal static string? PathOverride { get; set; }
    static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "coaching.json");

    public static event Action? Changed;

    internal sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<CoachRecord> Calls { get; set; } = new();
        /// <summary>"yyyy-MM-dd" week start → "callId|explanation".</summary>
        public Dictionary<string, string> BestWhy { get; set; } = new();
    }

    static Envelope Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Envelope();
            var e = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions) ?? new Envelope();
            e.Calls ??= new();
            e.BestWhy ??= new();
            e.Calls.RemoveAll(c => c is null);
            return e;
        }
        catch (Exception ex)
        {
            Log.Write($"Coaching load failed: {ex.Message}");
            return new Envelope();
        }
    }

    static void Mutate(Action<Envelope> change)
    {
        lock (Gate)
        {
            try
            {
                var e = Read();
                change(e);
                var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
                e.Calls.RemoveAll(c => c.StartedUtc < cutoff);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(e, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Write($"Coaching save failed: {ex.Message}");
                return;
            }
        }
        Changed?.Invoke();
    }

    public static List<CoachRecord> Load()
    {
        lock (Gate) return Read().Calls;
    }

    public static void Add(CoachRecord record) => Mutate(e =>
    {
        e.Calls.RemoveAll(c => c.Id == record.Id);
        e.Calls.Add(record);
    });

    public static string? BestWhy(DateTime weekStart, string callId)
    {
        lock (Gate)
            return Read().BestWhy.TryGetValue(weekStart.ToString("yyyy-MM-dd"), out var v) && v.StartsWith(callId + "|")
                ? v[(callId.Length + 1)..]
                : null;
    }

    public static void SaveBestWhy(DateTime weekStart, string callId, string text) =>
        Mutate(e => e.BestWhy[weekStart.ToString("yyyy-MM-dd")] = callId + "|" + text);
}
