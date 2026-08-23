using System.IO;
using System.Windows.Threading;

namespace Palon.Notes;

/// <summary>
/// Orchestrates call notes: records while the engine is on a call, then
/// mix → transcribe → summarize on a background queue. Sessions are
/// processed strictly one at a time (a new call can record while the
/// previous one is still transcribing) and the audio is deleted after the
/// processing attempt, success or fail — that's the privacy promise.
/// </summary>
sealed class NotesPipeline : IDisposable
{
    static readonly TimeSpan MinCallLength = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MaxRecording = TimeSpan.FromMinutes(45);

    readonly CallEngine _engine;
    readonly CallRecorder _recorder = new();
    readonly SemaphoreSlim _processQueue = new(1, 1);
    readonly DispatcherTimer _capTimer;
    CallState _lastState = CallState.Idle;
    string? _activeCallNumber; // captured at recording start; engine clears its copy at call end

    /// <summary>Replaced by the Whisper engine when it's available.</summary>
    public ITranscriber Transcriber { get; set; } = new UnavailableTranscriber();

    /// <summary>True when the transcription engine + model are usable.</summary>
    public Func<bool> TranscriberReady { get; set; } = () => false;

    public event Action<string>? StatusChanged; // "" = idle
    public event Action<string>? ToastRequested;
    public event Action<CallNote>? NoteReady;

    public NotesPipeline(CallEngine engine)
    {
        _engine = engine;
        _engine.StateChanged += OnEngineStateChanged;

        _capTimer = new DispatcherTimer { Interval = MaxRecording };
        _capTimer.Tick += (_, _) =>
        {
            _capTimer.Stop();
            // Runaway call: stop and process what we have.
            if (_recorder.Stop() is { } session)
            {
                Log.Write("Recorder: 45 min cap reached");
                _ = ProcessAsync(session, recovered: false, _activeCallNumber);
            }
        };
    }

