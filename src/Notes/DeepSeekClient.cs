using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bridget.Notes;

/// <summary>Ask-Bridget outcome: Action is "open" (CommandId set), "open_url"
/// (Url set), or "answer" (Text set).</summary>
sealed record AssistResult(string Action, string? CommandId, string? Text, string? Url = null);

/// <summary>
/// Bridget's AI brain: an OpenAI-compatible chat client behind a provider
/// chain — Gemini first when its key exists (free tier, 1,500 calls/day),
/// DeepSeek as the second. Call summaries, dictation polish, follow-up
/// drafts and Ask-Bridget intent all route through here. Every failure
/// returns null and records LastError — AI output is never worth losing
/// the underlying text over.
/// </summary>
static class AiChat
{
    const int MaxTranscriptChars = 100_000;
    const int MaxDictationChars = 8_000;

    const string SummaryPrompt =
        "You write concise notes from a sales-call transcript. Reply in the language the " +
        "transcript is mostly written in (Hebrew transcript → Hebrew reply). Output exactly " +
        "4 lines: 3 lines starting with '• ' — the key facts, decisions or objections; then " +
        "1 line starting with 'Next step: ' (in Hebrew: 'הצעד הבא: ') with the single most " +
        "important follow-up. No headings, no extra text.";

    const string PolishPrompt =
        "You clean up dictated text. Fix punctuation and casing, remove filler words, false " +
        "starts and immediate self-corrections (keep the corrected version), and fix obvious " +
        "speech-to-text mistakes. Keep the original language, wording and meaning — do not " +
        "add, drop, summarize or translate anything. Reply with the cleaned text only.";

    const string ProfessionalPrompt = PolishPrompt +
        " Then lightly smooth the phrasing so it reads as clear, professional business " +
        "writing — still without adding or removing information.";

    const string FollowUpPrompt =
        "You draft the short follow-up message a salesperson sends right after a call, " +
        "WhatsApp style. Write in the language of the notes (Hebrew notes → Hebrew " +
        "message). Warm and brief — 2 to 4 sentences: reference what was discussed, " +
        "then end with the agreed next step. No subject line, no signature, no " +
        "placeholders like [name]. Reply with the message text only.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Why the last ChatAsync returned null (per provider), for UI surfacing.</summary>
    public static string? LastError { get; private set; }

    /// <summary>True when any AI provider is configured.</summary>
    public static bool HasKey => Settings.GeminiKey is not null || Settings.DeepSeekKey is not null;

    /// <summary>Returns the summary, or null when unavailable.</summary>
    public static Task<string?> SummarizeAsync(string transcript, CancellationToken ct)
    {
        if (transcript.Length > MaxTranscriptChars) transcript = transcript[..MaxTranscriptChars];
        return ChatAsync(SummaryPrompt, "Transcript:\n" + transcript, 0.3, 400, ct);
    }

    /// <summary>Cleaned-up dictation, or null when unavailable (caller keeps the raw text).</summary>
    public static Task<string?> PolishAsync(string text, bool professional, CancellationToken ct)
    {
        if (text.Length > MaxDictationChars) return Task.FromResult<string?>(null); // too long to round-trip — keep raw
        // rejectTruncated: a polish cut off at the token cap must never
        // replace the full raw transcript.
        return ChatAsync(professional ? ProfessionalPrompt : PolishPrompt, text, 0.2, 4096, ct, rejectTruncated: true);
    }

    /// <summary>A paste-ready follow-up message, or null when unavailable.</summary>
    public static Task<string?> FollowUpAsync(string noteText, CancellationToken ct)
    {
        if (noteText.Length > MaxTranscriptChars) noteText = noteText[..MaxTranscriptChars];
        return ChatAsync(FollowUpPrompt, "Call notes:\n" + noteText, 0.5, 300, ct);
    }

