using System.Windows;
using System.Windows.Threading;
using Palon.Notes;
using Palon.UI;

namespace Palon;

/// <summary>
/// Wires everything together for tray mode: engine ticks, tray icon,
/// flyout, dock pill, second-instance signal, and first-run onboarding.
/// </summary>
sealed class Shell : IDisposable
{
    readonly CallEngine _engine;
    readonly TrayHost _tray;
    readonly FlyoutWindow _flyout;
    readonly DockWindow _dock;
    readonly ReminderScheduler _reminders;
    readonly CallStatsTracker _stats;
    readonly NotesPipeline _notes;
    readonly Dictation _dictation;
    readonly Assistant _assistant;
    readonly DispatcherTimer _ticker;
    readonly EventWaitHandle _showFlyoutSignal;
    readonly RegisteredWaitHandle _showFlyoutWait;
    CallState _lastEngineState = CallState.Idle;

    public Shell()
    {
        Theme.Initialize();

        _engine = new CallEngine();
        _dock = new DockWindow();
        _flyout = new FlyoutWindow(_engine);
        _tray = new TrayHost();

        _tray.OpenRequested += () => _flyout.ShowFlyout();
        _tray.NotesRequested += () => _flyout.ShowNotes();
        _tray.QuitRequested += Quit;
        _flyout.QuitRequested += Quit;
        _dock.OpenFlyoutRequested += () => _flyout.ShowSnippets();
        _dock.OpenRemindersRequested += () => _flyout.ShowReminders();
        _dock.OpenNotesRequested += () => _flyout.ShowNotes();

        // On screen before anything that can fail below — a notes/dictation/
        // update hiccup must never cost the user the dock.
        _dock.ShowDock();
        _dock.SyncState(_engine.State); // StateChanged won't fire until the state moves

        _stats = new CallStatsTracker(_engine);
        _reminders = new ReminderScheduler(_engine);
        _reminders.ReminderDue += (reminder, missed) => _dock.ShowReminder(reminder, missed);
        _dock.ReminderOpenRequested += _reminders.Open;
        _dock.ReminderSnoozeRequested += _reminders.Snooze;
        _dock.ReminderDismissRequested += _reminders.Dismiss;

        _notes = new NotesPipeline(_engine)
        {
            Transcriber = new ChainTranscriber(),
            TranscriberReady = () => ChainTranscriber.Ready,
        };
        _notes.ToastRequested += message => _dock.ShowToast(message, paused: false, showIcon: false, important: true);
        _notes.StatusChanged += status => _dock.SetNotesStatus(status);
        _notes.NoteReady += note => _dock.ShowToast(
            note.SummaryError is { } reason
                ? $"Notes ready, no summary ({reason}) — click to view"
                : note.Number is { } number ? $"Notes ready ({number}) — click to view"
                : "Notes ready — click to view",
            paused: false, onClick: () => _flyout.ShowNotes(), showIcon: false, important: true);
        try
        {
            _notes.SweepRecoveredSessions();
        }
        catch (Exception ex)
        {
            Log.Write($"Recovered-session sweep failed: {ex.Message}");
        }

        _dictation = new Dictation();
        _dictation.Started += () => _dock.SetDictation(true);
        _dictation.Stopped += () => _dock.SetDictation(false);
        _dictation.StatusChanged += status => _dock.SetDictationStatus(status);
        _dictation.Level += _dock.SetVoiceLevel;
        _dictation.ToastRequested += message => _dock.ShowToast(message, paused: false, showIcon: false, important: true);
        // One microphone mode at a time: the other hotkey is inert while a
        // session runs, so the shared pill's buttons always match the mode.
        _dock.DictationToggleRequested += () =>
        {
            if (!_assistant.IsListening) _dictation.Toggle();
            // Not important: the listening pill already says which mode holds
            // the mic — this only lands when a toast can show right now.
            else _dock.ShowToast("Palon is listening — finish that first", paused: false, showIcon: false);
        };
        _dock.DictationCancelRequested += _dictation.Cancel;
        _flyout.ApplyDictationHotkey = _dock.ApplyDictationHotkey;
        _flyout.ApplySnippetHotkeys = _dock.ApplySnippetHotkeys;
        _flyout.SuspendGlobalHotkeys = _dock.SuspendHotkeys;

        _assistant = new Assistant(() => _engine.State, () => _engine.CurrentNumber);
        _assistant.CanAutoListen = () => !_dictation.IsActive;
        _assistant.Started += () => _dock.SetAssistant(true);
        _assistant.Stopped += () => _dock.SetAssistant(false);
        _assistant.StatusChanged += status => _dock.SetAssistantStatus(status);
        _assistant.Level += _dock.SetVoiceLevel;
        _assistant.ToastRequested += message => _dock.ShowToast(message, paused: false, showIcon: false, important: true);
        _assistant.Answered += (question, answer) =>
        {
            _flyout.SetLastExchange(question, answer);
            _dock.ShowToast("Palon answered — click to read", paused: false,
                onClick: () => _flyout.ShowPalon(), showIcon: false, important: true);
        };
        _dock.AssistantToggleRequested += () =>
        {
            if (!_dictation.IsActive) _assistant.Toggle();
            else _dock.ShowToast("Finish dictating first — one mic mode at a time", paused: false, showIcon: false);
        };
        _dock.AssistantCancelRequested += _assistant.Cancel;
        _flyout.ApplyAssistantHotkey = _dock.ApplyAssistantHotkey;
        _flyout.GetLiveHotkeys = _dock.LiveHotkeys;
        _flyout.PreviewVoice = () =>
        {
            // The mic is open during dictation — a preview through the
            // speakers would be transcribed into the user's document.
            if (!_dictation.IsActive) return _assistant.PreviewVoiceAsync();
            _dock.ShowToast("Finish dictating first — the preview would get transcribed",
                paused: false, showIcon: false, important: true);
            return Task.CompletedTask;
        };

        _engine.StateChanged += () =>
        {
            var state = _engine.State;
            if (state == CallState.OnCall && _lastEngineState != CallState.OnCall) ShowCallerBrief();
            _lastEngineState = state;
            _tray.SetState(state);
            _flyout.SyncFromEngine();
            _dock.SyncState(state);
        };
        _engine.MusicPaused += () => _dock.ShowToast("Paused for your call", paused: true);
        _engine.MusicResumed += () => _dock.ShowToast("Music resumed", paused: false);

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _ticker.Tick += async (_, _) => await _engine.TickAsync();
        _ticker.Start();

        // A second launch of the exe opens this instance's flyout instantly.
        _showFlyoutSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TraySignals.ShowFlyoutName);
        _showFlyoutWait = ThreadPool.RegisterWaitForSingleObject(
            _showFlyoutSignal,
            (_, _) => Application.Current.Dispatcher.InvokeAsync(() => _flyout.ShowFlyout()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        // The dock is already up — anything from here down is best-effort
        // and must not take the Shell (and with it the dock) down.
        try
        {
            WarnAboutPredecessors();
        }
        catch (Exception ex)
        {
            Log.Write($"Predecessor check failed: {ex.Message}");
        }

        try
        {
            UpdateCheck.Run(_flyout.SetUpdateAvailable);
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed to start: {ex.Message}");
        }

        if (!Settings.OnboardingDone)
        {
            OpenFlyoutSoon(() => _flyout.ShowFlyout(onboarding: true));
        }
        else if (Settings.JustMigrated && Settings.Trigger == TriggerMode.SoftphoneEvents)
        {
            // The exe changed names — softphone handlers point at the old one.
            OpenFlyoutSoon(() =>
            {
                _flyout.ShowFlyout();
                _flyout.ShowSoftphoneSetup("Palon replaces Bridget — update your softphone handlers.");
            });
        }
    }

    /// <summary>A still-running predecessor build (Bridget/Saley) won't
    /// collide on the renamed mutex or events — it would fight over the mic,
    /// the media sessions, and the softphone handlers (which would silently
    /// kill Palon's notes). Warn when one is running, or ran recently.</summary>
    void WarnAboutPredecessors()
    {
        var runningOld = new[] { "Bridget", "Saley" }
            .FirstOrDefault(name => TryDisposeExisting($@"Local\{name}.ShowFlyout"));
        if (runningOld is not null)
        {
            _dock.ShowToast($"The old {runningOld} is still running — quit it from its tray icon",
                paused: false, showIcon: false, important: true);
        }
        else if (!Settings.JustMigrated && !Settings.OldAppWarned)
        {
            // One-shot (the migration launch already shows its own nudge, and
            // the stale log's timestamp never changes, so this would otherwise
            // re-fire on every launch for a day).
            try
            {
                // Not running now, but has it run recently? A fresh predecessor
                // log means something still launches it (autostart, handlers).
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var recent = new[] { "Bridget", "Saley" }.FirstOrDefault(name =>
                {
                    var log = System.IO.Path.Combine(local, name, "log.txt");
                    return System.IO.File.Exists(log) &&
                        System.IO.File.GetLastWriteTimeUtc(log) > DateTime.UtcNow.AddDays(-1);
                });
                if (recent is not null)
                {
                    Settings.OldAppWarned = true;
                    _dock.ShowToast($"The old {recent} ran recently — delete {recent}.exe and point your softphone handlers at Palon.exe",
                        paused: false, showIcon: false, important: true);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>When a known number calls, the dock briefs you before you say
    /// hello: how long since the last call and the next step you promised.</summary>
    void ShowCallerBrief()
    {
        if (_engine.CurrentNumber is not { } number) return;
        _ = Task.Run(() =>
        {
            try
            {
                var lastNote = NotesStore.Load()
                    .Where(n => Agent.PhoneMatch.Same(n.Number, number))
                    .MaxBy(n => n.StartedUtc);
                if (lastNote is null) return;
                _dock.ShowToast(NoteBrief.Compose(lastNote, DateTime.UtcNow),
                    paused: false, onClick: () => _flyout.ShowNotes(), showIcon: false, important: true);
            }
            catch (Exception ex)
            {
                Log.Write($"Caller brief failed: {ex.Message}");
            }
        });
    }

    static bool TryDisposeExisting(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var handle)) return false;
        handle.Dispose();
        return true;
    }

    static void OpenFlyoutSoon(Action open)
    {
        var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        once.Tick += (_, _) =>
        {
            once.Stop();
            open();
        };
        once.Start();
    }

    static void Quit() => Application.Current.Shutdown();

    public void Dispose()
    {
        _ticker.Stop();
        _assistant.Dispose();
        _dictation.Dispose();
        _notes.Dispose();
        _reminders.Dispose();
        _stats.Dispose();
        _showFlyoutWait.Unregister(null);
        _showFlyoutSignal.Dispose();
        _dock.Shutdown();
        _tray.Dispose();
        _engine.Dispose();
        Theme.Shutdown();
    }
}
