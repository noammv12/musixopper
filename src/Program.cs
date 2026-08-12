using Musixopper.Interop;

namespace Musixopper;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) return RunCli(args[0].Trim().ToLowerInvariant());

        using var mutex = new Mutex(initiallyOwned: true, "Musixopper.Tray.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running — open the existing instance's flyout instead.
            if (EventWaitHandle.TryOpenExisting(TraySignals.ShowFlyoutName, out var show))
                using (show) show.Set();
            return 0;
        }

        Log.Init();
        Log.Write($"Musixopper {Version} starting");
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
        typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "2.0";

    // ---- CLI mode (softphone event handlers, terminal) --------------------

    static int RunCli(string verb) => verb switch
    {
        "pause" => SignalOrRun(TraySignals.CallStartName, MediaController.CliPauseAsync),
        "resume" or "play" => SignalOrRun(TraySignals.CallEndName, MediaController.CliResumeAsync),
        "test" => RunTest(),
        _ => Usage(),
    };

    static int SignalOrRun(string signalName, Func<Task> standalone)
    {
        // If the tray app is running, hand it the event so it keeps the
        // paused-session state (and the UI) in one place.
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
        catch (Exception ex)
        {
            Log.Write($"CLI '{signalName}' failed: {ex.Message}");
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

        TryWriteLine("Musixopper isn't running — simulating the call standalone…");
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

    static int Usage()
    {
        NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        TryWriteLine("Usage: Musixopper.exe [pause | resume | test]   (no arguments starts the app)");
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
