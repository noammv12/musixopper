namespace Bridget.Notes;

/// <summary>
/// The engine strategy the user picked: Groq first whenever a key is set
/// (fast, better Hebrew), falling back to the on-device Whisper model on
/// any Groq failure — no key, rate limit, network down.
/// </summary>
sealed class ChainTranscriber : ITranscriber
{
    readonly GroqTranscriber _groq = new();
    readonly WhisperTranscriber _local = new();

    public static bool Ready => Settings.GroqKey is not null || WhisperTranscriber.Ready;

    public async Task<string> TranscribeAsync(string wav16kMonoPath, string language, CancellationToken ct)
    {
        if (Settings.GroqKey is not null)
        {
            try
            {
                return await _groq.TranscribeAsync(wav16kMonoPath, language, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && WhisperTranscriber.Ready)
            {
                Log.Write($"Groq failed ({ex.Message}) — falling back to the local model");
            }
        }
        return await _local.TranscribeAsync(wav16kMonoPath, language, ct);
    }
}
