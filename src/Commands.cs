using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Palon;

sealed record PalonCommand(string Id, string Label, string Target);

/// <summary>
/// The user's command presets ("open my Salesforce"): a small ordered list
/// at %LOCALAPPDATA%\Palon\commands.json, run by click or by asking
/// Palon. Targets are anything the Windows shell can open — URLs, apps,
/// folders, protocol links. Same atomic store pattern as snippets.
/// </summary>
static class CommandStore
{
    public const int MaxCommands = 20;
    public const int MaxLabelLength = 30;
    public const int MaxTargetLength = 500;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palon");
    static string FilePath => Path.Combine(Dir, "commands.json");

    public static event Action? Changed;

    sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public List<Entry> Commands { get; set; } = new();
    }

    sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Target { get; set; } = "";
    }

    public static List<PalonCommand> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<PalonCommand>();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(FilePath), JsonOptions);
            if (envelope?.Commands is not { } entries) throw new JsonException("no commands array");
            return Clamp(entries.Select(e => new PalonCommand(
                string.IsNullOrEmpty(e.Id) ? Guid.NewGuid().ToString("n") : e.Id,
                e.Label, e.Target)));
        }
        catch (Exception ex)
        {
            // Unreadable file: run empty in memory, never clobber it.
            Log.Write($"Commands load failed: {ex.Message}");
            return new List<PalonCommand>();
        }
    }

    /// <summary>Persists the list; Changed fires only when the write succeeded.</summary>
    public static bool Save(IReadOnlyList<PalonCommand> commands)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var envelope = new Envelope
            {
                Commands = Clamp(commands)
                    .Select(c => new Entry { Id = c.Id, Label = c.Label, Target = c.Target }).ToList(),
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Commands save failed: {ex.Message}");
            return false;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Opens the target via the shell. False (and a log line) on failure.</summary>
    public static bool Execute(PalonCommand command)
    {
        try
        {
            Process.Start(new ProcessStartInfo(command.Target) { UseShellExecute = true });
            Log.Write($"Command run: {command.Label}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Command '{command.Label}' failed: {ex.Message}");
            return false;
        }
    }

    static List<PalonCommand> Clamp(IEnumerable<PalonCommand> commands) => commands
        .Select(c => new PalonCommand(
            c.Id,
            (c.Label ?? "").Trim() is { Length: > MaxLabelLength } l ? l[..MaxLabelLength] : (c.Label ?? "").Trim(),
            (c.Target ?? "").Trim() is { Length: > MaxTargetLength } t ? t[..MaxTargetLength] : (c.Target ?? "").Trim()))
        .Where(c => c.Label.Length > 0 || c.Target.Length > 0)
        .Take(MaxCommands)
        .ToList();
}
