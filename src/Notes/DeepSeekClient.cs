using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bridget.Notes;

/// <summary>Ask-Bridget outcome: Action is "open" (CommandId set) or "answer" (Text set).</summary>
sealed record AssistResult(string Action, string? CommandId, string? Text);

/// <summary>
/// DeepSeek's OpenAI-compatible chat API: call summaries, dictation
/// polish, and follow-up drafts. Every failure returns null — AI output
/// is never worth losing the underlying text over.
/// </summary>
static class DeepSeekClient
{
    const string Endpoint = "https://api.deepseek.com/chat/completions";
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

    /// <summary>Returns the summary, or null when unavailable (no retry beyond one).</summary>
    public static Task<string?> SummarizeAsync(string transcript, string apiKey, CancellationToken ct)
    {
        if (transcript.Length > MaxTranscriptChars) transcript = transcript[..MaxTranscriptChars];
        return ChatAsync(SummaryPrompt, "Transcript:\n" + transcript, 0.3, 400, apiKey, ct);
    }

    /// <summary>Cleaned-up dictation, or null when unavailable (caller keeps the raw text).</summary>
    public static Task<string?> PolishAsync(string text, bool professional, string apiKey, CancellationToken ct)
    {
        if (text.Length > MaxDictationChars) return Task.FromResult<string?>(null); // too long to round-trip — keep raw
        // rejectTruncated: a polish cut off at the token cap must never
        // replace the full raw transcript.
        return ChatAsync(professional ? ProfessionalPrompt : PolishPrompt, text, 0.2, 4096, apiKey, ct, rejectTruncated: true);
    }

    /// <summary>A paste-ready follow-up message, or null when unavailable.</summary>
    public static Task<string?> FollowUpAsync(string noteText, string apiKey, CancellationToken ct)
    {
        if (noteText.Length > MaxTranscriptChars) noteText = noteText[..MaxTranscriptChars];
        return ChatAsync(FollowUpPrompt, "Call notes:\n" + noteText, 0.5, 300, apiKey, ct);
    }

    /// <summary>
    /// Ask-Bridget intent: either run one of the user's saved commands or
    /// answer the question. Null only when the API is unreachable; malformed
    /// model output degrades to treating the raw text as the answer.
    /// </summary>
    public static async Task<AssistResult?> AssistAsync(
        string question, IReadOnlyList<BridgetCommand> commands, string apiKey, CancellationToken ct)
    {
        var prompt = new StringBuilder(
            "You are Bridget, a concise personal assistant for a busy salesperson. Decide whether " +
            "the user's spoken request runs one of their saved commands or needs an answer. Reply " +
            "with PURE JSON only, no markdown fences: {\"action\":\"open\",\"id\":\"<command id>\"} " +
            "to run a command, or {\"action\":\"answer\",\"text\":\"...\"} to answer. Match commands " +
            "generously across languages and phrasings (Hebrew 'תפתח סיילספורס' matches a command " +
            "labeled 'Salesforce'). Answers: reply in the user's language, at most 2 short sentences " +
            "unless they clearly asked for more, plain text, no emoji.");
        if (commands.Count > 0)
        {
            prompt.Append("\nSaved commands:");
            foreach (var command in commands)
                prompt.Append($"\n- id={command.Id} label=\"{command.Label}\"");
        }
        else
        {
            prompt.Append("\nThe user has no saved commands — always answer.");
        }

        var raw = await ChatAsync(prompt.ToString(), question, 0.2, 500, apiKey, ct);
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
                if (action == "answer"
                    && doc.RootElement.TryGetProperty("text", out var t)
                    && t.GetString() is { Length: > 0 } text)
                    return new AssistResult("answer", null, text);
            }
        }
        catch (JsonException)
        {
            // fall through — the raw text is still a usable answer
        }
        return new AssistResult("answer", null, raw);
    }

    static async Task<string?> ChatAsync(string systemPrompt, string userContent, double temperature, int maxTokens, string apiKey, CancellationToken ct, bool rejectTruncated = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var body = new
                {
                    model = "deepseek-chat",
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
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Log.Write("DeepSeek: key rejected (401)");
                    return null;
                }
                if (status == 402)
                {
                    Log.Write("DeepSeek: insufficient balance (402)");
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    Log.Write($"DeepSeek: HTTP {status}");
                    return null;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var choice = doc.RootElement.GetProperty("choices")[0];
                if (rejectTruncated
                    && choice.TryGetProperty("finish_reason", out var finish)
                    && finish.GetString() == "length")
                {
                    Log.Write("DeepSeek: response hit the token cap — discarded");
                    return null;
                }
                var content = choice.GetProperty("message").GetProperty("content").GetString();
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }
            catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
            {
                Log.Write($"DeepSeek: {ex.Message} — retrying");
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"DeepSeek failed: {ex.Message}");
                return null;
            }
        }
        return null;
    }
}
