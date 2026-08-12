namespace Musixopper;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            // CLI mode, for wiring into softphone call-event handlers
            // (e.g. Softphone.Pro "Incoming/Outgoing call answer" → pause,
            // "Call end" → resume).
            return args[0].Trim().ToLowerInvariant() switch
            {
                "pause" => RunCli(TraySignals.CallStartName, MediaController.CliPauseAsync),
                "resume" or "play" => RunCli(TraySignals.CallEndName, MediaController.CliResumeAsync),
                _ => 2,
            };
        }

        using var mutex = new Mutex(initiallyOwned: true, "Musixopper.Tray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayAppContext());
        return 0;
    }

    static int RunCli(string signalName, Func<Task> standalone)
    {
        // If the tray app is running, hand it the event so it keeps the
        // paused-session state (and the tray icon) in one place.
        if (EventWaitHandle.TryOpenExisting(signalName, out var signal))
        {
            using (signal) signal.Set();
            return 0;
        }

        // No tray instance: act on the media sessions directly. WinRT async
        // must not be blocked on from the STA main thread, so hop to the pool.
        try
        {
            Task.Run(standalone).GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
