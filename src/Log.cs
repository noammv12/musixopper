using System.IO;

namespace Musixopper;

/// <summary>Minimal size-capped file log: %LOCALAPPDATA%\Musixopper\log.txt.</summary>
static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musixopper");
    static string FilePath => Path.Combine(Dir, "log.txt");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var old = Path.Combine(Dir, "log.old.txt");
                File.Delete(old);
                File.Move(FilePath, old);
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
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
