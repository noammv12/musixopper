namespace Palon.Voice;

/// <summary>
/// Energy-based endpointing: watches microphone levels and decides when the
/// user has finished talking — speech confirmed, then sustained quiet — or
/// never started. Pure state machine over (RMS dB, elapsed ms) so it's unit
/// testable; the noise floor adapts, so a humming office doesn't read as
/// speech and a quiet room doesn't need a hot mic.
/// </summary>
sealed class VoiceActivityMeter
{
    public enum Verdict
    {
        Continue,
        StopAfterSpeech,
        StopNoSpeech,
    }

    const double SpeechMarginDb = 10;    // speech = this far above the noise floor
    const double MinSpeechDb = -42;      // ...but never quieter than this
    const double FloorAdaptRate = 0.05;  // EMA toward quiet frames only
    const double MinFloorDb = -70;
    const double MaxFloorDb = -30;

    readonly double _minSpeechMs;
    readonly double _silenceHoldMs;
    readonly double _noSpeechMs;

    double _noiseFloorDb = -55;
    double _speechRunMs;
    double _silenceMs;
    double _totalMs;
    bool _speechConfirmed;

    public VoiceActivityMeter(double minSpeechMs = 250, double silenceHoldMs = 1400, double noSpeechMs = 8000)
    {
        _minSpeechMs = minSpeechMs;
        _silenceHoldMs = silenceHoldMs;
        _noSpeechMs = noSpeechMs;
    }

    public bool HadSpeech => _speechConfirmed;

    public Verdict Feed(double rmsDb, double elapsedMs)
    {
        _totalMs += elapsedMs;
        var threshold = Math.Max(_noiseFloorDb + SpeechMarginDb, MinSpeechDb);
        if (rmsDb > threshold)
        {
            _speechRunMs += elapsedMs;
            if (_speechRunMs >= _minSpeechMs)
            {
                _speechConfirmed = true;
                _silenceMs = 0;
            }
        }
        else
        {
            _speechRunMs = 0;
            _silenceMs += elapsedMs;
            _noiseFloorDb = Math.Clamp(
                _noiseFloorDb + (rmsDb - _noiseFloorDb) * FloorAdaptRate, MinFloorDb, MaxFloorDb);
        }

        if (_speechConfirmed && _silenceMs >= _silenceHoldMs) return Verdict.StopAfterSpeech;
        if (!_speechConfirmed && _totalMs >= _noSpeechMs) return Verdict.StopNoSpeech;
        return Verdict.Continue;
    }

    /// <summary>RMS level in dBFS for a capture buffer — 32-bit float or
    /// 16-bit PCM (what WASAPI mix formats actually are). Unknown formats
    /// read as silence, which just disables auto-stop gracefully.</summary>
    public static double RmsDb(byte[] buffer, int bytes, int bitsPerSample)
    {
        double sum = 0;
        var count = 0;
        if (bitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= bytes; i += 4)
            {
                double sample = BitConverter.ToSingle(buffer, i);
                sum += sample * sample;
                count++;
            }
        }
        else if (bitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= bytes; i += 2)
            {
                double sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                sum += sample * sample;
                count++;
            }
        }
        if (count == 0) return -90;
        var rms = Math.Sqrt(sum / count);
        return 20 * Math.Log10(rms + 1e-9);
    }
}
