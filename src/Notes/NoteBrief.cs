namespace Palon.Notes;

/// <summary>
/// The one-line caller brief the dock shows when a known number calls:
/// how long ago you last spoke and — preferring the note's "next step"
/// line, that's the promise you made — what it was about.
/// </summary>
static class NoteBrief
{
    const int MaxChars = 90;

    public static string Compose(CallNote note, DateTime nowUtc)
    {
        var prefix = note.Number is { } number ? $"{number} · {Ago(note.StartedUtc, nowUtc)}: " : $"{Ago(note.StartedUtc, nowUtc)}: ";
        var room = Math.Max(20, MaxChars - prefix.Length);
        var line = KeyLine(note.Summary ?? note.Transcript);
        if (line.Length > room) line = line[..room].TrimEnd() + "…";
        return prefix + line;
    }

    /// <summary>The next-step line when the summary has one, else the first
    /// non-empty line with bullet dressing stripped.</summary>
    internal static string KeyLine(string body)
    {
        string? first = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('•', '-', '*', ' ').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Next step:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("הצעד הבא:"))
                return line;
            first ??= line;
        }
        return first ?? "";
    }

    internal static string Ago(DateTime thenUtc, DateTime nowUtc)
    {
        var days = (int)(nowUtc.Date - thenUtc.Date).TotalDays;
        return days switch
        {
            <= 0 => "earlier today",
            1 => "yesterday",
            < 30 => $"{days}d ago",
            _ => thenUtc.ToLocalTime().ToString("d MMM"),
        };
    }
}
