using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Bridget.Voice;

/// <summary>
/// Bridget's voice. Provider chain: the free Edge neural voice (female
/// "Hila" for Hebrew — Windows ships no female Hebrew voice at all) when
/// online, falling back to the offline Windows voice. Playback goes
/// through WASAPI; a raw WasapiOut is not a GSMTC media session, so
/// speaking never trips the app's own music-pause logic. One utterance at
/// a time; Stop() interrupts.
/// </summary>
sealed class Speaker : IDisposable
{
    readonly object _gate = new();
    WasapiOut? _out;
    WaveStream? _reader;
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
            WaveStream? reader = null;
            MemoryStream? buffer = null;

            if (Settings.VoicePreference != "windows")
            {
                var voice = IsHebrew(text) ? EdgeTts.HebrewVoice : EdgeTts.DefaultVoice;
                var mp3 = await EdgeTts.SynthesizeAsync(text, voice, CancellationToken.None);
                if (gen != _generation) return; // superseded while synthesizing
                if (mp3 is not null)
                {
                    try
                    {
                        buffer = new MemoryStream(mp3);
                        reader = new Mp3FileReader(buffer);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Edge TTS audio unreadable: {ex.Message}");
                        buffer?.Dispose();
                        reader = null;
                        buffer = null;
                    }
                }
            }

            if (reader is null)
            {
                (reader, buffer) = await SynthesizeWindowsAsync(text);
                if (gen != _generation || reader is null)
                {
                    reader?.Dispose();
                    buffer?.Dispose();
                    return;
                }
            }

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

    async Task<(WaveStream? Reader, MemoryStream? Buffer)> SynthesizeWindowsAsync(string text)
    {
        try
        {
            // Fully into memory first — TTS clips are small, and it keeps the
            // WinRT stream's lifetime out of the audio path.
            using var synth = new SpeechSynthesizer();
            if (PickWindowsVoice(text) is { } voice) synth.Voice = voice;
            var winrtStream = await synth.SynthesizeTextToStreamAsync(text);
            var buffer = new MemoryStream();
            using (var src = winrtStream.AsStreamForRead())
            {
                await src.CopyToAsync(buffer);
            }
            buffer.Position = 0;
            return (new WaveFileReader(buffer), buffer);
        }
        catch (Exception ex)
        {
            Log.Write($"Windows TTS failed: {ex.Message}");
            return (null, null);
        }
    }

    VoiceInformation? PickWindowsVoice(string text)
    {
        try
        {
            if (!IsHebrew(text)) return null; // default voice is fine for Latin text

            var hebrew = SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith("he", StringComparison.OrdinalIgnoreCase));
            if (hebrew is not null) return hebrew;

            if (!_hebrewHintShown)
            {
                _hebrewHintShown = true;
                ToastRequested?.Invoke("Offline Hebrew voice missing — add it in Windows Settings → Time & Language → Speech");
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"Voice pick failed: {ex.Message}");
            return null;
        }
    }

    static bool IsHebrew(string text) => text.Any(c => c is >= '֐' and <= '׿');

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
