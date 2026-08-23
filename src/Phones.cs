namespace Palon;

/// <summary>
/// Turns dialer-formatted numbers into wa.me-linkable international digits.
/// Trunk-zero numbers get the default country code (Israel — the app's home
/// market); numbers already carrying a country code pass through.
/// </summary>
static class Phones
{
    public const string DefaultCountryCode = "972";

    /// <summary>International digits ("972501234567") or null when the raw
    /// text can't safely be made international.</summary>
    public static string? ToInternationalDigits(string? raw, string defaultCountryCode = DefaultCountryCode)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var explicitInternational = trimmed.StartsWith('+');
        var digits = Agent.PhoneMatch.Digits(trimmed);

        if (digits.StartsWith("00")) // classic international dialing prefix
        {
            digits = digits[2..];
            explicitInternational = true;
        }

        string result;
        if (explicitInternational) result = digits;
        else if (digits.StartsWith('0')) result = defaultCountryCode + digits[1..];
        else if (digits.Length >= 11) result = digits;                       // already has a country code
        else result = defaultCountryCode + digits;                           // local without the trunk zero

        return result.Length is >= 10 and <= 15 && !result.StartsWith('0') ? result : null;
    }

    /// <summary>A wa.me chat link, optionally with a prefilled message; null
    /// when the number isn't linkable.</summary>
    public static string? WaMeUrl(string? rawNumber, string? text = null)
    {
        if (ToInternationalDigits(rawNumber) is not { } digits) return null;
        return string.IsNullOrWhiteSpace(text)
            ? $"https://wa.me/{digits}"
            : $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }
}
