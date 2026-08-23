using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Palon.Voice;

/// <summary>
/// Optional premium voice: ElevenLabs TTS, activated by pasting an API key.
/// Hebrew needs their newer models, so the model is picked per utterance.
/// Null on any failure — the Speaker falls down the chain, never to silence.
/// </summary>
static class ElevenLabs
{
    /// <summary>"Daniel", ElevenLabs' premade British male voice — the
    /// closest premade match to Palon's composed-aide register.</summary>
    public const string DefaultVoiceId = "onwK4e9ZLuTAKqWW03F9";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>Streams the utterance's MP3 to onChunk via the /stream
    /// endpoint. True once any audio arrived (even if the stream then died —
    /// playing the truncated audio beats double-playing via the next tier);
    /// false means none did and the Speaker should fall down the chain.</summary>
    public static async Task<bool> StreamAsync(
        string text, string voiceId, string apiKey, bool hebrew, Action<byte[]> onChunk, CancellationToken ct)
    {
        var delivered = false;
        try
        {
            // Hebrew arrived with the v3 model family; multilingual v2 is the
            // stable default for everything else.
            var model = hebrew ? "eleven_v3" : "eleven_multilingual_v2";
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream?output_format=mp3_44100_64");
            request.Headers.Add("xi-api-key", apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { text, model_id = model }),
                Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"ElevenLabs: HTTP {(int)response.StatusCode}");
                return false;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                delivered = true;
                onChunk(buffer[..read]);
            }
            return delivered;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return delivered;
        }
        catch (Exception ex)
        {
            Log.Write($"ElevenLabs failed{(delivered ? " mid-stream (playing what arrived)" : "")}: {ex.Message}");
            return delivered;
        }
    }
}
