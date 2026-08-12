using System.IO;
using System.Text;
using Whisper.net;

namespace Saley.Notes;

/// <summary>Local whisper.cpp transcription (multilingual small model).</summary>
sealed class WhisperTranscriber : ITranscriber
{
    public static bool Ready => ModelStore.IsReady && WhisperRuntime.UnavailableReason is null;

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
