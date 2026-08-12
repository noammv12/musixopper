using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Saley.Notes;

/// <summary>Microphone-only recorder for dictation (same pattern as CallRecorder).</summary>
sealed class DictationRecorder : IDisposable
{
    WasapiCapture? _mic;
    WaveFileWriter? _writer;
    string? _path;

    public string? Start(string dir)
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
