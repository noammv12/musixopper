namespace Musixopper;

enum CallState
{
    Idle,
    OnCall,
    Disabled,
}

/// <summary>
/// The call-detection state machine, UI-free. Ticked by the shell every
/// 750 ms: drains softphone signals, checks the microphone (in Microphone
/// mode), pauses playing media when a call starts and resumes that same
/// media once the call has been over for a short grace period.
/// </summary>
sealed class CallEngine : IDisposable
{
    // Grace period before resuming, so music doesn't blip on between
    // back-to-back calls or a quick redial.
    static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(2);

    readonly EventWaitHandle _callStartSignal = new(false, EventResetMode.AutoReset, TraySignals.CallStartName);
    readonly EventWaitHandle _callEndSignal = new(false, EventResetMode.AutoReset, TraySignals.CallEndName);

    bool _signaledOnCall;
    bool _micWasInUse;
    bool _pausedForCall;
    IReadOnlyList<string> _pausedSessions = Array.Empty<string>();
    DateTime? _resumePendingSince;
    bool _ticking;

    public TriggerMode Mode { get; private set; } = Settings.Trigger;
    public bool Enabled { get; private set; } = Settings.Enabled;
    public CallState State { get; private set; } = CallState.Idle;
    public string? OnCallApp { get; private set; }

    public event Action? StateChanged;
    public event Action? MusicPaused;
    public event Action? MusicResumed;

    public void SetMode(TriggerMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        _signaledOnCall = false;
        _micWasInUse = false;
        Settings.Trigger = mode;
        Log.Write($"Trigger mode -> {mode}");
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        Settings.Enabled = enabled;
        Log.Write($"Enabled -> {enabled}");
    }

    public async Task TickAsync()
    {
        if (_ticking) return; // a pause/resume may outlast one timer interval
        _ticking = true;
        try
        {
            // Drain pending softphone signals even when they end up unused,
            // so a stale signal can't fire after a mode switch or re-enable.
            if (_callStartSignal.WaitOne(0)) _signaledOnCall = true;
            if (_callEndSignal.WaitOne(0)) _signaledOnCall = false;

            if (!Enabled)
            {
                // Disabled mid-call: forget what we paused but don't resume it,
                // that would blast music into the ongoing call.
                _pausedForCall = false;
                _pausedSessions = Array.Empty<string>();
                _resumePendingSince = null;
                _signaledOnCall = false;
                _micWasInUse = false;
                SetState(CallState.Disabled);
                return;
            }

            bool onCall;
            string? callerApp = null;
            if (Mode == TriggerMode.SoftphoneEvents)
            {
                onCall = _signaledOnCall;
            }
            else
            {
                var micInUse = MicMonitor.IsMicInUse(out var users);
                // A "call answered" signal without a matching "call end"
                // shouldn't outlive the call: once the mic closes, it's over.
                if (_signaledOnCall && _micWasInUse && !micInUse) _signaledOnCall = false;
                _micWasInUse = micInUse;
                onCall = micInUse || _signaledOnCall;
                if (micInUse && users.Count > 0) callerApp = Path.GetFileName(users[0]);
            }

            if (onCall)
            {
                _resumePendingSince = null;
                if (!_pausedForCall)
                {
                    _pausedForCall = true;
                    _pausedSessions = await MediaController.PauseAllPlayingAsync();
                    Log.Write($"Call started — paused {_pausedSessions.Count} session(s)");
                    if (_pausedSessions.Count > 0) MusicPaused?.Invoke();
                }
                SetState(CallState.OnCall, callerApp);
            }
            else if (_pausedForCall)
            {
                _resumePendingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _resumePendingSince >= ResumeDelay)
                {
                    var toResume = _pausedSessions;
                    _pausedForCall = false;
                    _pausedSessions = Array.Empty<string>();
                    _resumePendingSince = null;
                    if (toResume.Count > 0)
                    {
                        await MediaController.ResumeAsync(toResume);
                        Log.Write($"Call ended — resumed {toResume.Count} session(s)");
                        MusicResumed?.Invoke();
                    }
                    SetState(CallState.Idle);
                }
                else
                {
                    // Call just ended; stay visually "on call" through the grace period.
                    SetState(CallState.OnCall, OnCallApp);
                }
            }
            else
            {
                SetState(CallState.Idle);
            }
        }
        catch (Exception ex)
        {
            // Never let a transient registry/WinRT failure kill the loop.
            Log.Write($"Tick error: {ex.Message}");
        }
        finally
        {
            _ticking = false;
        }
    }

    void SetState(CallState state, string? callerApp = null)
    {
        if (State == state && OnCallApp == callerApp) return;
        State = state;
        OnCallApp = callerApp;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _callStartSignal.Dispose();
        _callEndSignal.Dispose();
    }
}
