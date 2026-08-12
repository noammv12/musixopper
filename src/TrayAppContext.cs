using Microsoft.Win32;

namespace Musixopper;

/// <summary>
/// The tray application: polls the microphone state, pauses playing media
/// when a call starts (mic opens) and resumes that same media once the mic
/// has been free for a short grace period.
/// </summary>
sealed class TrayAppContext : ApplicationContext
{
    const int PollIntervalMs = 750;
    // Grace period before resuming, so music doesn't blip on between
    // back-to-back calls or a quick redial.
    static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(2);

    const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Musixopper";

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly ToolStripMenuItem _status;
    readonly ToolStripMenuItem _enabled;
    readonly ToolStripMenuItem _startup;

    bool _pausedForCall;
    IReadOnlyList<string> _pausedSessions = Array.Empty<string>();
    DateTime? _micFreeSince;
    bool _ticking;

    public TrayAppContext()
    {
        _status = new ToolStripMenuItem("Waiting for a call…") { Enabled = false };
        _enabled = new ToolStripMenuItem("Pause music during calls") { Checked = true, CheckOnClick = true };
        _startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabled);
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
            if (!_enabled.Checked)
            {
                // Disabled mid-call: forget what we paused but don't resume it,
                // that would blast music into the ongoing call.
                _pausedForCall = false;
                _pausedSessions = Array.Empty<string>();
                _micFreeSince = null;
                SetState(TrayIcons.Disabled, "Disabled", "Musixopper — disabled");
                return;
            }

            if (MicMonitor.IsMicInUse(out var users))
            {
                _micFreeSince = null;
                if (!_pausedForCall)
                {
                    _pausedForCall = true;
                    _pausedSessions = await MediaController.PauseAllPlayingAsync();
                }
                var caller = users.Count > 0 ? Path.GetFileName(users[0]) : "an app";
                SetState(TrayIcons.OnCall, $"On a call ({caller}) — music paused", "Musixopper — on a call");
            }
            else if (_pausedForCall)
            {
                _micFreeSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _micFreeSince >= ResumeDelay)
                {
                    var toResume = _pausedSessions;
                    _pausedForCall = false;
                    _pausedSessions = Array.Empty<string>();
                    _micFreeSince = null;
                    await MediaController.ResumeAsync(toResume);
                }
            }
            else
            {
                SetState(TrayIcons.Idle, "Waiting for a call…", "Musixopper — waiting for a call");
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
        base.ExitThreadCore();
    }
}
