using Palon.Interop;

namespace Palon;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Trim stray quotes too — some dialer integrations pass them through.
        // The payload rejoins argv: an unquoted "+972 50-1234567" from
        // %NUMBER% arrives split across arguments.
        if (args.Length > 0)
            return RunCli(args[0].Trim().Trim('"').ToLowerInvariant(),
                args.Length > 1 ? string.Join(' ', args[1..]) : null);

        using var mutex = new Mutex(initiallyOwned: true, "Palon.Tray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running — open the existing instance's flyout instead.
            if (EventWaitHandle.TryOpenExisting(TraySignals.ShowFlyoutName, out var show))
                using (show) show.Set();
            return 0;
        }

        Log.Init();
        Log.Write($"Palon {Version} starting");
        Settings.MigrateFromPredecessors(); // must run before Shell reads Settings
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write($"Dispatcher exception: {e.Exception}");
            e.Handled = true; // keep the tray alive through UI hiccups
        };

        Shell? shell = null;
        app.Startup += (_, _) => shell = new Shell();
        app.Exit += (_, _) => shell?.Dispose();
        app.Run();
        Log.Write("Exited cleanly");
        return 0;
    }

    internal static string Version =>
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "4.0";

    // ---- CLI mode (softphone event handlers, terminal) --------------------

    static int RunCli(string verb, string? payload) => verb switch
    {
        "pause" => SignalOrRun(verb, TraySignals.CallStartName, MediaController.CliPauseAsync, payload),
        "resume" or "play" => SignalOrRun(verb, TraySignals.CallEndName, MediaController.CliResumeAsync),
        "test" => RunTest(),
        _ => Usage(verb),
    };

    static int SignalOrRun(string verb, string signalName, Func<Task> standalone, string? callerNumber = null)
    {
        // The caller's number (softphone %NUMBER% handler arg) rides a file
        // side channel — named events carry no payload. Written before the
        // signal so the engine finds it when the state flips.
        if (callerNumber is not null) CurrentCall.Set(callerNumber);

        // If the tray app is running, hand it the event so it keeps the
        // paused-session state (and the UI) in one place.
        if (EventWaitHandle.TryOpenExisting(signalName, out var signal))
        {
            using (signal) signal.Set();
            Log.Write($"CLI '{verb}' received — signaled the running app");
            return 0;
        }

        // No tray instance: act on the media sessions directly. WinRT async
        // must not be blocked on from the STA main thread, so hop to the pool.
        try
        {
            Task.Run(standalone).GetAwaiter().GetResult();
            Log.Write($"CLI '{verb}' received — handled standalone (app not running)");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"CLI '{verb}' failed: {ex.Message}");
            return 1;
        }
    }

    static int RunTest()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        var trayRunning = EventWaitHandle.TryOpenExisting(TraySignals.CallStartName, out var start);

        if (trayRunning)
        {
            TryWriteLine("Simulating an 8-second call — your music should pause now…");
            using (start) start!.Set();
            Thread.Sleep(8000);
            if (EventWaitHandle.TryOpenExisting(TraySignals.CallEndName, out var end))
                using (end) end.Set();
            TryWriteLine("Call ended — music should resume in about 2 seconds.");
            return 0;
        }

        TryWriteLine("Palon isn't running — simulating the call standalone…");
        try
        {
            Task.Run(MediaController.CliPauseAsync).GetAwaiter().GetResult();
            Thread.Sleep(8000);
            Task.Run(MediaController.CliResumeAsync).GetAwaiter().GetResult();
            TryWriteLine("Done — music paused for 8 seconds and resumed.");
            return 0;
        }
        catch (Exception ex)
        {
            TryWriteLine($"Test failed: {ex.Message}");
            return 1;
        }
    }

    static int Usage(string verb)
    {
        Log.Write($"CLI: unknown verb '{verb}'");
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        TryWriteLine("Usage: Palon.exe [pause [number] | resume | test]   (no arguments starts the app)");
        return 2;
    }

    static void TryWriteLine(string text)
    {
        try
        {
            Console.WriteLine(text);
        }
        catch
        {
        }
    }
}
