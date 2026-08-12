using System.Windows;
using System.Windows.Threading;
using Musixopper.UI;

namespace Musixopper;

/// <summary>
/// Wires everything together for tray mode: engine ticks, tray icon,
/// flyout, HUD, second-instance signal, and first-run onboarding.
/// </summary>
sealed class Shell : IDisposable
{
    readonly CallEngine _engine;
    readonly TrayHost _tray;
    readonly FlyoutWindow _flyout;
    readonly HudWindow _hud;
    readonly DispatcherTimer _ticker;
    readonly EventWaitHandle _showFlyoutSignal;
    readonly RegisteredWaitHandle _showFlyoutWait;

    public Shell()
    {
        Theme.Initialize();

        _engine = new CallEngine();
        _hud = new HudWindow();
        _flyout = new FlyoutWindow(_engine);
        _tray = new TrayHost();

        _tray.OpenRequested += () => _flyout.ShowFlyout();
        _tray.QuitRequested += Quit;
        _flyout.QuitRequested += Quit;

        _engine.StateChanged += () =>
        {
            _tray.SetState(_engine.State);
            _flyout.SyncFromEngine();
        };
        _engine.MusicPaused += () => _hud.ShowMessage("Paused for your call", paused: true);
        _engine.MusicResumed += () => _hud.ShowMessage("Music resumed", paused: false);

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _ticker.Tick += async (_, _) => await _engine.TickAsync();
        _ticker.Start();

        // A second launch of the exe (or "Musixopper.exe" from anywhere)
        // opens this instance's flyout instantly.
        _showFlyoutSignal = new EventWaitHandle(false, EventResetMode.AutoReset, TraySignals.ShowFlyoutName);
        _showFlyoutWait = ThreadPool.RegisterWaitForSingleObject(
            _showFlyoutSignal,
            (_, _) => Application.Current.Dispatcher.InvokeAsync(() => _flyout.ShowFlyout()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        if (!Settings.OnboardingDone)
        {
            var once = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            once.Tick += (_, _) =>
            {
                once.Stop();
                _flyout.ShowFlyout(onboarding: true);
            };
            once.Start();
        }
    }

    static void Quit() => Application.Current.Shutdown();

    public void Dispose()
    {
        _ticker.Stop();
        _showFlyoutWait.Unregister(null);
        _showFlyoutSignal.Dispose();
        _tray.Dispose();
        _engine.Dispose();
        Theme.Shutdown();
    }
}
