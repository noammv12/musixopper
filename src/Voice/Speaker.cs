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
    CancellationTokenSource? _synthCts;
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
        // Stop()/barge-in must be able to abort in-flight cloud synthesis,
        // not just the playback after it.
        using var synthCts = new CancellationTokenSource();
        lock (_gate)
        {
            _synthCts = synthCts;
        }
        try
        {
            WaveStream? reader = null;
            MemoryStream? buffer = null;

            if (Settings.VoicePreference != "windows")
            {
                var hebrew = IsHebrew(text);
                byte[]? mp3 = null;

                // Premium tier first when a key is present, then the free
                // Edge neural voice.
                if (Settings.ElevenLabsKey is { } elevenKey)
                {
                    var voiceId = Settings.ElevenLabsVoiceId is { Length: > 0 } id ? id : ElevenLabs.DefaultVoiceId;
                    mp3 = await ElevenLabs.SynthesizeAsync(text, voiceId, elevenKey, hebrew, synthCts.Token);
                    if (gen != _generation) return;
                }
                if (mp3 is null && !synthCts.IsCancellationRequested)
                {
                    mp3 = await EdgeTts.SynthesizeAsync(text, hebrew ? EdgeTts.HebrewVoice : EdgeTts.DefaultVoice, synthCts.Token);
                    if (gen != _generation) return;
                }

                if (mp3 is not null)
                {
                    try
                    {
                        buffer = new MemoryStream(mp3);
                        // Mp3FileReader lives in the NAudio metapackage; the
                        // split packages expose the same thing as base+ACM.
                        reader = new Mp3FileReaderBase(buffer, wf => new AcmMp3FrameDecompressor(wf));
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Cloud TTS audio unreadable: {ex.Message}");
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
            lock (_gate)
            {
                if (_synthCts == synthCts) _synthCts = null;
            }
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
        lock (_gate)
        {
            try
            {
                _synthCts?.Cancel();
            }
            catch
            {
            }
        }
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
