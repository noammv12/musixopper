using System.IO;

namespace Saley;

/// <summary>
/// One-shot side channel for the caller's number. The CLI writes it just
/// before signaling CallStart (named events carry no payload); the engine
/// takes it when the call state flips. Stale or malformed content is
/// ignored — the number is decoration, never load-bearing.
/// </summary>
static class CurrentCall
{
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Saley", "current-call.txt");

    /// <summary>Sanitizes and stores the number; no-op when it isn't one.</summary>
    public static void Set(string? raw)
    {
        if (Sanitize(raw) is not { } number) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"{DateTime.UtcNow.Ticks}|{number}");
        }
        catch (Exception ex)
        {
            Log.Write($"Current-call write failed: {ex.Message}");
        }
    }

    /// <summary>Reads and clears the stored number; null when absent or stale.</summary>
    public static string? Take()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var content = File.ReadAllText(FilePath);
            File.Delete(FilePath);
            var sep = content.IndexOf('|');
            if (sep <= 0 || !long.TryParse(content[..sep], out var ticks)) return null;
            var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            if (age < TimeSpan.Zero || age > MaxAge) return null;
            return Sanitize(content[(sep + 1)..]);
        }
        catch (Exception ex)
        {
            Log.Write($"Current-call read failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Digits and phone punctuation only; null when clearly not a number —
    /// including the literal "%NUMBER%" a dialer passes through unsubstituted.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim().Trim('"').Trim();
        if (trimmed.Length is 0 or > 32) return null;
        var digits = 0;
        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c)) digits++;
            else if (c is not ('+' or '-' or ' ' or '(' or ')' or '.' or '*' or '#')) return null;
        }
        return digits >= 3 ? trimmed : null;
    }
}
