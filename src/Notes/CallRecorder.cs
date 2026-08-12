using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Saley.Notes;

sealed class RecordingSession
{
    public required string Directory { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; set; }
    public string? MicPath { get; set; }
    public DateTime MicStartUtc { get; set; }
    public string? SysPath { get; set; }
    public DateTime SysStartUtc { get; set; }
    public bool MicUnavailable { get; set; }

    public TimeSpan Duration => (EndedUtc ?? DateTime.UtcNow) - StartedUtc;
}

/// <summary>
/// Records the call: default microphone + system loopback (the far end's
/// voice comes out of the speaker/headset; music is paused by the app
/// anyway). Each track goes to its own WAV in the session's tmp directory.
/// A silent playback keeps the loopback pumping through digital silence —
/// WASAPI loopback otherwise delivers no data at all while nothing plays.
/// </summary>
sealed class CallRecorder : IDisposable
{
    WasapiCapture? _mic;
    WaveFileWriter? _micWriter;
    WasapiLoopbackCapture? _sys;
    WaveFileWriter? _sysWriter;
    WasapiOut? _keepAlive;
    RecordingSession? _session;

    public bool IsRecording => _session is not null;

    public RecordingSession? Start(string tmpBaseDir)
    {
        if (_session is not null) return _session;

        var dir = Path.Combine(tmpBaseDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder: cannot create session dir: {ex.Message}");
            return null;
        }
        var session = new RecordingSession { Directory = dir, StartedUtc = DateTime.UtcNow };

        var enumerator = new MMDeviceEnumerator();

        try
        {
            var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sys = new WasapiLoopbackCapture(render);
            var path = Path.Combine(dir, "sys.wav");
            var writer = new WaveFileWriter(path, sys.WaveFormat);
            sys.DataAvailable += (_, e) =>
            {
                try
                {
                    writer.Write(e.Buffer, 0, e.BytesRecorded);
                }
                catch
                {
                    // Writer already closed (stop raced a late buffer).
                }
            };
            sys.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Write($"Recorder: system track stopped early: {e.Exception.Message}");
            };

            var keepAlive = new WasapiOut(render, AudioClientShareMode.Shared, true, 200);
            keepAlive.Init(new SilenceProvider(sys.WaveFormat));
            keepAlive.Play();

            sys.StartRecording();
            _sys = sys;
            _sysWriter = writer;
            _keepAlive = keepAlive;
            session.SysPath = path;
            session.SysStartUtc = DateTime.UtcNow;
            Log.Write($"Recorder: system audio via {render.FriendlyName}");
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder: loopback unavailable: {ex.Message}");
            DisposeSystemTrack();
        }

        try
        {
            MMDevice micDevice;
            try
            {
                micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            var mic = new WasapiCapture(micDevice);
            var path = Path.Combine(dir, "mic.wav");
            var writer = new WaveFileWriter(path, mic.WaveFormat);
            mic.DataAvailable += (_, e) =>
            {
                try
                {
                    writer.Write(e.Buffer, 0, e.BytesRecorded);
                }
                catch
                {
                }
            };
            mic.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Write($"Recorder: mic track stopped early: {e.Exception.Message}");
            };
            mic.StartRecording();
            _mic = mic;
            _micWriter = writer;
            session.MicPath = path;
            session.MicStartUtc = DateTime.UtcNow;
            Log.Write($"Recorder: microphone via {micDevice.FriendlyName}");
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder: microphone unavailable: {ex.Message}");
            session.MicUnavailable = true;
        }

        if (_sys is null && _mic is null)
        {
            TryDeleteDir(dir);
            return null;
        }

        _session = session;
        return session;
    }

    /// <summary>Stops capture and finalizes the WAVs; returns the finished session.</summary>
    public RecordingSession? Stop()
    {
        var session = _session;
        _session = null;
        DisposeMicTrack();
        DisposeSystemTrack();
        if (session is not null) session.EndedUtc = DateTime.UtcNow;
        return session;
    }

    void DisposeMicTrack()
    {
        try
        {
            _mic?.StopRecording();
            _mic?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _mic = null;
            try
            {
                _micWriter?.Dispose(); // finalizes the RIFF header
            }
            catch
            {
            }
            _micWriter = null;
        }
    }

    void DisposeSystemTrack()
    {
        try
        {
            _sys?.StopRecording();
            _sys?.Dispose();
        }
        catch
        {
        }
        try
        {
            _keepAlive?.Stop();
            _keepAlive?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _sys = null;
            _keepAlive = null;
            try
            {
                _sysWriter?.Dispose();
            }
            catch
            {
            }
            _sysWriter = null;
        }
    }

    static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    public void Dispose() => Stop();
}
