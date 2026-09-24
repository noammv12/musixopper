using System.Text.Json;

namespace Palon.Vision;

/// <summary>
/// Request shapes for image understanding over the OpenAI-compatible chat
/// endpoints both providers expose: a user message whose content is an
/// array of a text part and an image_url part carrying a base64 data URI.
/// Gemini's OpenAI endpoint takes this shape; DeepSeek takes it on the
/// multimodal flash model (V4.1 Flash, Sep 2026) — the Pro model is
/// text-only, so it's skipped for images rather than sent a 400.
/// </summary>
static class VisionPayload
{
    /// <summary>Hard cap on the encoded image; above it the caller re-encodes smaller.</summary>
    public const int MaxImageBytes = 3_500_000;

    public static string DataUri(byte[] jpeg) => "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

    /// <summary>The multimodal user message: text first, then the image.</summary>
    public static Dictionary<string, object?> UserMessage(string text, byte[] jpeg) => new()
    {
        ["role"] = "user",
        ["content"] = new object[]
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
            new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?> { ["url"] = DataUri(jpeg) },
            },
        },
    };

    /// <summary>Whether a provider/model accepts image parts.</summary>
    public static bool SupportsImages(string provider, string model)
    {
        if (provider == Notes.AiModels.Gemini) return true;
        var m = model.ToLowerInvariant();
        return m.Contains("flash") || m.Contains("vision") || m.Contains("vl");
    }

    // ---- screen-read replies --------------------------------------------------

    public const string ReadSystemPrompt =
        "You read a screenshot the user explicitly captured and sent. Reply with PURE JSON only, " +
        "no markdown fences: {\"transcript\":\"<all legible text in the image, verbatim, in reading " +
        "order, original language and digits>\",\"answer\":\"<your answer to the user's request>\"}. " +
        "The answer is in Hebrew (male grammatical forms for yourself) unless the user wrote English; " +
        "plain text, no markdown, no emoji; concise — at most 6 short lines unless asked to summarize " +
        "something long. Only what is visible: never guess text you cannot read — say it is unclear.";

    /// <summary>A parsed screen-read reply: what the image says, and the answer.</summary>
    public sealed record ReadReply(string Transcript, string Answer);

    /// <summary>Lenient parse: JSON with transcript/answer, else the raw prose as the answer.</summary>
    public static ReadReply ParseRead(string raw)
    {
        var json = ReceiptExtraction.ExtractJsonObject(raw);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var transcript = ReceiptExtraction.Text(root, "transcript") ?? "";
                var answer = ReceiptExtraction.Text(root, "answer") ?? "";
                if (answer.Length == 0) answer = transcript;
                if (answer.Length > 0) return new ReadReply(transcript, answer.Trim());
            }
            catch (JsonException)
            {
                // fall through to prose
            }
        }
        var prose = raw.Trim();
        return new ReadReply(prose, prose);
    }
}
