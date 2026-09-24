using Palon.UI;
using Xunit;

namespace Palon.Tests;

public class DockLayoutTests
{
    [Fact]
    public void Pill_is_content_plus_border()
    {
        Assert.Equal(402, DockLayout.PillWidth(399.3, 120, 800));
    }

    [Fact]
    public void Pill_never_below_min()
    {
        Assert.Equal(160, DockLayout.PillWidth(40, 160, 800));
        Assert.Equal(160, DockLayout.PillWidth(double.NaN, 160, 800));
    }

    [Fact]
    public void Pill_clamped_to_max()
    {
        Assert.Equal(700, DockLayout.PillWidth(950, 160, 700));
    }

    [Fact]
    public void Max_width_is_window_and_work_area_bound()
    {
        Assert.Equal(800, DockLayout.MaxPillWidth(840, 40, 1920));
        Assert.Equal(600 - 2 * DockLayout.ScreenMargin, DockLayout.MaxPillWidth(840, 40, 600));
        Assert.Equal(800, DockLayout.MaxPillWidth(840, 40, 0)); // unknown screen
    }

    [Fact]
    public void Center_keeps_whole_pill_on_screen()
    {
        // 700-wide pill near the left edge of a 0..1920 work area.
        var c = DockLayout.ClampCenter(150, 700, 0, 1920, 120);
        Assert.Equal(350 + DockLayout.ScreenMargin, c);
        c = DockLayout.ClampCenter(1900, 700, 0, 1920, 120);
        Assert.Equal(1920 - 350 - DockLayout.ScreenMargin, c);
    }

    [Fact]
    public void Small_pill_uses_keep_in()
    {
        Assert.Equal(120, DockLayout.ClampCenter(10, 100, 0, 1920, 120));
        Assert.Equal(960, DockLayout.ClampCenter(960, 100, 0, 1920, 120));
    }

    [Fact]
    public void Pill_wider_than_screen_is_centered()
    {
        Assert.Equal(400, DockLayout.ClampCenter(10, 900, 0, 800, 120));
    }

    [Fact]
    public void Window_fits_pill_plus_margins_and_caps()
    {
        Assert.Equal((220.0, 70.0), DockLayout.WindowSize(180, 44, 20, 6, 840, 320));
        Assert.Equal((221.0, 71.0), DockLayout.WindowSize(180.4, 44.2, 20, 6, 840, 320));
        Assert.Equal((840.0, 320.0), DockLayout.WindowSize(2000, 900, 20, 6, 840, 320));
        Assert.Equal((40.0, 26.0), DockLayout.WindowSize(double.NaN, -5, 20, 6, 840, 320));
    }

    [Fact]
    public void Morph_span_covers_both_ends()
    {
        Assert.Equal((600.0, 48.0), DockLayout.MorphSpan(180, 48, 600, 44));
        Assert.Equal((400.0, 200.0), DockLayout.MorphSpan(400, 52, 300, 200));
    }

    [Fact]
    public void Resize_keeps_bottom_center()
    {
        var (l, t) = DockLayout.AnchorBottomCenter(100, 500, 220, 70, 640, 270);
        Assert.Equal(100 + 110 - 320, l);
        Assert.Equal(500 + 70 - 270, t);
        Assert.Equal(100 + 110, l + 640 / 2.0);
    }
}
