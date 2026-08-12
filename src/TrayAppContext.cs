using Microsoft.Win32;

namespace Musixopper;

enum TriggerMode
{
    /// <summary>Pause whenever any app opens the microphone.</summary>
    Microphone,

    /// <summary>Pause only on explicit call-answered signals from the
    /// softphone's event handlers (via "Musixopper.exe pause/resume").</summary>
    SoftphoneEvents,
}

/// <summary>
/// The tray application: pauses playing media when a call starts and
/// resumes that same media once the call has been over for a short
/// grace period. Call start/end is detected either from microphone
/// usage or from softphone event signals, depending on the trigger mode.
/// </summary>
sealed class TrayAppContext : ApplicationContext
{
    const int PollIntervalMs = 750;
    // Grace period before resuming, so music doesn't blip on between
    // back-to-back calls or a quick redial.
    static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(2);

    const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Musixopper";
    const string SettingsKeyPath = @"SOFTWARE\Musixopper";

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly ToolStripMenuItem _status;
    readonly ToolStripMenuItem _enabled;
    readonly ToolStripMenuItem _micMode;
    readonly ToolStripMenuItem _eventsMode;
    readonly ToolStripMenuItem _startup;
    readonly EventWaitHandle _callStartSignal;
    readonly EventWaitHandle _callEndSignal;

    TriggerMode _mode;
    bool _signaledOnCall;
    bool _micWasInUse;
    bool _pausedForCall;
    IReadOnlyList<string> _pausedSessions = Array.Empty<string>();
    DateTime? _resumePendingSince;
    bool _ticking;

    public TrayAppContext()
    {
        _callStartSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TraySignals.CallStartName);
        _callEndSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TraySignals.CallEndName);

        _mode = LoadMode();

        _status = new ToolStripMenuItem("Waiting for a call…") { Enabled = false };
        _enabled = new ToolStripMenuItem("Pause music during calls") { Checked = true, CheckOnClick = true };

        _micMode = new ToolStripMenuItem("When my microphone is in use");
        _eventsMode = new ToolStripMenuItem("Only on softphone call events (answered calls)");
        _micMode.Click += (_, _) => SetMode(TriggerMode.Microphone);
        _eventsMode.Click += (_, _) => SetMode(TriggerMode.SoftphoneEvents);
        var trigger = new ToolStripMenuItem("Pause trigger");
        trigger.DropDownItems.Add(_micMode);
        trigger.DropDownItems.Add(_eventsMode);
        UpdateModeChecks();

        _startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabled);
        menu.Items.Add(trigger);
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "Musixopper — waiting for a call",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    async Task TickAsync()
    {
        if (_ticking) return; // a pause/resume may outlast one timer interval
        _ticking = true;
        try
        {
            // Drain pending softphone signals even when they end up unused,
            // so a stale signal can't fire after a mode switch or re-enable.
            if (_callStartSignal.WaitOne(0)) _signaledOnCall = true;
            if (_callEndSignal.WaitOne(0)) _signaledOnCall = false;

            if (!_enabled.Checked)
            {
                // Disabled mid-call: forget what we paused but don't resume it,
                // that would blast music into the ongoing call.
                _pausedForCall = false;
                _pausedSessions = Array.Empty<string>();
                _resumePendingSince = null;
                _signaledOnCall = false;
                _micWasInUse = false;
                SetState(TrayIcons.Disabled, "Disabled", "Musixopper — disabled");
                return;
            }

            bool onCall;
            var statusDetail = "";
            if (_mode == TriggerMode.SoftphoneEvents)
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
                if (micInUse && users.Count > 0)
                    statusDetail = $" ({Path.GetFileName(users[0])})";
            }

            if (onCall)
            {
                _resumePendingSince = null;
                if (!_pausedForCall)
                {
                    _pausedForCall = true;
                    _pausedSessions = await MediaController.PauseAllPlayingAsync();
                }
                SetState(TrayIcons.OnCall, $"On a call{statusDetail} — music paused", "Musixopper — on a call");
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
                    await MediaController.ResumeAsync(toResume);
                }
            }
            else
            {
                var idle = _mode == TriggerMode.SoftphoneEvents
                    ? "Waiting for an answered call…"
                    : "Waiting for a call…";
                SetState(TrayIcons.Idle, idle, "Musixopper — waiting for a call");
            }
        }
        catch
        {
            // Never let a transient registry/WinRT failure kill the tray loop.
        }
        finally
        {
            _ticking = false;
        }
    }

    void SetMode(TriggerMode mode)
    {
        _mode = mode;
        _signaledOnCall = false;
        _micWasInUse = false;
        UpdateModeChecks();
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
        key.SetValue("TriggerMode", mode.ToString());
    }

    void UpdateModeChecks()
    {
        _micMode.Checked = _mode == TriggerMode.Microphone;
        _eventsMode.Checked = _mode == TriggerMode.SoftphoneEvents;
    }

    static TriggerMode LoadMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
        return key?.GetValue("TriggerMode") as string == nameof(TriggerMode.SoftphoneEvents)
            ? TriggerMode.SoftphoneEvents
            : TriggerMode.Microphone;
    }

    void SetState(Icon icon, string statusText, string tooltip)
    {
        _tray.Icon = icon;
        _status.Text = statusText;
        _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63]; // NotifyIcon.Text hard limit
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (on) key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _callStartSignal.Dispose();
        _callEndSignal.Dispose();
        base.ExitThreadCore();
    }
}
