using System.IO;

namespace Bridget;

/// <summary>Minimal size-capped file log: %LOCALAPPDATA%\Bridget\log.txt.</summary>
static class Log
{
    const long MaxBytes = 256 * 1024;
    const int RotateCheckEvery = 64;
    static readonly object Gate = new();
    static int _writesSinceCheck;

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bridget");
    static string FilePath => Path.Combine(Dir, "log.txt");

    public static void Init()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RotateIfNeeded();
            }
        }
        catch
        {
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // The cap must hold for long-running tray sessions and for CLI
                // processes that never call Init, so re-check periodically here.
                if (_writesSinceCheck++ % RotateCheckEvery == 0) RotateIfNeeded();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    static void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var old = Path.Combine(Dir, "log.old.txt");
        File.Delete(old);
        File.Move(FilePath, old);
    }
}
