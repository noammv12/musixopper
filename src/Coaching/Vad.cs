namespace Palon.Coaching;

/// <summary>A voiced stretch of one channel, in seconds from the channel's start.</summary>
readonly record struct Segment(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// Streaming voice-activity detector for one 16 kHz mono channel — energy +
/// spectral cues with an adaptive noise floor and hangover. No model, no
/// package: 32 ms frames through a 512-point FFT.
/// A frame is a speech candidate when its energy clears the tracked noise
/// floor by a margin (and an absolute gate, so digital silence and hiss
/// never count), most of its energy sits in the voice band (200–4000 Hz),
/// and that band is harmonic rather than flat (spectral flatness — white
/// noise is ≈1, voiced speech far lower). Onset needs 2 of 3 candidate
/// frames; hangover holds speech ~300 ms past the last candidate so breaths
/// don't split a turn; segments shorter than MinSpeech are dropped.
/// One instance per channel (state is per stream).
/// </summary>
sealed class Vad
{
    public const int SampleRate = 16000;
    public const int FrameSize = 512;
    const double FrameSec = (double)FrameSize / SampleRate;

    public double MarginDb { get; init; } = 9;
    public double AbsoluteGateDb { get; init; } = -58;
    public double MinBandRatio { get; init; } = 0.3;
    public double MaxFlatness { get; init; } = 0.35;
    public int HangoverFrames { get; init; } = 10;   // ≈320 ms
    public double MinSpeechSec { get; init; } = 0.2;

    readonly float[] _frame = new float[FrameSize];
    int _fill;
    long _frameIndex;
    double _floorDb = double.NaN;
    int _hang;
    bool _inSpeech;
    long _speechStartFrame;
    long _lastVoicedFrame;
    int _recent; // bitmask of the last 3 candidate decisions
    readonly List<Segment> _segments = new();

    static readonly double[] Window = Hann(FrameSize);
    readonly double[] _re = new double[FrameSize];
    readonly double[] _im = new double[FrameSize];

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            _frame[_fill++] = s;
            if (_fill == FrameSize)
            {
                Process();
                _fill = 0;
            }
        }
    }

    /// <summary>Closes any open segment and returns all segments.</summary>
    public List<Segment> Finish()
    {
        if (_inSpeech) Close(_lastVoicedFrame + 1);
        _inSpeech = false;
        return new List<Segment>(_segments);
    }

    /// <summary>One-shot convenience for whole buffers (tests, short clips).</summary>
    public static List<Segment> Detect(float[] samples)
    {
        var vad = new Vad();
        vad.Push(samples);
        return vad.Finish();
    }

    void Process()
    {
        double sumSq = 0;
        for (var i = 0; i < FrameSize; i++) sumSq += _frame[i] * (double)_frame[i];
        var energyDb = 10 * Math.Log10(sumSq / FrameSize + 1e-12);

        var candidate = false;
        if (energyDb > AbsoluteGateDb)
        {
            var (bandRatio, flatness) = Spectrum();
            var floor = double.IsNaN(_floorDb) ? energyDb : _floorDb;
            candidate = energyDb > floor + MarginDb && bandRatio >= MinBandRatio && flatness <= MaxFlatness;
            // The very first loud harmonic frame has no floor yet — accept it
            // on spectrum alone so a call opening mid-sentence isn't lost.
            if (double.IsNaN(_floorDb) && bandRatio >= MinBandRatio && flatness <= MaxFlatness * 0.8) candidate = true;
        }
        UpdateFloor(energyDb, candidate);

        _recent = ((_recent << 1) | (candidate ? 1 : 0)) & 0b111;
        var votes = (_recent & 1) + ((_recent >> 1) & 1) + ((_recent >> 2) & 1);

        if (!_inSpeech)
        {
            if (votes >= 2)
            {
                _inSpeech = true;
                // Back-date to the first candidate of the vote window.
                _speechStartFrame = _frameIndex - ((_recent & 0b100) != 0 ? 2 : 1);
                if (_speechStartFrame < 0) _speechStartFrame = 0;
                _lastVoicedFrame = _frameIndex;
                _hang = HangoverFrames;
            }
        }
        else if (candidate)
        {
            _lastVoicedFrame = _frameIndex;
            _hang = HangoverFrames;
        }
        else if (--_hang <= 0)
        {
            Close(_lastVoicedFrame + 1);
            _inSpeech = false;
        }
        _frameIndex++;
    }

    void Close(long endFrame)
    {
        var seg = new Segment(_speechStartFrame * FrameSec, endFrame * FrameSec);
        if (seg.Length >= MinSpeechSec) _segments.Add(seg);
    }

    /// <summary>Noise floor: falls fast to quieter frames, creeps up slowly,
    /// and barely moves during speech (so a long monologue can't become the floor).</summary>
    void UpdateFloor(double energyDb, bool speech)
    {
        if (energyDb <= -100) return; // digital silence says nothing about the room
        if (double.IsNaN(_floorDb))
        {
            _floorDb = energyDb;
            return;
        }
        double rate = energyDb < _floorDb ? 0.25 : speech || _inSpeech ? 0.0005 : 0.02;
        _floorDb += rate * (energyDb - _floorDb);
    }

    (double BandRatio, double Flatness) Spectrum()
    {
        for (var i = 0; i < FrameSize; i++)
        {
            _re[i] = _frame[i] * Window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);
        const double binHz = (double)SampleRate / FrameSize;
        int lo = (int)Math.Ceiling(200 / binHz), hi = (int)Math.Floor(4000 / binHz), dc = (int)Math.Ceiling(80 / binHz);
        double total = 0, band = 0, logSum = 0;
        var n = 0;
        for (var k = dc; k <= FrameSize / 2; k++)
        {
            var p = _re[k] * _re[k] + _im[k] * _im[k];
            total += p;
            if (k >= lo && k <= hi)
            {
                band += p;
                logSum += Math.Log(p + 1e-12);
                n++;
            }
        }
        if (total <= 0 || band <= 0 || n == 0) return (0, 1);
        var flatness = Math.Exp(logSum / n) / (band / n);
        return (band / total, flatness);
    }

    static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
