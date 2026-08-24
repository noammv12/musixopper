using System.IO;
using System.Windows.Threading;
using Palon.Agent;
using Palon.Notes;
using Palon.Voice;

namespace Palon;

/// <summary>
/// Ask Palon, push-to-talk: toggle (hotkey or dock chip) → record the
/// mic → transcribe (Groq first, local fallback) → the agent loop decides —
/// call tools (open things, set reminders, search notes, report stats,
/// control music) and answer, spoken out loud (unless a call is being
/// recorded or voice is off). A short session memory makes follow-up
/// questions work; the v7 single-shot JSON intent stays as the fallback
/// when the tool-calling path is unavailable.
/// </summary>
sealed class Assistant : IDisposable
{
    static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(1);
    static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(600);

    readonly DictationRecorder _recorder = new();
    readonly Speaker _speaker = new();
    static readonly TimeSpan FollowUpMaxLength = TimeSpan.FromSeconds(20);

    readonly Func<CallState> _callState;
    readonly AssistantSession _session;
    readonly DispatcherTimer _cap;
    bool _busy;
    bool _followUp; // current listening round auto-opened after an answer

    public bool IsListening { get; private set; }

    /// <summary>Set by Shell: whether conversation mode may auto-open the mic
    /// right now (false while dictation holds it).</summary>
    public Func<bool>? CanAutoListen { get; set; }

    public event Action? Started;
    public event Action? Stopped;
    public event Action<string>? StatusChanged; // "" = idle
    public event Action<string>? ToastRequested;
    public event Action<string, string>? Answered; // question, answer
    /// <summary>Mic level in dBFS while listening (capture thread).</summary>
    public event Action<double>? Level;

    public Assistant(Func<CallState> callState, Func<string?> currentNumber)
    {
        _callState = callState;
        _session = new AssistantSession(callState, currentNumber);
        _cap = new DispatcherTimer { Interval = MaxLength };
        _cap.Tick += (_, _) =>
        {
            _cap.Stop();
            if (IsListening) _ = StopAndActAsync();
        };
        _speaker.ToastRequested += message => ToastRequested?.Invoke(message);
        _speaker.SpeakingChanged += speaking => StatusChanged?.Invoke(speaking ? "Speaking…" : "");
        // Raised on the capture thread — hop to the UI thread before state.
        _recorder.AutoStopped += hadSpeech => _cap.Dispatcher.InvokeAsync(() => OnAutoStopped(hadSpeech));
        _recorder.LevelDb += db => Level?.Invoke(db);
    }

    void OnAutoStopped(bool hadSpeech)
    {
        if (!IsListening) return;
        if (hadSpeech)
        {
            _ = StopAndActAsync();
            return;
        }
        // Open mic, nobody spoke: a follow-up window simply closes; a
        // deliberate press deserves to hear why nothing happened.
        var wasFollowUp = _followUp;
        Cancel();
        if (!wasFollowUp) ToastRequested?.Invoke("Didn't hear anything");
    }

    public void Toggle()
    {
        // Barge-in: the hotkey mid-utterance cuts Palon off and listens.
        if (_speaker.IsSpeaking) _speaker.Stop();
        if (_busy)
        {
            // A silent no-op reads as "the hotkey is broken".
            ToastRequested?.Invoke("Still working on the last one…");
            return;
        }
        if (IsListening) _ = StopAndActAsync();
        else Start();
    }

    /// <summary>Speaks a short sample line with the current voice settings.</summary>
    public Task PreviewVoiceAsync()
    {
        if (_callState() == CallState.OnCall)
        {
            ToastRequested?.Invoke("Preview after the call — Palon stays quiet while recording");
            return Task.CompletedTask;
        }
        return _speaker.SpeakAsync("שלום, אני פאלון — העוזר האישי שלך. לשירותך.");
    }

    public void Cancel()
    {
        _speaker.Stop();
        if (!IsListening) return;
        IsListening = false;
        _followUp = false;
        _cap.Stop();
        TryDelete(_recorder.Stop());
        Stopped?.Invoke();
    }

    void Start(bool followUp = false)
    {
        if (!AiChat.HasKey)
        {
            ToastRequested?.Invoke("Ask Palon needs a Gemini or DeepSeek key — paste one under Notes & dictation");
            return;
        }
        if (!ChainTranscriber.Ready)
        {
            ToastRequested?.Invoke("Ask Palon needs a Groq key or the offline voice model");
            return;
        }
        if (_recorder.Start(NotesStore.TmpDir, autoStop: Settings.AssistantAutoStop) is null)
        {
            ToastRequested?.Invoke("Couldn't open the microphone");
            return;
        }
        IsListening = true;
        _followUp = followUp;
        // A follow-up window is short — it self-closes if the user moved on.
        _cap.Interval = followUp ? FollowUpMaxLength : MaxLength;
        _cap.Start();
        Started?.Invoke();
    }

