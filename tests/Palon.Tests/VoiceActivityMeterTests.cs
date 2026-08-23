using Palon.Voice;
using Xunit;

namespace Palon.Tests;

public class VoiceActivityMeterTests
{
    const double Step = 100; // ms per frame

    static VoiceActivityMeter.Verdict Run(VoiceActivityMeter meter, params (double Db, int Ms)[] segments)
    {
        foreach (var (db, ms) in segments)
            for (var t = 0; t < ms; t += (int)Step)
            {
                var verdict = meter.Feed(db, Step);
                if (verdict != VoiceActivityMeter.Verdict.Continue) return verdict;
            }
        return VoiceActivityMeter.Verdict.Continue;
    }

    [Fact]
    public void Speech_then_sustained_quiet_ends_the_turn()
    {
        var meter = new VoiceActivityMeter();
        var verdict = Run(meter, (-60, 500), (-25, 1200), (-60, 3000));
        Assert.Equal(VoiceActivityMeter.Verdict.StopAfterSpeech, verdict);
        Assert.True(meter.HadSpeech);
    }

    [Fact]
    public void Short_pauses_inside_speech_do_not_cut_the_user_off()
    {
        var meter = new VoiceActivityMeter();
        // talk – 800 ms breath – talk: the hold (1400 ms) must survive the breath
        var verdict = Run(meter, (-25, 800), (-60, 800), (-25, 800), (-60, 800));
        Assert.Equal(VoiceActivityMeter.Verdict.Continue, verdict);
    }

    [Fact]
    public void Total_silence_times_out_as_no_speech()
    {
        var meter = new VoiceActivityMeter();
        var verdict = Run(meter, (-65, 10_000));
        Assert.Equal(VoiceActivityMeter.Verdict.StopNoSpeech, verdict);
        Assert.False(meter.HadSpeech);
    }

    [Fact]
    public void Steady_office_hum_is_noise_not_speech()
    {
        var meter = new VoiceActivityMeter();
        var verdict = Run(meter, (-45, 10_000));
        Assert.Equal(VoiceActivityMeter.Verdict.StopNoSpeech, verdict);
        Assert.False(meter.HadSpeech);
    }

    [Fact]
    public void A_click_blip_is_not_speech()
    {
        var meter = new VoiceActivityMeter();
        var verdict = Run(meter, (-60, 1000), (-20, 100), (-60, 8000));
        Assert.Equal(VoiceActivityMeter.Verdict.StopNoSpeech, verdict);
    }

    [Fact]
    public void Rms_of_16bit_full_scale_square_is_near_zero_dbfs()
    {
        var buffer = new byte[4000];
        for (var i = 0; i < buffer.Length; i += 2)
        {
            buffer[i] = 0xFF;
            buffer[i + 1] = 0x7F; // 32767
        }
        var db = VoiceActivityMeter.RmsDb(buffer, buffer.Length, 16);
        Assert.InRange(db, -0.5, 0.5);
    }

    [Fact]
    public void Rms_of_float_silence_is_deep_negative()
    {
        var buffer = new byte[4000]; // all zeros = silence
        var db = VoiceActivityMeter.RmsDb(buffer, buffer.Length, 32);
        Assert.True(db < -80);
    }
}
