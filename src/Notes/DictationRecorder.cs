using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Palon.Voice;

namespace Palon.Notes;

/// <summary>Microphone-only recorder for dictation (same pattern as CallRecorder).</summary>
sealed class DictationRecorder : IDisposable
{
    WasapiCapture? _mic;
    WaveFileWriter? _writer;
    string? _path;

    /// <summary>Auto-stop verdict (true = speech was heard, the recording is
    /// worth processing). Raised at most once per Start, on the capture
    /// thread — marshal to the UI thread before touching state.</summary>
    public event Action<bool>? AutoStopped;

    /// <summary>Mic level in dBFS, throttled to ~every 40 ms while recording —
    /// feeds the dock's waveform. Raised on the capture thread.</summary>
    public event Action<double>? LevelDb;

    public string? Start(string dir, bool autoStop = false)
    {
        if (_mic is not null) return _path;

        WasapiCapture? mic = null;
        WaveFileWriter? writer = null;
        try
        {
            Directory.CreateDirectory(dir);
            var enumerator = new MMDeviceEnumerator();
            MMDevice device;
            try
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            mic = new WasapiCapture(device);
            var path = Path.Combine(dir, $"dictation-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");
            var target = writer = new WaveFileWriter(path, mic.WaveFormat);
            var lastFlush = DateTime.UtcNow;
            var lastLevel = DateTime.MinValue;
            var meter = autoStop ? new VoiceActivityMeter() : null;
            var meterFormat = mic.WaveFormat;
            var autoStopRaised = false;
            mic.DataAvailable += (_, e) =>
            {
                try
                {
                    target.Write(e.Buffer, 0, e.BytesRecorded);
                    if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 2)
                    {
                        lastFlush = DateTime.UtcNow;
                        target.Flush();
                    }
                    if (meterFormat.AverageBytesPerSecond > 0)
                    {
                        var db = VoiceActivityMeter.RmsDb(e.Buffer, e.BytesRecorded, meterFormat.BitsPerSample);
                        if ((DateTime.UtcNow - lastLevel).TotalMilliseconds >= 40)
                        {
                            lastLevel = DateTime.UtcNow;
                            LevelDb?.Invoke(db);
                        }
                        if (meter is not null && !autoStopRaised)
                        {
                            var ms = e.BytesRecorded * 1000.0 / meterFormat.AverageBytesPerSecond;
                            var verdict = meter.Feed(db, ms);
                            if (verdict != VoiceActivityMeter.Verdict.Continue)
                            {
                                autoStopRaised = true;
                                AutoStopped?.Invoke(verdict == VoiceActivityMeter.Verdict.StopAfterSpeech);
                            }
                        }
                    }
                }
                catch
                {
                }
            };
            mic.StartRecording();
            _mic = mic;
            _writer = writer;
            _path = path;
            Log.Write($"Dictation: recording via {device.FriendlyName}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Write($"Dictation: microphone unavailable: {ex.Message}");
            try
            {
                mic?.Dispose();
            }
            catch
            {
            }
            try
            {
                writer?.Dispose();
            }
            catch
            {
            }
            return null;
        }
    }

    /// <summary>Stops and finalizes; returns the recorded WAV path.</summary>
    public string? Stop()
    {
        var path = _path;
        _path = null;
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
                _writer?.Dispose();
            }
            catch
            {
            }
            _writer = null;
        }
        return path;
    }

    public void Dispose() => Stop();
}
