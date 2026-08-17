using System.Windows;
using System.Windows.Threading;
using Bridget.Notes;
using Bridget.UI;

namespace Bridget;

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
            note.Summary is null && AiChat.HasKey
                ? $"Notes ready, no summary ({AiChat.LastError ?? "AI failed"}) — click to view"
                : note.Number is { } number ? $"Notes ready ({number}) — click to view"
                : "Notes ready — click to view",
            paused: false, onClick: () => _flyout.ShowNotes(), showIcon: false, important: true);
        _notes.SweepRecoveredSessions();

        _dictation = new Dictation();
        _dictation.Started += () => _dock.SetDictation(true);
        _dictation.Stopped += () => _dock.SetDictation(false);
        _dictation.StatusChanged += status => _dock.SetDictationStatus(status);
        _dictation.ToastRequested += message => _dock.ShowToast(message, paused: false, showIcon: false, important: true);
        // One microphone mode at a time: the other hotkey is inert while a
        // session runs, so the shared pill's buttons always match the mode.
        _dock.DictationToggleRequested += () =>
        {
            if (!_assistant.IsListening) _dictation.Toggle();
        };
        _dock.DictationCancelRequested += _dictation.Cancel;
        _flyout.ApplyDictationHotkey = _dock.ApplyDictationHotkey;
        _flyout.ApplySnippetHotkeys = _dock.ApplySnippetHotkeys;
        _flyout.SuspendGlobalHotkeys = _dock.SuspendHotkeys;

        _assistant = new Assistant(() => _engine.State);
        _assistant.Started += () => _dock.SetAssistant(true);
        _assistant.Stopped += () => _dock.SetAssistant(false);
        _assistant.StatusChanged += status => _dock.SetAssistantStatus(status);
        _assistant.ToastRequested += message => _dock.ShowToast(message, paused: false, showIcon: false, important: true);
        _assistant.Answered += (question, answer) =>
        {
            _flyout.SetLastExchange(question, answer);
            _dock.ShowToast("Bridget answered — click to read", paused: false,
                onClick: () => _flyout.ShowBridget(), showIcon: false, important: true);
        };
        _dock.AssistantToggleRequested += () =>
        {
            if (!_dictation.IsActive) _assistant.Toggle();
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
            _tray.SetState(_engine.State);
            _flyout.SyncFromEngine();
            _dock.SyncState(_engine.State);
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

        _dock.ShowDock();
        _dock.SyncState(_engine.State); // StateChanged won't fire until the state moves

        // A still-running Saley build won't collide on the renamed mutex or
        // events — it would fight over the mic, the media sessions, and the
        // softphone handlers (which would silently kill Bridget's notes).
        if (EventWaitHandle.TryOpenExisting(@"Local\Saley.ShowFlyout", out var oldApp))
        {
            oldApp.Dispose();
            _dock.ShowToast("The old Saley is still running — quit it from its tray icon",
                paused: false, showIcon: false, important: true);
        }
        else
        {
            try
            {
                // Not running now, but has it run recently? A fresh Saley log
                // means something still launches it (autostart, handlers).
                var saleyLog = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Saley", "log.txt");
                if (System.IO.File.Exists(saleyLog) &&
                    System.IO.File.GetLastWriteTimeUtc(saleyLog) > DateTime.UtcNow.AddDays(-1))
                {
                    _dock.ShowToast("The old Saley ran recently — delete Saley.exe and point your softphone handlers at Bridget.exe",
                        paused: false, showIcon: false, important: true);
                }
            }
            catch
            {
            }
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
                _flyout.ShowSoftphoneSetup("Bridget replaces Saley — update your softphone handlers.");
            });
        }
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
