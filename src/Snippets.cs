using System.IO;
using System.Text.Json;

namespace Palon;

sealed record Snippet(string Label, string Text);

/// <summary>
/// The user's snippet library: a small ordered list stored at
/// %LOCALAPPDATA%\Palon\snippets.json. Saves are user-initiated only and
/// written atomically; a corrupt file is never overwritten with defaults.
/// </summary>
static class SnippetStore
{
    public const int MaxSnippets = 15;
    public const int MaxLabelLength = 24;
    public const int MaxTextLength = 4000;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");
    static string FilePath => Path.Combine(Dir, "snippets.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<Entry> Snippets { get; set; } = new();
    }

    sealed class Entry
    {
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    static List<Snippet> Defaults() => new()
    {
        new Snippet("Calendar", "Here's my calendar — grab any time that works: https://calendly.com/you"),
        new Snippet("Follow-up", "Great speaking with you! As promised, here's a quick summary and next steps: "),
    };

    public static List<Snippet> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // Seed quietly — no Changed event, or a Load called from a
                // chip refresh would re-enter that same refresh (and a
                // persistent write failure would recurse Load<->Save).
                var seed = Defaults();
                TryWrite(seed);
                return seed;
            }
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Snippets is not { } entries) throw new JsonException("no snippets array");
            return Clamp(entries.Select(e => new Snippet(e.Label, e.Text)));
        }
        catch (Exception ex)
        {
            // Unreadable file: run with defaults in memory, never clobber it.
            Log.Write($"Snippets load failed: {ex.Message}");
            return Defaults();
        }
    }

    /// <summary>Persists the list; Changed fires only when the write succeeded.</summary>
    public static bool Save(IReadOnlyList<Snippet> snippets)
    {
        if (!TryWrite(Clamp(snippets))) return false;
        Changed?.Invoke();
        return true;
    }

    static bool TryWrite(IReadOnlyList<Snippet> snippets)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var envelope = new Envelope
            {
                Snippets = snippets.Select(s => new Entry { Label = s.Label, Text = s.Text }).ToList(),
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Snippets save failed: {ex.Message}");
            return false;
        }
    }

    static List<Snippet> Clamp(IEnumerable<Snippet> snippets) => snippets
        .Select(s => new Snippet(
            (s.Label ?? "").Trim() is { Length: > MaxLabelLength } l ? l[..MaxLabelLength] : (s.Label ?? "").Trim(),
            (s.Text ?? "") is { Length: > MaxTextLength } t ? t[..MaxTextLength] : s.Text ?? ""))
        .Where(s => s.Label.Length > 0 || s.Text.Length > 0)
        .Take(MaxSnippets)
        .ToList();
}
