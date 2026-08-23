namespace Palon.Agent;

/// <summary>
/// Loose phone-number equality: strip everything but digits and compare
/// suffixes, so "+972 50-123-4567", "0501234567" and "501234567" all match.
/// </summary>
static class PhoneMatch
{
    const int MinMeaningfulDigits = 7;

    public static bool Same(string? a, string? b)
    {
        var da = Digits(a);
        var db = Digits(b);
        if (da.Length < MinMeaningfulDigits || db.Length < MinMeaningfulDigits) return false;
        return da.Length >= db.Length ? da.EndsWith(db) : db.EndsWith(da);
    }

    public static string Digits(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        Span<char> buffer = stackalloc char[raw.Length];
        var n = 0;
        foreach (var c in raw)
            if (char.IsAsciiDigit(c))
                buffer[n++] = c;
        return new string(buffer[..n]);
    }
}
