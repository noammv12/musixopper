using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;

namespace Palon.Voice;

/// <summary>
/// Palon's voice. Provider chain: the free Edge neural voice (male "Avri"
/// for Hebrew, British "Ryan" for English — the Jarvis register) when
/// online, falling back to the offline Windows voice. The cloud tiers
/// stream: MP3 chunks play while synthesis is still running, so the first
/// sound lands in well under a second. Playback goes through WASAPI; a raw
/// WasapiOut is not a GSMTC media session, so speaking never trips the
/// app's own music-pause logic. One utterance at a time; Stop() interrupts.
/// </summary>
sealed class Speaker : IDisposable
{
    readonly object _gate = new();
    WasapiOut? _out;
    WaveStream? _reader;
    MemoryStream? _buffer;
    StreamingMp3Player? _player;
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
            if (Settings.VoicePreference != "windows"
                && await TryStreamCloudAsync(text, gen, synthCts.Token))
                return;
            if (gen != _generation || synthCts.IsCancellationRequested) return;

            var (reader, buffer) = await SynthesizeWindowsAsync(text);
            if (gen != _generation || reader is null)
            {
                reader?.Dispose();
                buffer?.Dispose();
                return;
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

    /// <summary>Premium tier first when a key is present, then the free Edge
    /// neural voice, both streaming into one player. True when cloud audio
    /// happened (or a barge-in ended the turn); false hands the utterance to
    /// the offline Windows voice.</summary>
    async Task<bool> TryStreamCloudAsync(string text, int gen, CancellationToken ct)
    {
        var hebrew = IsHebrew(text);
        var player = new StreamingMp3Player();
        // Fires from the network thread on first audible frame — that's the
        // moment Palon is actually "speaking", not when synthesis started.
        player.PlaybackStarted += () =>
        {
            if (gen == _generation) SetSpeaking(true);
        };
        lock (_gate)
        {
            _player = player;
        }

        var streamed = false;
        if (Settings.ElevenLabsKey is { } elevenKey)
        {
            var voiceId = Settings.ElevenLabsVoiceId is { Length: > 0 } id ? id : ElevenLabs.DefaultVoiceId;
            streamed = await ElevenLabs.StreamAsync(text, voiceId, elevenKey, hebrew, player.Write, ct);
        }
        if (!streamed && !ct.IsCancellationRequested)
            streamed = await EdgeTts.StreamAsync(
                text, hebrew ? EdgeTts.HebrewVoice : EdgeTts.DefaultVoice, player.Write, ct);

        if (gen != _generation || ct.IsCancellationRequested) return true; // barged in — Stop() cleans up
        if (streamed && player.HasAudio)
        {
            await player.FinishAsync(ct);
            return true;
        }

        // No cloud audio at all — release the player and fall offline.
        lock (_gate)
        {
            _player = null;
        }
        player.Dispose();
        return false;
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
            if (!IsHebrew(text))
            {
                // Palon is male — prefer a male voice over the (often female)
                // system default; null keeps the default when none exists.
                return SpeechSynthesizer.AllVoices
                    .FirstOrDefault(v => v.Gender == VoiceGender.Male &&
                        v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            }

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
            _player?.Dispose();
            _out = null;
            _reader = null;
            _buffer = null;
            _player = null;
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
