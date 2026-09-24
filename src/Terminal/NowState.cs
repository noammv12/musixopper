using System.IO;
using System.Text.Json;

namespace Palon.Terminal;

/// <summary>
/// The Now window's small memory: which stack cards were handled (so a done or
/// dismissed card never comes back), and when the rituals last ran.
/// %LOCALAPPDATA%\Palon\now.json — best effort; a bad file just means a fresh start.
/// </summary>
sealed class NowState
{
    public DateTime? LastMorningLocal { get; set; }
    public DateTime? LastRecapLocal { get; set; }
    public DateTime? LastActiveLocal { get; set; }
    public int Opens { get; set; }
    /// <summary>Card id → when it was handled (UTC); pruned after a week.</summary>
    public Dictionary<string, DateTime> Handled { get; set; } = new();

    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    /// <summary>Drops handled ids older than <see cref="Keep"/>.</summary>
    public void Prune(DateTime nowUtc)
    {
        foreach (var key in Handled.Where(kv => nowUtc - kv.Value > Keep).Select(kv => kv.Key).ToList())
            Handled.Remove(key);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon", "now.json");

    public static NowState Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<NowState>(File.ReadAllText(FilePath), Json) is { } s)
            {
                s.Handled ??= new();
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Now state unreadable, starting fresh: {ex.Message}");
        }
        return new NowState();
    }

    public void Save()
    {
        try
        {
            Prune(DateTime.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Now state save failed: {ex.Message}");
        }
    }
}
