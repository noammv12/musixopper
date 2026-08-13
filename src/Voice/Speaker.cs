using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Bridget.Voice;

/// <summary>
/// Bridget's voice: Windows' built-in TTS (WinRT SpeechSynthesis) played
/// through WASAPI. Picks a voice matching the text's language — Hebrew
/// text gets a he-* voice when one is installed. One utterance at a time;
/// Stop() interrupts. Raw WasapiOut is not a GSMTC media session, so
/// speaking never trips the app's own music-pause logic.
/// </summary>
sealed class Speaker : IDisposable
{
    readonly object _gate = new();
    WasapiOut? _out;
    WaveFileReader? _reader;
    MemoryStream? _buffer;
    int _generation;
    bool _hebrewHintShown;

    public bool IsSpeaking { get; private set; }
    public event Action<bool>? SpeakingChanged;
    public event Action<string>? ToastRequested;

    /// <summary>Speaks the text, replacing any utterance in progress.
    /// Returns when playback finishes or is stopped. Never throws.</summary>
    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();
        var gen = Interlocked.Increment(ref _generation);
        try
        {
            // Synthesize fully into memory first — TTS clips are small, and
            // it keeps the WinRT stream's lifetime out of the audio path.
            using var synth = new SpeechSynthesizer();
            if (PickVoice(text) is { } voice) synth.Voice = voice;
            var winrtStream = await synth.SynthesizeTextToStreamAsync(text);
            var buffer = new MemoryStream();
            using (var src = winrtStream.AsStreamForRead())
            {
                await src.CopyToAsync(buffer);
            }
            buffer.Position = 0;
            if (gen != _generation)
            {
                buffer.Dispose();
                return; // superseded while synthesizing
            }

            var reader = new WaveFileReader(buffer);
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
            output.Init(reader);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, _) => done.TrySetResult();

            lock (_gate)
            {
                _out = output;
                _reader = reader;
                _buffer = buffer;
            }
            SetSpeaking(true);
            output.Play();
            await done.Task;
        }
        catch (Exception ex)
        {
            Log.Write($"Speak failed: {ex.Message}");
        }
        finally
        {
            if (gen == _generation)
            {
                CleanupPlayback();
                SetSpeaking(false);
            }
        }
    }

    /// <summary>Cuts off the current utterance (and cancels one being synthesized).</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        CleanupPlayback();
        SetSpeaking(false);
    }

    VoiceInformation? PickVoice(string text)
    {
        try
        {
            var wantsHebrew = text.Any(c => c is >= '֐' and <= '׿');
            if (!wantsHebrew) return null; // default voice is fine for Latin text

            var hebrew = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith("he", StringComparison.OrdinalIgnoreCase));
            if (hebrew is not null) return hebrew;

            if (!_hebrewHintShown)
            {
                _hebrewHintShown = true;
                ToastRequested?.Invoke("For Hebrew replies, add the Hebrew voice: Windows Settings → Time & Language → Speech");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Voice pick failed: {ex.Message}");
            return null;
        }
    }

    void CleanupPlayback()
    {
        lock (_gate)
        {
            try
            {
                _out?.Stop();
            }
            catch
            {
            }
            _out?.Dispose();
            _reader?.Dispose();
            _buffer?.Dispose();
            _out = null;
            _reader = null;
            _buffer = null;
        }
    }

    void SetSpeaking(bool speaking)
    {
        if (IsSpeaking == speaking) return;
        IsSpeaking = speaking;
        SpeakingChanged?.Invoke(speaking);
    }

    public void Dispose() => Stop();
}
