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

    /// <summary>Pauses every currently playing session and returns those session objects.</summary>
    public static async Task<IReadOnlyList<GlobalSystemMediaTransportControlsSession>> PauseAllPlayingSessionsAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var paused = new List<GlobalSystemMediaTransportControlsSession>();
        foreach (var session in manager.GetSessions())
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    && await session.TryPauseAsync())
                {
                    paused.Add(session);
                }
            }
            catch
            {
                // A session can vanish mid-enumeration (tab closed, app exited).
            }
        }
        return paused;
    }

    /// <summary>
    /// Resumes exactly the given sessions — never a sibling session of the
    /// same app that the user paused themselves — and only if still paused.
    /// </summary>
    public static async Task ResumeSessionsAsync(IEnumerable<GlobalSystemMediaTransportControlsSession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    await session.TryPlayAsync();
                }
            }
            catch
            {
                // Session died since we paused it; nothing to resume.
            }
        }
    }

    /// <summary>Resume by app id — the CLI fallback, where the pausing process is gone.</summary>
    public static async Task ResumeByAppIdAsync(IEnumerable<string> appIds)
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
        var paused = await PauseAllPlayingSessionsAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        await File.WriteAllLinesAsync(StateFile, paused.Select(s => s.SourceAppUserModelId));
    }

    public static async Task CliResumeAsync()
    {
        if (!File.Exists(StateFile)) return;
        var ids = await File.ReadAllLinesAsync(StateFile);
        File.Delete(StateFile);
        await ResumeByAppIdAsync(ids);
    }
}
