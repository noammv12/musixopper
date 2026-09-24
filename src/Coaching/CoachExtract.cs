using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palon.Coaching;

/// <summary>The fixed objection categories the model must choose from.</summary>
static class Objections
{
    public const string HasBroker = "already_has_broker";
    public const string NoMoney = "no_money_now";
    public const string NeedsToThink = "needs_to_think";
    public const string Spouse = "spouse";
    public const string Trust = "trust_regulation";
    public const string Fees = "fees";
    public const string Other = "other";

    public static readonly string[] All = { HasBroker, NoMoney, NeedsToThink, Spouse, Trust, Fees, Other };

    public static string Label(string cat) => cat switch
    {
        HasBroker => "כבר יש ברוקר",
        NoMoney => "אין כסף כרגע",
        NeedsToThink => "צריך לחשוב",
        Spouse => "בן/בת זוג",
        Trust => "אמון / רגולציה",
        Fees => "עמלות",
        _ => "אחר",
    };
}

sealed record CoachItem(string Quote, string? Category = null);

sealed record CoachNextStep(bool Agreed, string? Text, string? Quote);

/// <summary>What the AI heard, every item backed by a verified transcript quote.</summary>
sealed record CoachData(
    List<CoachItem> Objections,
    List<CoachItem> ClientQuestions,
    CoachNextStep? NextStep,
    int Dropped = 0); // items thrown out because their quote wasn't in the transcript

/// <summary>
/// The COACH trailer line of the summary reply: parse, validate against the
/// enum and schema, then keep only items whose quote really appears in the
/// transcript (after normalizing Hebrew punctuation, niqqud and whitespace).
/// </summary>
static class CoachExtract
{
    public const string Marker = "COACH:";

    /// <summary>Removes the COACH line from the reply. Line is null when absent.</summary>
    public static (string Text, string? Line) Split(string reply)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n').ToList();
        var index = lines.FindLastIndex(l => l.TrimStart().TrimStart('*', '`', ' ').StartsWith(Marker, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return (reply.Trim(), null);
        var line = lines[index];
        lines.RemoveAt(index);
        var rest = string.Join("\n", lines).Trim();
        return (rest.Length == 0 ? reply.Trim() : rest, line);
    }

    /// <summary>Parses the JSON (the line may carry the marker or be bare
    /// JSON from the retry) and validates it. Error is set when the shape is
    /// wrong — the caller retries once with it.</summary>
    public static (CoachData? Data, string? Error) Parse(string line, string transcript)
    {
        var start = line.IndexOf('{');
        var end = line.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, "no JSON object found");
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            return (null, "invalid JSON: " + ex.Message);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "top level must be an object");
            var norm = Normalize(transcript);
            var dropped = 0;

            var objections = new List<CoachItem>();
            if (root.TryGetProperty("objections", out var objs))
            {
                if (objs.ValueKind != JsonValueKind.Array) return (null, "objections must be an array");
                foreach (var o in objs.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) return (null, "each objection must be an object");
                    var cat = Str(o, "category") ?? Str(o, "cat");
                    var quote = Str(o, "quote");
                    if (cat is null || quote is null) return (null, "objection needs category and quote");
                    cat = cat.Trim().ToLowerInvariant().Replace(' ', '_').Replace('/', '_');
                    if (!Objections.All.Contains(cat)) return (null, $"unknown category '{cat}' — use one of {string.Join(", ", Objections.All)}");
                    if (QuoteFound(quote, norm)) objections.Add(new CoachItem(quote.Trim(), cat));
                    else dropped++;
                }
            }

            var questions = new List<CoachItem>();
            if (root.TryGetProperty("client_questions", out var qs))
            {
                if (qs.ValueKind != JsonValueKind.Array) return (null, "client_questions must be an array");
                foreach (var q in qs.EnumerateArray())
                {
                    var quote = q.ValueKind == JsonValueKind.String ? q.GetString() : q.ValueKind == JsonValueKind.Object ? Str(q, "quote") : null;
                    if (quote is null) return (null, "each client question needs a quote");
                    if (QuoteFound(quote, norm)) questions.Add(new CoachItem(quote.Trim()));
                    else dropped++;
                }
            }

            CoachNextStep? next = null;
            if (root.TryGetProperty("next_step", out var ns) && ns.ValueKind != JsonValueKind.Null)
            {
                if (ns.ValueKind != JsonValueKind.Object) return (null, "next_step must be an object");
                if (!ns.TryGetProperty("agreed", out var ag) || ag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return (null, "next_step.agreed must be true or false");
                var quote = Str(ns, "quote");
                if (ag.GetBoolean())
                {
                    if (quote is null) return (null, "an agreed next_step needs a quote");
                    if (QuoteFound(quote, norm)) next = new CoachNextStep(true, Str(ns, "text")?.Trim(), quote.Trim());
                    else dropped++;
                }
                else next = new CoachNextStep(false, null, null);
            }
            return (new CoachData(objections, questions, next, dropped), null);
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    public static bool QuoteFound(string quote, string normalizedTranscript)
    {
        var q = Normalize(quote);
        return q.Length >= 2 && normalizedTranscript.Contains(q, StringComparison.Ordinal);
    }

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Lowercase; niqqud/cantillation, quote marks, geresh and
    /// gershayim removed; maqaf, dashes and punctuation become spaces;
    /// whitespace collapsed.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch is >= '֑' and <= 'ׇ' && ch is not ('־' or '׀' or '׃' or '׆')) continue; // points
            if (ch is '"' or '\'' or '`' or '׳' or '״' or '‘' or '’' or '“' or '”' or '„' or '´') continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else sb.Append(' ');
        }
        return Spaces.Replace(sb.ToString(), " ").Trim();
    }
}
