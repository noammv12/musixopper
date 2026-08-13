using System.IO;
using System.Windows.Threading;
using Bridget.Notes;
using Bridget.Voice;

namespace Bridget;

/// <summary>
/// Ask Bridget, push-to-talk: toggle (hotkey or dock chip) → record the
/// mic → transcribe (Groq first, local fallback) → DeepSeek decides —
/// run one of the user's saved commands, or answer, spoken out loud
/// (unless a call is being recorded or voice is off). Single-turn only.
/// </summary>
sealed class Assistant : IDisposable
{
    static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(1);
    static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(600);

    readonly DictationRecorder _recorder = new();
    readonly Speaker _speaker = new();
    readonly Func<CallState> _callState;
    readonly DispatcherTimer _cap;
    bool _busy;

    public bool IsListening { get; private set; }

    public event Action? Started;
    public event Action? Stopped;
    public event Action<string>? StatusChanged; // "" = idle
    public event Action<string>? ToastRequested;
    public event Action<string, string>? Answered; // question, answer

    public Assistant(Func<CallState> callState)
    {
        _callState = callState;
        _cap = new DispatcherTimer { Interval = MaxLength };
        _cap.Tick += (_, _) =>
        {
            _cap.Stop();
            if (IsListening) _ = StopAndActAsync();
        };
        _speaker.ToastRequested += message => ToastRequested?.Invoke(message);
        _speaker.SpeakingChanged += speaking => StatusChanged?.Invoke(speaking ? "Speaking…" : "");
    }

    public void Toggle()
    {
        // Barge-in: the hotkey mid-utterance cuts Bridget off and listens.
        if (_speaker.IsSpeaking) _speaker.Stop();
        if (_busy) return;
        if (IsListening) _ = StopAndActAsync();
        else Start();
    }

    public void Cancel()
    {
        _speaker.Stop();
        if (!IsListening) return;
        IsListening = false;
        _cap.Stop();
        TryDelete(_recorder.Stop());
        Stopped?.Invoke();
    }

    void Start()
    {
        if (Settings.DeepSeekKey is null)
        {
            ToastRequested?.Invoke("Ask Bridget needs your DeepSeek key — paste it under Notes & dictation");
            return;
        }
        if (!ChainTranscriber.Ready)
        {
            ToastRequested?.Invoke("Ask Bridget needs a Groq key or the offline voice model");
            return;
        }
        if (_recorder.Start(NotesStore.TmpDir) is null)
        {
            ToastRequested?.Invoke("Couldn't open the microphone");
            return;
        }
        IsListening = true;
        _cap.Start();
        Started?.Invoke();
    }

    async Task StopAndActAsync()
    {
        IsListening = false;
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
                ToastRequested?.Invoke("Bridget couldn't hear that — see log");
                return;
            }
            if (question.Length == 0)
            {
                ToastRequested?.Invoke("Didn't catch that");
                return;
            }

            if (Settings.DeepSeekKey is not { } key) return; // removed mid-flight
            var commands = CommandStore.Load();
            var result = await DeepSeekClient.AssistAsync(question, commands, key, CancellationToken.None);
            if (result is null)
            {
                ToastRequested?.Invoke("Bridget couldn't work that one out — try again");
                return;
            }

            if (result.Action == "open" && commands.FirstOrDefault(c => c.Id == result.CommandId) is { } command)
            {
                // The window opening is its own feedback — no TTS on opens.
                ToastRequested?.Invoke(CommandStore.Execute(command)
                    ? $"Opening {command.Label}"
                    : $"Couldn't open {command.Label} — see log");
                return;
            }

            var answer = (result.Text ?? "").Trim();
            if (answer.Length == 0)
            {
                ToastRequested?.Invoke("Bridget had no answer — try again");
                return;
            }
            Answered?.Invoke(question, answer);
            if (Settings.VoiceEnabled && _callState() != CallState.OnCall)
            {
                // Speaking isn't "busy" — clearing the flag here is what lets
                // the hotkey barge in (Toggle: stop speech, start listening).
                _busy = false;
                // Speaking during a recorded call would leak into the
                // transcript via loopback capture — screen-only then.
                await _speaker.SpeakAsync(answer);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Assistant failed: {ex}");
            ToastRequested?.Invoke("Bridget hit an error — see log");
        }
        finally
        {
            TryDelete(wav);
            TryDelete(mixed);
            StatusChanged?.Invoke("");
            _busy = false;
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
