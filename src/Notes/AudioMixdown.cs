using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Bridget.Notes;

/// <summary>
/// Offline mixdown of the recorded tracks into the 16 kHz mono PCM16 WAV
/// that Whisper expects: read → mono → resample → align by recorded start
/// times → average-mix.
/// </summary>
static class AudioMixdown
{
    public static string? To16kMono(RecordingSession session)
    {
        var readers = new List<WaveFileReader>();
        try
        {
            var tracks = new List<(string Path, DateTime StartUtc)>();
            if (session.SysPath is { } sys && File.Exists(sys)) tracks.Add((sys, session.SysStartUtc));
            if (session.MicPath is { } mic && File.Exists(mic)) tracks.Add((mic, session.MicStartUtc));
            if (tracks.Count == 0) return null;

            var earliest = tracks.Min(t => t.StartUtc);
            var inputs = new List<ISampleProvider>();
            foreach (var (path, startUtc) in tracks)
            {
                WaveFileReader reader;
                try
                {
                    reader = new WaveFileReader(path);
                }
                catch (Exception ex)
                {
                    // A hard crash mid-call can leave a truncated WAV; keep the other track.
                    Log.Write($"Mixdown: unreadable track {Path.GetFileName(path)}: {ex.Message}");
                    continue;
                }
                readers.Add(reader);
                Log.Write($"Mixdown: {Path.GetFileName(path)} {reader.TotalTime.TotalSeconds:0.#}s {reader.WaveFormat}");

                ISampleProvider samples = reader.ToSampleProvider();
                if (samples.WaveFormat.Channels > 1) samples = new MonoAverageSampleProvider(samples);
                samples = new WdlResamplingSampleProvider(samples, 16000);
                var delay = startUtc - earliest;
                if (delay > TimeSpan.FromMilliseconds(50))
                    samples = new OffsetSampleProvider(samples) { DelayBy = delay };
                inputs.Add(new VolumeSampleProvider(samples) { Volume = 0.5f });
            }
            if (inputs.Count == 0) return null;

            var mixed = Path.Combine(session.Directory, "mixed.wav");
            WaveFileWriter.CreateWaveFile16(mixed, new MixingSampleProvider(inputs));
            return mixed;
        }
        catch (Exception ex)
        {
            Log.Write($"Mixdown failed: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var reader in readers)
            {
                try
                {
                    reader.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>Converts a single WAV to the 16 kHz mono PCM16 Whisper format.</summary>
    public static string? SingleTo16kMono(string wavPath, string outPath)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            ISampleProvider samples = reader.ToSampleProvider();
            if (samples.WaveFormat.Channels > 1) samples = new MonoAverageSampleProvider(samples);
            samples = new WdlResamplingSampleProvider(samples, 16000);
            WaveFileWriter.CreateWaveFile16(outPath, samples);
            return outPath;
        }
        catch (Exception ex)
        {
            Log.Write($"Mixdown (single) failed: {ex.Message}");
            return null;
        }
    }

    public static TimeSpan WavDuration(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.TotalTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Averages any channel count down to mono.</summary>
    sealed class MonoAverageSampleProvider : ISampleProvider
    {
        readonly ISampleProvider _source;
        readonly int _channels;
        float[] _buffer = Array.Empty<float>();

        public WaveFormat WaveFormat { get; }

        public MonoAverageSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            var read = _source.Read(_buffer, 0, needed);
            var frames = read / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += _buffer[frame * _channels + c];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }
}
