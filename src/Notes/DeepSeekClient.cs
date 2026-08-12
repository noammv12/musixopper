using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Saley.Notes;

/// <summary>
/// Turns a transcript into "3 bullets + next step" via DeepSeek's
/// OpenAI-compatible chat API. Every failure degrades to a
/// transcript-only note — a summary is never worth losing a note over.
/// </summary>
static class DeepSeekClient
{
    const string Endpoint = "https://api.deepseek.com/chat/completions";
    const int MaxTranscriptChars = 100_000;

    const string SystemPrompt =
        "You write concise notes from a sales-call transcript. Reply in the language the " +
        "transcript is mostly written in (Hebrew transcript → Hebrew reply). Output exactly " +
        "4 lines: 3 lines starting with '• ' — the key facts, decisions or objections; then " +
        "1 line starting with 'Next step: ' (in Hebrew: 'הצעד הבא: ') with the single most " +
        "important follow-up. No headings, no extra text.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Returns the summary, or null when unavailable (no retry beyond one).</summary>
    public static async Task<string?> SummarizeAsync(string transcript, string apiKey, CancellationToken ct)
    {
        if (transcript.Length > MaxTranscriptChars) transcript = transcript[..MaxTranscriptChars];

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
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = "Transcript:\n" + transcript },
                    },
                    temperature = 0.3,
                    max_tokens = 400,
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
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
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
