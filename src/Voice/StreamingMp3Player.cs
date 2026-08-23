using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Palon.Voice;

/// <summary>
/// Plays an MP3 byte stream while it's still arriving: chunks are parsed
/// into frames, decoded (ACM) into a BufferedWaveProvider, and WASAPI
/// playback starts as soon as ~200 ms is buffered — so Palon starts
/// talking well before the synthesis finishes. One-shot: Write chunks,
/// then FinishAsync to drain; Dispose (any thread) cuts playback for
/// barge-in.
/// </summary>
sealed class StreamingMp3Player : IDisposable
{
    static readonly TimeSpan StartThreshold = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan MaxBuffered = TimeSpan.FromMinutes(3);

    readonly object _gate = new();
    readonly MemoryStream _pending = new();
    readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _readPos;
    IMp3FrameDecompressor? _decompressor;
    BufferedWaveProvider? _provider;
    WasapiOut? _out;
    byte[] _pcm = new byte[32 * 1024];
    bool _started;
    bool _disposed;
    bool _overflowLogged;

    /// <summary>Raised (from the writer's thread) when audio actually starts.</summary>
    public event Action? PlaybackStarted;

    /// <summary>True once at least one frame decoded — i.e. there is (or was) something to play.</summary>
    public bool HasAudio
    {
        get
        {
            lock (_gate)
            {
                return _provider is not null;
            }
        }
    }

    /// <summary>Appends an MP3 chunk and decodes every complete frame in it.</summary>
    public void Write(byte[] chunk)
    {
        Action? started = null;
        lock (_gate)
        {
            if (_disposed) return;
            _pending.Position = _pending.Length;
            _pending.Write(chunk, 0, chunk.Length);
            started = DecodePendingLocked();
        }
        if (started is not null)
        {
            started();
            PlaybackStarted?.Invoke();
        }
    }

    /// <summary>Starts playback if it hasn't (short clips may never hit the
    /// threshold), waits for the buffer to drain, then stops. False when no
    /// audio was ever decoded — the caller should fall back.</summary>
    public async Task<bool> FinishAsync(CancellationToken ct)
    {
        Action? started = null;
        lock (_gate)
        {
            if (_disposed || _provider is null) return _provider is not null;
            if (!_started)
            {
                _started = true;
                started = StartOutputLocked();
            }
        }
        if (started is not null)
        {
            started();
            PlaybackStarted?.Invoke();
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_disposed || _out is null || _provider!.BufferedBytes == 0) break;
                }
                if (_stopped.Task.IsCompleted) break; // device yanked mid-play
                await Task.Delay(80, ct);
            }
            // Let WASAPI's own latency buffer (200 ms) play out.
            await Task.Delay(250, ct);
        }
        catch (OperationCanceledException)
        {
        }
        Dispose();
        return true;
    }

    /// <summary>Decodes complete frames from the pending buffer; returns the
    /// deferred start action when the threshold was just crossed.</summary>
    Action? DecodePendingLocked()
    {
        _pending.Position = _readPos;
        while (true)
        {
            var framePos = _pending.Position;
            Mp3Frame? frame;
            try
            {
                frame = Mp3Frame.LoadFromStream(_pending);
            }
            catch (EndOfStreamException)
            {
                _pending.Position = framePos; // partial frame — wait for more bytes
                break;
            }
            if (frame is null)
            {
                _pending.Position = framePos;
                break;
            }

            if (_decompressor is null)
            {
                var format = new Mp3WaveFormat(frame.SampleRate,
                    frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate);
                _decompressor = new AcmMp3FrameDecompressor(format);
                _provider = new BufferedWaveProvider(_decompressor.OutputFormat)
                {
                    BufferDuration = MaxBuffered,
                    DiscardOnBufferOverflow = true, // pathological length: drop tail, don't throw
                };
            }
            var decoded = _decompressor.DecompressFrame(frame, _pcm, 0);
            if (decoded > 0)
            {
                if (!_overflowLogged && _provider!.BufferedBytes + decoded > _provider.BufferLength)
                {
                    _overflowLogged = true;
                    Log.Write("Streaming TTS: buffer full — trimming (answer over 3 minutes?)");
                }
                _provider!.AddSamples(_pcm, 0, decoded);
            }
            _readPos = _pending.Position;
        }

        // Reclaim consumed bytes now and then so long utterances stay small.
        if (_readPos > 256 * 1024)
        {
            var remainder = _pending.GetBuffer().AsSpan((int)_readPos, (int)(_pending.Length - _readPos)).ToArray();
            _pending.SetLength(0);
            _pending.Write(remainder, 0, remainder.Length);
            _readPos = 0;
        }

        if (!_started && _provider is not null && _provider.BufferedDuration >= StartThreshold)
        {
            _started = true;
            return StartOutputLocked();
        }
        return null;
    }

    /// <summary>Creates the output under the lock but returns Play as a
    /// deferred action — Play (and PlaybackStarted) must run outside it.</summary>
    Action StartOutputLocked()
    {
        var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
        output.Init(_provider);
        output.PlaybackStopped += (_, _) => _stopped.TrySetResult();
        _out = output;
        return output.Play;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _out?.Stop();
            }
            catch
            {
            }
            try
            {
                _out?.Dispose();
            }
            catch
            {
            }
            _decompressor?.Dispose();
            _pending.Dispose();
            _out = null;
            _decompressor = null;
        }
        _stopped.TrySetResult();
    }
}
