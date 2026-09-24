using Palon;
using Palon.UI;
using Xunit;

public class PerfTests
{
    [Fact]
    public void Pacer_PausesWhenMinimizedOrZeroSize()
    {
        Assert.Equal(0, AvatarPacer.FrameInterval(180, busy: true, windowActive: true, windowMinimized: true));
        Assert.Equal(0, AvatarPacer.FrameInterval(0, true, true, false));
    }

    [Fact]
    public void Pacer_60fpsOnlyForLargeBusyInActiveWindow()
    {
        Assert.Equal(AvatarPacer.Fps60, AvatarPacer.FrameInterval(180, true, true, false));
        Assert.Equal(AvatarPacer.Fps30, AvatarPacer.FrameInterval(30, true, true, false));
        Assert.Equal(AvatarPacer.Fps30, AvatarPacer.FrameInterval(180, true, false, false));
    }

    [Fact]
    public void Pacer_IdleIsSlower()
    {
        Assert.Equal(AvatarPacer.Fps30, AvatarPacer.FrameInterval(180, false, true, false));
        Assert.Equal(AvatarPacer.Fps20, AvatarPacer.FrameInterval(30, false, true, false));
        Assert.Equal(AvatarPacer.Fps12, AvatarPacer.FrameInterval(180, false, false, false));
    }

    [Fact]
    public void Pacer_SharedIntervalIgnoresPaused()
    {
        Assert.Equal(0, AvatarPacer.SharedInterval(new double[] { 0, 0 }));
        Assert.Equal(AvatarPacer.Fps30, AvatarPacer.SharedInterval(new[] { 0, AvatarPacer.Fps12, AvatarPacer.Fps30 }));
    }

    [Fact]
    public void Pacer_DueHasJitterSlack()
    {
        Assert.True(AvatarPacer.Due(1.030, 1.0, AvatarPacer.Fps30));
        Assert.False(AvatarPacer.Due(1.010, 1.0, AvatarPacer.Fps30));
        Assert.False(AvatarPacer.Due(5, 1.0, 0));
    }

    [Fact]
    public void LazyRegistry_BuildsOnceOnFirstUse()
    {
        int built = 0;
        var r = new LazyRegistry<string, object>();
        r.Register("a", () => { built++; return new object(); });
        Assert.False(r.IsBuilt("a"));
        Assert.Equal(0, built);
        var first = r["a"];
        Assert.Same(first, r.Get("a"));
        Assert.Equal(1, built);
        Assert.True(r.TryGetBuilt("a", out _));
        Assert.Throws<KeyNotFoundException>(() => r["missing"]);
    }

    [Fact]
    public void LazyRegistry_SetIsPrebuilt()
    {
        var r = new LazyRegistry<int, string>();
        r.Set(1, "now");
        Assert.True(r.IsBuilt(1));
        Assert.Equal(1, r.BuiltCount);
        Assert.Equal("now", r[1]);
    }

    [Fact]
    public void Perf_FormatAndLastLines()
    {
        var line = Perf.Format(200L << 20, 300L << 20, 50L << 20, 60L << 20, 10, 5, 2, 3, true, 30);
        Assert.Contains("ws=200.0MB", line);
        Assert.Contains("gc2=2", line);
        Assert.Contains("avatars=3 avatarClock=on", line);
        var lines = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? $"x {Perf.Tag} {i}" : "other").ToList();
        var last = Perf.LastLines(lines, 3);
        Assert.Equal(3, last.Count);
        Assert.EndsWith("18", last[^1]);
    }
}
