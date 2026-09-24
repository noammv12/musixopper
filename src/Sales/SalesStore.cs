using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palon.Sales;

/// <summary>
/// Monthly deals: %LOCALAPPDATA%\Palon\sales.json holding every month book
/// plus the bonus rules. Same atomic write pattern as the other stores; an
/// unreadable file is never overwritten (saves refuse until it's fixed).
/// </summary>
static class SalesStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");

    /// <summary>Test hook: redirects the store to another file.</summary>
    internal static string? PathOverride { get; set; }
    static string FilePath => PathOverride ?? Path.Combine(Dir, "sales.json");

    public static event Action? Changed;

    internal sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public BonusRules? Rules { get; set; }
        public List<MonthBook> Months { get; set; } = new();
    }

    /// <summary>Reads the file; null means unreadable (distinct from missing = empty).</summary>
    static Envelope? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Envelope();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Months is null) throw new JsonException("no months array");
            envelope.Months.RemoveAll(m => m is null || m.Month is < 1 or > 12);
            foreach (var m in envelope.Months) m.Deals ??= new();
            return envelope;
        }
        catch (Exception ex)
        {
            Log.Write($"Sales load failed: {ex.Message}");
            return null;
        }
    }

    static bool Write(Envelope envelope)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            envelope.Months = envelope.Months.OrderBy(m => m.Year).ThenBy(m => m.Month).ToList();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Sales save failed: {ex.Message}");
            return false;
        }
    }

    public static BonusRules LoadRules() => Read()?.Rules ?? BonusRules.Default();

    public static bool SaveRules(BonusRules rules) => Mutate(e => e.Rules = rules);

    /// <summary>All saved months, newest first.</summary>
    public static List<MonthBook> ListMonths() =>
        (Read()?.Months ?? new()).OrderByDescending(m => m.Year).ThenByDescending(m => m.Month).ToList();

    public static MonthBook? Get(int year, int month) =>
        Read()?.Months.FirstOrDefault(m => m.Year == year && m.Month == month);

    /// <summary>Returns the month's book, creating and saving an empty one if missing.</summary>
    public static MonthBook Open(int year, int month)
    {
        if (Get(year, month) is { } existing) return existing;
        var book = new MonthBook { Year = year, Month = month };
        SaveMonth(book);
        return book;
    }

    /// <summary>Inserts or replaces the month book; Changed fires only on a successful write.</summary>
    public static bool SaveMonth(MonthBook book) => Mutate(e =>
    {
        e.Months.RemoveAll(m => m.Year == book.Year && m.Month == book.Month);
        e.Months.Add(book);
    });

    static bool Mutate(Action<Envelope> change)
    {
        var envelope = Read();
        if (envelope is null) return false; // never clobber an unreadable file
        change(envelope);
        if (!Write(envelope)) return false;
        Changed?.Invoke();
        return true;
    }
}
