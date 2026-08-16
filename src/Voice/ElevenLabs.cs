using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Bridget.Voice;

/// <summary>
/// Optional premium voice: ElevenLabs TTS, activated by pasting an API key.
/// Hebrew needs their newer models, so the model is picked per utterance.
/// Null on any failure — the Speaker falls down the chain, never to silence.
/// </summary>
static class ElevenLabs
{
    /// <summary>"Rachel", ElevenLabs' well-known premade female voice.</summary>
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<byte[]?> SynthesizeAsync(string text, string voiceId, string apiKey, bool hebrew, CancellationToken ct)
    {
        try
        {
            // Hebrew arrived with the v3 model family; multilingual v2 is the
            // stable default for everything else.
            var model = hebrew ? "eleven_v3" : "eleven_multilingual_v2";
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}?output_format=mp3_44100_64");
            request.Headers.Add("xi-api-key", apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { text, model_id = model }),
                Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"ElevenLabs: HTTP {(int)response.StatusCode}");
                return null;
            }
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"ElevenLabs failed: {ex.Message}");
            return null;
        }
    }
}
