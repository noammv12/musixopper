namespace Saley.Notes;

/// <summary>
/// Speech-to-text seam: today the local Whisper engine; a cloud option can
/// slot in later without touching the pipeline.
/// </summary>
interface ITranscriber
{
    /// <param name="wav16kMonoPath">16 kHz mono PCM16 WAV.</param>
    /// <param name="language">ISO code like "he", or "auto".</param>
    Task<string> TranscribeAsync(string wav16kMonoPath, string language, CancellationToken ct);
}

/// <summary>Placeholder until the Whisper engine ships.</summary>
sealed class UnavailableTranscriber : ITranscriber
{
    public Task<string> TranscribeAsync(string wav16kMonoPath, string language, CancellationToken ct) =>
        throw new InvalidOperationException("Transcription engine not installed.");
}
