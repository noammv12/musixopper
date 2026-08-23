using System.IO;
using System.Windows.Threading;
using Palon.Notes;

namespace Palon;

/// <summary>
/// Wispr-Flow-style dictation: toggle (hotkey or dock chip) → record the
/// mic → transcribe (Groq first, local fallback) → type the text into
/// whatever app has focus. The dock never activates, so the target app's
/// caret is exactly where the user left it.
/// </summary>
sealed class Dictation : IDisposable
{
    static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);
    static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(600);

    readonly DictationRecorder _recorder = new();
    readonly DispatcherTimer _cap;
    bool _busy;

    public bool IsActive { get; private set; }

    public event Action? Started;
    public event Action? Stopped;
    public event Action<string>? StatusChanged; // "" = idle
    public event Action<string>? ToastRequested;

    public Dictation()
    {
        _cap = new DispatcherTimer { Interval = MaxLength };
        _cap.Tick += (_, _) =>
        {
            _cap.Stop();
            if (IsActive) _ = StopAndTypeAsync();
        };
    }

    public void Toggle()
    {
        if (_busy) return;
        if (IsActive) _ = StopAndTypeAsync();
        else Start();
    }

    public void Cancel()
    {
        if (!IsActive) return;
        IsActive = false;
        _cap.Stop();
        TryDelete(_recorder.Stop());
        Stopped?.Invoke();
    }

    void Start()
    {
        if (!ChainTranscriber.Ready)
        {
            ToastRequested?.Invoke("Dictation needs a Groq key or the offline model");
            return;
        }
        if (_recorder.Start(NotesStore.TmpDir) is null)
        {
            ToastRequested?.Invoke("Couldn't open the microphone");
            return;
        }
        IsActive = true;
        _cap.Start();
        Started?.Invoke();
    }

    async Task StopAndTypeAsync()
    {
        IsActive = false;
        _cap.Stop();
        _busy = true;
        var wav = _recorder.Stop();
        Stopped?.Invoke();
        string? mixed = null;
        try
        {
            if (wav is null) return;
            StatusChanged?.Invoke("Typing it out…");

            var text = await Task.Run(async () =>
            {
                mixed = AudioMixdown.SingleTo16kMono(wav, wav + ".16k.wav");
                if (mixed is null) return null;
                if (AudioMixdown.WavDuration(mixed) < MinLength) return "";
                var transcriber = new ChainTranscriber();
                return (await transcriber.TranscribeAsync(mixed, Settings.NotesLanguage, CancellationToken.None)).Trim();
            });

            if (text is null)
            {
                ToastRequested?.Invoke("Dictation failed — see log");
                return;
            }
            if (text.Length == 0)
            {
                ToastRequested?.Invoke("Didn't catch that");
                return;
            }

            if (Settings.DictationPolish && AiChat.HasKey)
            {
                StatusChanged?.Invoke("Polishing…");
                var polished = await AiChat.PolishAsync(text, Settings.DictationProfessional, CancellationToken.None);
                // Null (API failure, text too long) keeps the raw transcript —
                // the dictation itself is never lost to the polish step.
                if (!string.IsNullOrWhiteSpace(polished)) text = polished;
            }

            // Back on the UI thread here (clipboard needs STA).
            var result = await SnippetPaster.PasteTextAsync(text);
            if (result == PasteResult.CopiedOnly) ToastRequested?.Invoke("Dictation copied — press Ctrl+V");
            else if (result == PasteResult.Failed) ToastRequested?.Invoke("Dictation couldn't paste — see log");
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation failed: {ex}");
            ToastRequested?.Invoke("Dictation failed — see log");
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
    }
}