    async Task StopAndActAsync()
    {
        IsListening = false;
        var wasFollowUp = _followUp;
        _followUp = false;
        _cap.Stop();
        _busy = true;
        var wav = _recorder.Stop();
        Stopped?.Invoke();
        string? mixed = null;
        try
        {
            if (wav is null) return;
            StatusChanged?.Invoke("Thinking…");

            var question = await Task.Run(async () =>
            {
                mixed = AudioMixdown.SingleTo16kMono(wav, wav + ".16k.wav");
                if (mixed is null) return null;
                if (AudioMixdown.WavDuration(mixed) < MinLength) return "";
                var transcriber = new ChainTranscriber();
                return (await transcriber.TranscribeAsync(mixed, Settings.NotesLanguage, CancellationToken.None)).Trim();
            });

            if (question is null)
            {
                ToastRequested?.Invoke("Palon couldn't hear that — see log");
                return;
            }
            if (question.Length == 0)
            {
                Log.Write("Palon heard nothing usable");
                // A silent follow-up window just closes — no nagging toast.
                if (!wasFollowUp) ToastRequested?.Invoke("Didn't catch that");
                return;
            }
            Log.Write($"Palon heard: \"{question}\"");

            if (!AiChat.HasKey) return; // removed mid-flight

            var result = await AgentLoop.RunAsync(_session, question, CancellationToken.None);
            if (result.Outcome is not { } outcome)
            {
                if (result.Failure == AgentFailure.ProviderDown)
                {
                    // Tool path unusable — degrade to the v7 single-shot
                    // intent rather than to silence.
                    Log.Write("Palon agent path unavailable — falling back to single-shot intent");
                    await LegacyAssistAsync(question);
                }
                else
                {
                    // Round cap: the model already had its chances — another
                    // sequential round-trip would only add latency.
                    ToastRequested?.Invoke("Palon couldn't work that one out — try again");
                }
                return;
            }
            if (outcome.Acted)
            {
                // The action (window, music) is its own feedback — no TTS.
                ToastRequested?.Invoke(outcome.Text);
                return;
            }
            await DeliverAnswerAsync(question, outcome.Text);
        }
        catch (Exception ex)
        {
            Log.Write($"Assistant failed: {ex}");
            ToastRequested?.Invoke("Palon hit an error — see log");
        }
        finally
        {
            TryDelete(wav);
            TryDelete(mixed);
            StatusChanged?.Invoke("");
            _busy = false;
        }
    }

    /// <summary>The v7 path: one JSON-intent round-trip, no tools, no memory.
    /// Kept as the degraded mode so a provider that can't do tool calling
    /// still leaves Palon able to open things and answer.</summary>
    async Task LegacyAssistAsync(string question)
    {
        var commands = CommandStore.Load();
        var result = await AiChat.AssistAsync(question, commands, CancellationToken.None);
        if (result is null)
        {
            Log.Write("Palon intent: unusable model reply");
            ToastRequested?.Invoke("Palon couldn't work that one out — try again");
            return;
        }
        Log.Write($"Palon intent: {result.Action}"
            + (result.CommandId is { } cid ? $" id={cid}" : "")
            + (result.Url is { } u ? $" url={u}" : ""));

        if (result.Action == "open" && commands.FirstOrDefault(c => c.Id == result.CommandId) is { } command)
        {
            // The window opening is its own feedback — no TTS on opens.
            ToastRequested?.Invoke(CommandStore.Execute(command)
                ? $"Opening {command.Label}"
                : $"Couldn't open {command.Label} — see log");
            return;
        }

        if (result.Action == "open_url" && result.Url is { } url)
        {
            ToastRequested?.Invoke(OpenUrl(url) ? "Opening it" : "Couldn't open that — see log");
            return;
        }

        var answer = (result.Text ?? "").Trim();
        if (answer.Length == 0)
        {
            ToastRequested?.Invoke("Palon had no answer — try again");
            return;
        }
        await DeliverAnswerAsync(question, answer);
    }

    async Task DeliverAnswerAsync(string question, string answer)
    {
        Answered?.Invoke(question, answer);
        if (Settings.VoiceEnabled && _callState() != CallState.OnCall)
        {
            // Speaking isn't "busy" — clearing the flag here is what lets
            // the hotkey barge in (Toggle: stop speech, start listening).
            _busy = false;
            // Speaking during a recorded call would leak into the
            // transcript via loopback capture — screen-only then.
            await _speaker.SpeakAsync(answer);

            // Conversation mode: the answer just finished — reopen the mic
            // for a follow-up. Only with auto-stop on (the window must be
            // able to close itself), never over a barge-in (IsListening),
            // a new round (_busy), a call, or an active dictation.
            if (Settings.ConversationMode && Settings.AssistantAutoStop
                && !IsListening && !_busy && _callState() != CallState.OnCall
                && (CanAutoListen?.Invoke() ?? true))
            {
                Start(followUp: true);
            }
        }
    }

    static bool OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Open URL failed: {ex.Message}");
            return false;
        }
    }

    static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Cancel();
        _cap.Stop();
        _recorder.Dispose();
        _speaker.Dispose();
    }
}
