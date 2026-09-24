using Palon.UI;
using Xunit;

namespace Palon.Tests;

public class PalonEngineTests
{
    static PalonMood[] AllMoods => Enum.GetValues<PalonMood>();

    static void AssertFinite(PalonFrame f)
    {
        foreach (var arr in new[] { f.Body, f.EyeL, f.EyeR, f.Hair })
            foreach (var v in arr) Assert.True(double.IsFinite(v), "non-finite coordinate");
    }

    [Fact]
    public void Sample_IsDeterministic()
    {
        foreach (var m in AllMoods)
        {
            PalonFrame a = new(), b = new();
            PalonEngine.Sample(new PalonState(m) { Seed = 1.7 }, 12.345, a);
            PalonEngine.Sample(new PalonState(m) { Seed = 1.7 }, 12.345, b);
            Assert.Equal(a.Body, b.Body); Assert.Equal(a.EyeL, b.EyeL); Assert.Equal(a.EyeR, b.EyeR); Assert.Equal(a.Hair, b.Hair);
        }
    }

    [Fact]
    public void Sample_NeverProducesNaN_AcrossMoodsTransitionsAndMotionLevels()
    {
        var f = new PalonFrame();
        foreach (var motion in new[] { 1.0, 0.0 })
            foreach (var from in AllMoods)
                foreach (var to in AllMoods)
                {
                    var s = new PalonState(from, 0) { Seed = 4.6 };
                    var x = new PalonExtras { Motion = motion, HairWidth = 1.3, GazeYaw = 14, GazePitch = -10 };
                    for (double t = 0; t < 30; t += 0.037)
                    {
                        if (Math.Abs(t - 3) < 0.02) PalonEngine.SetMood(s, to, t, motion);
                        if (Math.Abs(t - 3.2) < 0.02) PalonEngine.SetMood(s, from, t, motion); // interrupt mid-morph
                        PalonEngine.Sample(s, t, f, 100, x);
                        AssertFinite(f);
                    }
                }
    }

    [Fact]
    public void Sample_StaysFiniteAtLargeClockValues()
    {
        var f = new PalonFrame();
        foreach (var m in AllMoods)
            foreach (var t in new[] { 3_999.99, 86_400.5, 1e7 })
            {
                PalonEngine.Sample(new PalonState(m, t - 1), t, f);
                AssertFinite(f);
            }
    }

    [Fact]
    public void InterruptedMorph_DoesNotJump()
    {
        // Switching mood mid-morph must continue from the pose on screen: consecutive frames stay close.
        var s = new PalonState(PalonMood.Idle, 0); var f = new PalonFrame(); double[]? prev = null; double worst = 0;
        for (double t = 0; t < 4; t += 1.0 / 120)
        {
            if (t >= 1.0 && s.Cur == PalonMood.Idle) PalonEngine.SetMood(s, PalonMood.Think, t);
            if (t >= 1.2 && s.Cur == PalonMood.Think) PalonEngine.SetMood(s, PalonMood.Happy, t);
            if (t >= 1.3 && s.Cur == PalonMood.Happy) PalonEngine.SetMood(s, PalonMood.Listen, t);
            PalonEngine.Sample(s, t, f);
            if (prev != null)
                for (int i = 0; i < f.Body.Length; i++) worst = Math.Max(worst, Math.Abs(f.Body[i] - prev[i]));
            prev = (double[])f.Body.Clone();
        }
        Assert.True(worst < 3, $"body jumped {worst:F2} units in one 120 Hz frame");
    }

    [Fact]
    public void SettledMorph_EqualsTargetMood()
    {
        PalonFrame a = new(), b = new();
        var s = new PalonState(PalonMood.Idle, 0);
        PalonEngine.SetMood(s, PalonMood.Talk, 1);
        PalonEngine.Sample(s, 1 + PalonEngine.Morph + 2, a);
        // same mood, same local time since it started, no transition
        var direct = new PalonState(PalonMood.Talk, 1) { BlinkAt = s.BlinkAt };
        PalonEngine.Sample(direct, 1 + PalonEngine.Morph + 2, b);
        Assert.Equal(a.EyeL, b.EyeL);
    }

    [Fact]
    public void MatchesWebEngine_GoldenFrame()
    {
        // Golden output of docs/design/engine.js: create('idle',0), seed 1.7, setMood think@2, happy@2.2 (interrupting), sample(2.3).
        var s = new PalonState(PalonMood.Idle, 0) { Seed = 1.7 };
        PalonEngine.SetMood(s, PalonMood.Think, 2);
        PalonEngine.SetMood(s, PalonMood.Happy, 2.2);
        var f = new PalonFrame();
        PalonEngine.Sample(s, 2.3, f);
        Assert.Equal("M-9.77 -79.28Q-8.14 -95.9 1.35 -112Q8.11 -100.9 6.47 -84.28ZM-6.62 -82.53Q4.28 -104.29 23.26 -121.29Q23.23 -102.79 12.32 -81.03ZM0.62 -85.09Q12.27 -95.3 28.62 -100.29Q25.73 -88.68 14.08 -78.47Z",
            PalonEngine.HairPath(f.Hair));
        var d = PalonEngine.Catmull(f.Body);
        Assert.Equal(PalonEngine.N, d.Count(c => c == 'C'));
        Assert.Equal(PalonEngine.EP, PalonEngine.Catmull(f.EyeL).Count(c => c == 'C'));
    }

    [Fact]
    public void ReducedMotion_IdleIsStill()
    {
        PalonFrame a = new(), b = new();
        var x = new PalonExtras { Motion = 0, HairWidth = 1 };
        var s = new PalonState(PalonMood.Idle, 0) { Seed = 0 };
        // pick two times away from any blink
        double t1 = 100, t2 = 0;
        for (double t = 100.5; t < 200; t += 0.5)
            if (!PalonEngine.IsBlinking(s, t) && !PalonEngine.IsBlinking(s, t1)) { t2 = t; break; }
        PalonEngine.Sample(s, t1, a, 100, x);
        PalonEngine.Sample(s, t2, b, 100, x);
        Assert.Equal(a.Body, b.Body); Assert.Equal(a.EyeL, b.EyeL); Assert.Equal(a.Hair, b.Hair);
    }
}
