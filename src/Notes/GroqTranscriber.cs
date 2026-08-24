using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NAudio.Wave;

namespace Palon.Notes;

/// <summary>
/// Cloud transcription via Groq's OpenAI-compatible Whisper endpoint —
/// whisper-large-v3-turbo, much better Hebrew than the local small model
/// and seconds instead of minutes. Free tier caps files at 25 MB, so long
/// calls are split into ≤10-minute WAV chunks and the transcripts joined.
/// </summary>
sealed class GroqTranscriber : ITranscriber
{
    const string Endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
    const string Model = "whisper-large-v3-turbo";
    static readonly TimeSpan ChunkLength = TimeSpan.FromMinutes(10);
    static readonly TimeSpan MaxSingleFile = TimeSpan.FromMinutes(12);

    // Covers a ~20 MB upload on a slow uplink plus server-side processing.
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(6) };

    /// <summary>Per-request budget scaled to the clip, under the HttpClient's
    /// 6-minute outer bound: a 3-second dictated question gets ~20 s, a
    /// 10-minute call chunk keeps the full allowance. Without this, a hanging
    /// Groq request parks an interactive "Thinking…" for minutes.</summary>
    internal static TimeSpan RequestBudget(TimeSpan clipDuration) =>
        TimeSpan.FromSeconds(Math.Clamp(15 + clipDuration.TotalSeconds * 2, 20, 360));

    public async Task<string> TranscribeAsync(string wav16kMonoPath, string language, CancellationToken ct)
    {
        var key = Settings.GroqKey ?? throw new InvalidOperationException("No Groq key configured.");

        if (AudioMixdown.WavDuration(wav16kMonoPath) <= MaxSingleFile)
            return await TranscribeFileAsync(wav16kMonoPath, language, key, ct);

        // Long call: chunk under the 25 MB free-tier file cap.
        var text = new StringBuilder();
        var chunkIndex = 0;
        foreach (var chunk in SplitWav(wav16kMonoPath))
        {
            try
            {
                chunkIndex++;
                Log.Write($"Groq: transcribing chunk {chunkIndex}");
                text.Append(await TranscribeFileAsync(chunk, language, key, ct));
                text.Append(' ');
            }
            finally
            {
                try
                {
                    File.Delete(chunk);
                }
                catch
                {
                }
            }
        }
        return text.ToString();
    }

    static async Task<string> TranscribeFileAsync(string path, string language, string key, CancellationToken ct)
    {
        var budgetSpan = RequestBudget(AudioMixdown.WavDuration(path));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(budgetSpan);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            await using var file = File.OpenRead(path);
            var content = new MultipartFormDataContent();
            var audio = new StreamContent(file);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audio, "file", "audio.wav");
            content.Add(new StringContent(Model), "model");
            content.Add(new StringContent("text"), "response_format");
            content.Add(new StringContent("0"), "temperature");
            if (language != "auto") content.Add(new StringContent(language), "language");
            request.Content = content;

            using var response = await Http.SendAsync(request, budget.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var reason = status switch
                {
                    401 => "Groq key rejected",
                    413 => "Groq: file too large",
                    429 => "Groq rate limit reached",
                    _ => $"Groq HTTP {status}",
                };
                throw new HttpRequestException(reason);
            }
            return (await response.Content.ReadAsStringAsync(budget.Token)).Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The budget fired, not the caller. Surface as a non-cancellation
            // failure: ChainTranscriber's fallback filter passes real
            // cancellations through but must catch a Groq hang, or the local
            // model never gets its turn.
            throw new TimeoutException($"Groq timed out ({budgetSpan.TotalSeconds:0}s)");
        }
    }

    /// <summary>Splits a 16 kHz mono PCM16 WAV into ≤10-minute chunk files.</summary>
    static IEnumerable<string> SplitWav(string path)
    {
        var chunks = new List<string>();
        using (var reader = new WaveFileReader(path))
        {
            var bytesPerChunk = (long)(reader.WaveFormat.AverageBytesPerSecond * ChunkLength.TotalSeconds);
            // Keep chunks block-aligned.
            bytesPerChunk -= bytesPerChunk % reader.WaveFormat.BlockAlign;
            var buffer = new byte[64 * 1024];
            var index = 0;
            while (reader.Position < reader.Length)
            {
                var chunkPath = path + $".chunk{index++}.wav";
                using var writer = new WaveFileWriter(chunkPath, reader.WaveFormat);
                long written = 0;
                while (written < bytesPerChunk)
                {
                    var toRead = (int)Math.Min(buffer.Length, bytesPerChunk - written);
                    var read = reader.Read(buffer, 0, toRead);
                    if (read == 0) break;
                    writer.Write(buffer, 0, read);
                    written += read;
                }
                chunks.Add(chunkPath);
            }
        }
        return chunks;
    }
}
