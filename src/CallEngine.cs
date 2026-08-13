using Windows.Media.Control;

namespace Saley;

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
    IReadOnlyList<GlobalSystemMediaTransportControlsSession> _pausedSessions =
        Array.Empty<GlobalSystemMediaTransportControlsSession>();
    DateTime? _resumePendingSince;
    bool _ticking;

    public TriggerMode Mode { get; private set; } = Settings.Trigger;
    public bool Enabled { get; private set; } = Settings.Enabled;
    public CallState State { get; private set; } = CallState.Idle;

    /// <summary>Caller's number for the ongoing call, when the softphone
    /// handler passed one (%NUMBER%); null otherwise and between calls.</summary>
    public string? CurrentNumber { get; private set; }

    public event Action? StateChanged;
    public event Action? MusicPaused;
    public event Action? MusicResumed;

    public void SetMode(TriggerMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        _signaledOnCall = false;
        _micWasInUse = false;
        // Switching modes mid-call: forget what we paused rather than letting
        // the next tick resume it into the ongoing call.
        _pausedForCall = false;
        _pausedSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        _resumePendingSince = null;
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
            // When both arrived within one tick the real order is unknowable:
            // already on a call, the end closed the old call and the start
            // opened a new one (stay on call); idle, a sub-tick call was
            // answered and already ended (stay idle). Both cases keep the
            // previous value.
            var endSignaled = _callEndSignal.WaitOne(0);
            var startSignaled = _callStartSignal.WaitOne(0);
            if (startSignaled && endSignaled) Log.Write($"Coalesced call signals (on call stays {_signaledOnCall})");
            else if (endSignaled) _signaledOnCall = false;
            else if (startSignaled) _signaledOnCall = true;

            // A start signal while already on a call (mic-mode answer,
            // coalesced end+start) still carries a fresh number: consume it
            // now so a stale side-channel file can't tag the NEXT call.
            if (startSignaled && State == CallState.OnCall && CurrentCall.Take() is { } freshNumber)
                CurrentNumber = freshNumber;

            if (!Enabled)
            {
                // Disabled mid-call: forget what we paused but don't resume it,
                // that would blast music into the ongoing call.
                _pausedForCall = false;
                _pausedSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
                _resumePendingSince = null;
                _signaledOnCall = false;
                _micWasInUse = false;
                SetState(CallState.Disabled);
                return;
            }

            bool onCall;
            if (Mode == TriggerMode.SoftphoneEvents)
            {
                onCall = _signaledOnCall;
            }
            else
            {
                var micInUse = MicMonitor.IsMicInUse(out _);
                // A "call answered" signal without a matching "call end"
                // shouldn't outlive the call: once the mic closes, it's over.
                if (_signaledOnCall && _micWasInUse && !micInUse) _signaledOnCall = false;
                _micWasInUse = micInUse;
                onCall = micInUse || _signaledOnCall;
            }

            if (onCall)
            {
                // Take the side-channel number before StateChanged fires, so
                // subscribers (notes) see it on the call-start transition.
                if (State != CallState.OnCall) CurrentNumber = CurrentCall.Take();
                _resumePendingSince = null;
                if (!_pausedForCall)
                {
                    _pausedForCall = true;
                    _pausedSessions = await MediaController.PauseAllPlayingSessionsAsync();
                    Log.Write($"Call started — paused {_pausedSessions.Count} session(s)");
                    if (_pausedSessions.Count > 0) MusicPaused?.Invoke();
                }
                SetState(CallState.OnCall);
            }
            else if (_pausedForCall)
            {
                _resumePendingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _resumePendingSince >= ResumeDelay)
                {
                    var toResume = _pausedSessions;
                    _pausedForCall = false;
                    _pausedSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
                    _resumePendingSince = null;
                    if (toResume.Count > 0)
                    {
                        await MediaController.ResumeSessionsAsync(toResume);
                        Log.Write($"Call ended — resumed {toResume.Count} session(s)");
                        MusicResumed?.Invoke();
                    }
                    SetState(CallState.Idle);
                }
                // else: call just ended; stay visually "on call" through the grace period.
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

    void SetState(CallState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
        // Cleared after notifying: end-of-call subscribers may still want it.
        if (state != CallState.OnCall) CurrentNumber = null;
    }

    public void Dispose()
    {
        _callStartSignal.Dispose();
        _callEndSignal.Dispose();
    }
}
