using System.IO;
using Windows.Media.Control;

namespace Musixopper;

/// <summary>
/// Pauses and resumes system media sessions (browser tabs, Spotify, ...)
/// through the same channel the keyboard media keys use, so anything that
/// shows up in the Windows volume flyout can be controlled.
/// </summary>
static class MediaController
{
    static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musixopper", "paused-sessions.txt");

    /// <summary>Pauses every currently playing session and returns their app ids.</summary>
    public static async Task<IReadOnlyList<string>> PauseAllPlayingAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var paused = new List<string>();
        foreach (var session in manager.GetSessions())
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && await session.TryPauseAsync())
                {
                    paused.Add(session.SourceAppUserModelId);
                }
            }
            catch
            {
                // A session can vanish mid-enumeration (tab closed, app exited).
            }
        }
        return paused;
    }

    /// <summary>Resumes only the sessions in <paramref name="appIds"/> that are still paused.</summary>
    public static async Task ResumeAsync(IEnumerable<string> appIds)
    {
        var wanted = new HashSet<string>(appIds, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return;

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        foreach (var session in manager.GetSessions())
        {
            try
            {
                if (wanted.Contains(session.SourceAppUserModelId) &&
                    session.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    await session.TryPlayAsync();
                }
            }
            catch
            {
            }
        }
    }

    // CLI mode runs as short-lived processes, so what got paused has to be
    // remembered on disk between the "pause" and "resume" invocations.
    public static async Task CliPauseAsync()
    {
        var paused = await PauseAllPlayingAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        await File.WriteAllLinesAsync(StateFile, paused);
    }

    public static async Task CliResumeAsync()
    {
        if (!File.Exists(StateFile)) return;
        var ids = await File.ReadAllLinesAsync(StateFile);
        File.Delete(StateFile);
        await ResumeAsync(ids);
    }
}
