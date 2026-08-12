namespace Musixopper;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            // CLI mode, for wiring into softphone call-event hooks
            // (e.g. Softphone.Pro "run program on call event").
            return args[0].Trim().ToLowerInvariant() switch
            {
                "pause" => RunCli(MediaController.CliPauseAsync),
                "resume" or "play" => RunCli(MediaController.CliResumeAsync),
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

    // WinRT async operations must not be blocked on from the STA main
    // thread; run them on the thread pool instead.
    static int RunCli(Func<Task> action)
    {
        try
        {
            Task.Run(action).GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