    /// <summary>
    /// Ask-Bridget intent: run a saved command, open a well-known URL, or
    /// answer. Null only when every provider failed; malformed model output
    /// degrades to treating plain-prose replies as the answer.
    /// </summary>
    public static async Task<AssistResult?> AssistAsync(
        string question, IReadOnlyList<BridgetCommand> commands, CancellationToken ct)
    {
        var prompt = new StringBuilder(
            "You are Bridget, a decisive personal assistant for a busy salesperson. The input is a " +
            "voice transcript. Decide ONE action and reply with PURE JSON only, no markdown fences:\n" +
            "1. {\"action\":\"open\",\"id\":\"<command id>\"} — the request matches one of the user's " +
            "saved commands (match generously across languages and phrasings: Hebrew " +
            "'תפתחי סיילספורס' matches a command labeled 'Salesforce').\n" +
            "2. {\"action\":\"open_url\",\"url\":\"https://…\"} — the request is to open a well-known " +
            "website that is NOT a saved command (YouTube → https://www.youtube.com, Gmail, " +
            "WhatsApp Web…), or to search ('חפש X' / 'search for X' → " +
            "https://www.google.com/search?q=X, URL-encoded).\n" +
            "3. {\"action\":\"answer\",\"text\":\"...\"} — anything else: answer in the user's " +
            "language, at most 2 short sentences unless they clearly asked for more, plain text, " +
            "no emoji.\n" +
            "HARD RULES: never ask a clarifying question, never reply with a generic 'how can I " +
            "help'. If asked to open something you can't resolve to a command or a URL, the answer " +
            "is one short sentence telling the user to add it under Commands. If the transcript is " +
            "garbled, say you didn't catch it. Prefer a saved command over open_url when both fit.");
        if (commands.Count > 0)
        {
            prompt.Append("\nSaved commands:");
            foreach (var command in commands)
                prompt.Append($"\n- id={command.Id} label=\"{command.Label}\"");
        }
        else
        {
            prompt.Append("\nThe user has no saved commands.");
        }

        // rejectTruncated: half a JSON object must not reach the fallback
        // below, where it would be displayed — and spoken — verbatim.
        var raw = await ChatAsync(prompt.ToString(), question, 0.2, 500, ct, rejectTruncated: true);
        if (raw is null) return null;

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
                var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() : null;
                if (action == "open"
                    && doc.RootElement.TryGetProperty("id", out var id)
                    && id.GetString() is { Length: > 0 } commandId)
                    return new AssistResult("open", commandId, null);
                if (action == "open_url"
                    && doc.RootElement.TryGetProperty("url", out var u)
                    && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                    return new AssistResult("open_url", null, null, uri.AbsoluteUri);
                if (action == "answer"
                    && doc.RootElement.TryGetProperty("text", out var t)
                    && t.GetString() is { Length: > 0 } text)
                    return new AssistResult("answer", null, text);
                return null; // valid JSON but no usable action/content
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        // The model ignored the JSON contract. Plain prose is still a usable
        // answer; a JSON-looking fragment is not.
        return raw.StartsWith('{') ? null : new AssistResult("answer", null, raw);
    }

    // ---- provider chain ---------------------------------------------------

    sealed record Provider(string Name, string Url, string Model, string Key);

    static IEnumerable<Provider> Providers()
    {
        // Gemini first: its free tier absorbs the daily volume; DeepSeek is
        // the paid fallback. Either alone also works.
        if (Settings.GeminiKey is { } gemini)
            yield return new Provider("Gemini",
                "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                Settings.GeminiModel, gemini);
        if (Settings.DeepSeekKey is { } deepSeek)
            yield return new Provider("DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-chat", deepSeek);
    }

    static async Task<string?> ChatAsync(string systemPrompt, string userContent, double temperature, int maxTokens, CancellationToken ct, bool rejectTruncated = false)
    {
        var errors = new List<string>(2);
        foreach (var provider in Providers())
        {
            var (text, error) = await ChatOnceAsync(provider, systemPrompt, userContent, temperature, maxTokens, ct, rejectTruncated);
            if (text is not null)
            {
                LastError = null;
                return text;
            }
            errors.Add($"{provider.Name}: {error}");
            if (ct.IsCancellationRequested) break;
        }
        LastError = errors.Count > 0 ? string.Join(" → ", errors) : "no AI key";
        if (errors.Count > 0) Log.Write($"AI chain failed: {LastError}");
        return null;
    }

    static async Task<(string? Text, string Error)> ChatOnceAsync(
        Provider provider, string systemPrompt, string userContent,
        double temperature, int maxTokens, CancellationToken ct, bool rejectTruncated)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, provider.Url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
                var body = new
                {
                    model = provider.Model,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userContent },
                    },
                    temperature,
                    max_tokens = maxTokens,
                    stream = false,
                };
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                using var response = await Http.SendAsync(request, ct);
                var status = (int)response.StatusCode;
                if ((status == 429 || status >= 500) && attempt == 0)
                {
                    await Task.Delay(2000, ct);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, "key rejected (401)");
                if (status == 402) return (null, "insufficient balance (402)");
                if (status == 429) return (null, "rate limited (429)");
                if (!response.IsSuccessStatusCode) return (null, $"HTTP {status}");

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (rejectTruncated
                    && choice.TryGetProperty("finish_reason", out var finish)
                    && finish.GetString() == "length")
                    return (null, "hit the token cap");
                var content = choice.GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(content) ? (null, "empty reply") : (content.Trim(), "");
            }
            catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
            {
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch
                {
                    return (null, "cancelled");
                }
            }
            catch (OperationCanceledException)
            {
                return (null, "cancelled");
            }
            catch (Exception ex)
            {
                return (null, ex.Message);
            }
        }
        return (null, "network error");
    }
}