    public void SweepRecoveredSessions()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(NotesStore.TmpDir)) return;
                // Loose dictation temp files (crash mid-dictation) are only
                // valid while recording — delete them.
                foreach (var file in Directory.GetFiles(NotesStore.TmpDir, "dictation-*"))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
                foreach (var dir in Directory.GetDirectories(NotesStore.TmpDir))
                {
                    var mic = Path.Combine(dir, "mic.wav");
                    var sys = Path.Combine(dir, "sys.wav");
                    var session = new RecordingSession
                    {
                        Directory = dir,
                        StartedUtc = Directory.GetCreationTimeUtc(dir),
                    };
                    session.EndedUtc = Directory.GetLastWriteTimeUtc(dir);
                    if (File.Exists(mic))
                    {
                        session.MicPath = mic;
                        session.MicStartUtc = File.GetCreationTimeUtc(mic);
                    }
                    if (File.Exists(sys))
                    {
                        session.SysPath = sys;
                        session.SysStartUtc = File.GetCreationTimeUtc(sys);
                    }
                    if (session.MicPath is null && session.SysPath is null)
                    {
                        CleanupSession(session);
                        continue;
                    }
                    Log.Write($"Notes: recovering interrupted session {Path.GetFileName(dir)}");
                    _ = ProcessAsync(session, recovered: true);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Notes sweep failed: {ex.Message}");
            }
        });
    }

    void OnEngineStateChanged()
    {
        var state = _engine.State;
        var was = _lastState;
        _lastState = state;

        if (state == CallState.OnCall && was != CallState.OnCall)
        {
            _activeCallNumber = _engine.CurrentNumber;
            if (!Settings.NotesEnabled) return;
            if (!TranscriberReady())
            {
                // Silent skips read as "notes stopped working" — say why.
                Log.Write("Notes skipped — transcriber not ready (no Groq key and no offline model)");
                ToastRequested?.Invoke("Notes skipped — add a Groq key or download the offline model");
                return;
            }
            if (_recorder.Start(NotesStore.TmpDir) is null)
            {
                ToastRequested?.Invoke("Notes: couldn't record audio");
                return;
            }
            _capTimer.Start();
            return;
        }

        if (was == CallState.OnCall && state != CallState.OnCall)
        {
            _capTimer.Stop();
            var session = _recorder.Stop();
            if (session is null) return;

            // Disabled mid-call or feature switched off: honor the opt-out.
            if (state == CallState.Disabled || !Settings.NotesEnabled)
            {
                CleanupSession(session);
                return;
            }
            if (session.Duration < MinCallLength)
            {
                CleanupSession(session);
                return;
            }
            ToastRequested?.Invoke("Taking notes…");
            _ = ProcessAsync(session, recovered: false, _activeCallNumber);
        }
    }

    async Task ProcessAsync(RecordingSession session, bool recovered, string? number = null)
    {
        await _processQueue.WaitAsync();
        try
        {
            await Task.Run(async () =>
            {
                var keepForRetry = false;
                try
                {
                    StatusChanged?.Invoke("Preparing audio…");
                    var mixed = Path.Combine(session.Directory, "mixed.wav");
                    if (!File.Exists(mixed)) mixed = AudioMixdown.To16kMono(session) ?? "";
                    if (mixed.Length == 0 || !File.Exists(mixed))
                    {
                        if (!recovered) ToastRequested?.Invoke("Notes: no usable audio");
                        return;
                    }
                    var audioLength = AudioMixdown.WavDuration(mixed);
                    Log.Write($"Notes: mixed audio {audioLength.TotalSeconds:0.#}s");
                    if (audioLength < MinCallLength)
                    {
                        Log.Write("Notes: mixed audio too short — skipping");
                        if (!recovered) ToastRequested?.Invoke("Notes: call audio too short");
                        return;
                    }

                    StatusChanged?.Invoke("Transcribing your last call…");
                    var transcript = (await Transcriber.TranscribeAsync(mixed, Settings.NotesLanguage, CancellationToken.None)).Trim();
                    Log.Write($"Notes: transcript {transcript.Length} chars");
                    if (transcript.Length == 0)
                    {
                        if (!recovered) ToastRequested?.Invoke("Notes: nothing heard on the call");
                        return;
                    }
                    if (session.MicUnavailable)
                        transcript = "(Microphone wasn't recorded — your side of the call may be missing.)\n" + transcript;

                    string? summary = null;
                    string? summaryError = null;
                    if (AiChat.HasKey)
                    {
                        StatusChanged?.Invoke("Summarizing…");
                        (summary, summaryError) = await AiChat.SummarizeAsync(transcript, CancellationToken.None);
                    }

                    var durationSec = (int)Math.Max(session.Duration.TotalSeconds, audioLength.TotalSeconds);
                    var note = new CallNote(
                        Guid.NewGuid().ToString("n"),
                        session.StartedUtc,
                        durationSec,
                        summary,
                        transcript,
                        recovered ? "recovered" : summary is null ? "transcript-only" : "ok",
                        number,
                        summary is null ? summaryError : null);
                    NotesStore.Add(note);
                    NoteReady?.Invoke(note);
                }
                catch (Exception ex)
                {
                    Log.Write($"Notes processing failed: {ex}");
                    // Transient failure (e.g. Groq unreachable, no offline
                    // model): keep the audio and retry on next launch instead
                    // of deleting the only copy. A recovered session that
                    // fails again is dropped — no infinite retry pile.
                    keepForRetry = !recovered;
                    ToastRequested?.Invoke(keepForRetry
                        ? "Notes failed — will retry on next launch"
                        : "Notes failed — see log");
                }
                finally
                {
                    if (!keepForRetry) CleanupSession(session);
                    StatusChanged?.Invoke("");
                }
            });
        }
        finally
        {
            _processQueue.Release();
        }
    }

    static void CleanupSession(RecordingSession session)
    {
        try
        {
            Directory.Delete(session.Directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Notes cleanup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        _capTimer.Stop();
        _recorder.Dispose(); // flushes writers; leftover tmp becomes a recovered note next launch
    }
}
