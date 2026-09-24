using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Vision;

/// <summary>What the model pulled off a deposit receipt, after validation.</summary>
sealed record ReceiptData(
    string Transcript,
    string? PayerName,
    decimal? Amount,
    string? AmountText,
    string? Currency,
    DateTime? Date,
    string? Method,
    string? Reference,
    bool AmountVerified,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Receipt → deal extraction: the prompt, the JSON parse and the guard that
/// keeps a hallucinated amount out of the deal sheet — the amount must
/// appear in the model's own verbatim transcription of the image. Anything
/// unverified is still shown, flagged, and never saved without the user.
/// </summary>
static partial class ReceiptExtraction
{
    public const string SystemPrompt =
        "You read a payment/deposit receipt, bank transfer confirmation or card-payment screen the " +
        "user captured. Reply with PURE JSON only, no markdown fences:\n" +
        "{\"transcript\":\"<every legible line of text in the image, verbatim, original language and " +
        "digits, lines separated by \\n>\",\"payer_name\":\"<the person who paid/sent the money, or " +
        "null>\",\"amount\":\"<the paid amount exactly as printed, digits and separators, no currency " +
        "sign, or null>\",\"currency\":\"<ISO code: USD, ILS, EUR, GBP… or null>\",\"date\":\"<yyyy-MM-dd " +
        "or null>\",\"method\":\"<bank name / card / wire / crypto… or null>\",\"reference\":\"<transaction " +
        "or reference number, or null>\"}\n" +
        "Never guess: a field you cannot read is null. ₪/שח/ש\"ח → ILS, $ → USD, € → EUR.";

    /// <summary>The first {...} block in a reply (tolerates fences and chatter).</summary>
    public static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    internal static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return null;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>Null when the reply has no usable JSON at all.</summary>
    public static ReceiptData? Parse(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return null;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var warnings = new List<string>();
        var transcript = Text(root, "transcript") ?? "";
        var amountText = Text(root, "amount");
        var amount = amountText is null ? null : ParseAmount(amountText);
        if (amountText is not null && amount is null) warnings.Add("לא הצלחתי לקרוא את הסכום");

        var verified = amount is decimal a && AmountAppears(a, transcript);
        if (amount is not null && !verified)
            warnings.Add("הסכום לא נמצא בטקסט של הקבלה — בדוק אותו");
        if (amount is null) warnings.Add("לא נמצא סכום");

        var currency = NormalizeCurrency(Text(root, "currency"));
        if (currency is not null && currency != "USD")
            warnings.Add($"הקבלה ב-{currency} — העסקה נרשמת בדולרים, המר לפני שמירה");

        DateTime? date = null;
        if (Text(root, "date") is { } d)
        {
            if (DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                date = parsed.Date;
            else warnings.Add("התאריך לא ברור");
        }

        return new ReceiptData(transcript, Text(root, "payer_name"), amount, amountText, currency, date,
            Text(root, "method"), Text(root, "reference"), verified, warnings);
    }

    /// <summary>
    /// "3,000.00" / "3.000,00" / "3 000" / "$1,250.5" → value. A single
    /// separator followed by exactly three digits is a thousands group
    /// ("3,000" = 3000); otherwise the last separator is the decimal point.
    /// </summary>
    public static decimal? ParseAmount(string text)
    {
        var s = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
        if (s.Length == 0 || !s.Any(char.IsDigit)) return null;
        var lastSep = s.LastIndexOfAny(new[] { '.', ',' });
        string normalized;
        if (lastSep < 0)
        {
            normalized = s;
        }
        else
        {
            var tail = s[(lastSep + 1)..];
            var seps = s.Count(c => c is '.' or ',');
            var sameSepEverywhere = s.Where(c => c is '.' or ',').Distinct().Count() == 1;
            var isThousands = tail.Length == 3 && (seps > 1 ? sameSepEverywhere : true);
            normalized = isThousands
                ? new string(s.Where(char.IsDigit).ToArray())
                : new string(s[..lastSep].Where(char.IsDigit).ToArray()) + "." + tail;
        }
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
    }

    [GeneratedRegex(@"\d[\d.,\u00A0\u202F ]*\d|\d")]
    private static partial Regex NumberToken();

    /// <summary>
    /// True when some number printed in the transcript equals the amount —
    /// "3,000.00", "3000" and "3 000" all match 3000. Only number tokens are
    /// compared, so 300 never "appears" inside 3000.
    /// </summary>
    public static bool AmountAppears(decimal amount, string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        foreach (Match m in NumberToken().Matches(transcript))
        {
            var token = m.Value.Replace('\u00A0', ' ').Replace('\u202F', ' ');
            // "3 000" is one number, but "12 3000" (a date then an amount) is not:
            // only join spaced groups that look like thousands groups.
            var candidates = new List<string> { token.Replace(" ", "") };
            if (token.Contains(' ')) candidates.AddRange(token.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            foreach (var candidate in candidates)
                if (ParseAmount(candidate) == amount) return true;
        }
        return false;
    }

    public static string? NormalizeCurrency(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim().ToUpperInvariant();
        return s switch
        {
            "$" or "USD" or "US$" or "DOLLAR" or "DOLLARS" or "דולר" => "USD",
            "₪" or "ILS" or "NIS" or "שח" or "ש\"ח" or "שקל" or "שקלים" => "ILS",
            "€" or "EUR" or "EURO" => "EUR",
            "£" or "GBP" => "GBP",
            _ => s.Length is >= 2 and <= 5 ? s : null,
        };
    }

    /// <summary>A short Hebrew line describing what was read — for Ask and the sheet banner.</summary>
    public static string Describe(ReceiptData r)
    {
        var sb = new StringBuilder();
        sb.Append(r.PayerName is { } n ? $"משלם: {n}" : "משלם: לא זוהה");
        sb.Append(r.Amount is { } a ? $" · סכום: {a.ToString("#,0.##", CultureInfo.InvariantCulture)} {r.Currency ?? ""}".TrimEnd() : " · סכום: לא זוהה");
        if (r.Date is { } d) sb.Append($" · {d:dd/MM/yyyy}");
        if (r.Method is { } m) sb.Append($" · {m}");
        if (r.Reference is { } rf) sb.Append($" · אסמכתא {rf}");
        return sb.ToString();
    }
}
