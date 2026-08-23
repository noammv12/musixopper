using System.IO;
using System.Text;
using Whisper.net;

namespace Palon.Notes;

/// <summary>Local whisper.cpp transcription (multilingual small model).</summary>
sealed class WhisperTranscriber : ITranscriber
{
    // EnsureLoaded (not just the flag) so an AVX2-less machine is discovered
    // BEFORE a call gets recorded, not after — a failed transcription would
    // delete the audio without producing a note.
    public static bool Ready => ModelStore.IsReady && WhisperRuntime.EnsureLoaded();

    public async Task<string> TranscribeAsync(string wav16kMonoPath, string language, CancellationToken ct)
    {
        if (!WhisperRuntime.EnsureLoaded())
            throw new InvalidOperationException(WhisperRuntime.UnavailableReason ?? "Transcription engine unavailable.");
        if (!ModelStore.IsReady)
            throw new InvalidOperationException("Voice model not downloaded.");

        // The factory holds ~600 MB of model state; scoped per call so the
        // memory is returned between calls.
        using var factory = WhisperFactory.FromPath(ModelStore.ModelPath);
        var builder = factory.CreateBuilder()
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 1, 8));
        builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);

        await using var processor = builder.Build();
        var text = new StringBuilder();
        await using var stream = File.OpenRead(wav16kMonoPath);
        await foreach (var segment in processor.ProcessAsync(stream, ct))
        {
            text.Append(segment.Text);
        }
        return text.ToString();
    }
}
