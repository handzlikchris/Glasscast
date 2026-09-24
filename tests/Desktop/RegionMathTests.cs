using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Tests.Desktop;

public sealed class RegionMathTests
{
    private static readonly PixelSize Monitor = new(2560, 1440);

    [Fact]
    public void Clamp_keeps_the_region_on_the_monitor()
    {
        var r = RegionMath.Clamp(new CaptureRegion(2400, 1300, 600, 600), Monitor);
        Assert.Equal(new CaptureRegion(1960, 840, 600, 600), r);
    }

    [Fact]
    public void Clamp_enforces_min_and_max_size()
    {
        Assert.Equal(new CaptureRegion(0, 0, 2560, 1440), RegionMath.Clamp(new CaptureRegion(-50, -50, 99999, 99999), Monitor));

        var tiny = RegionMath.Clamp(new CaptureRegion(10, 10, 5, 5), Monitor);
        Assert.Equal(RegionMath.MinRegionSize, tiny.Width);
        Assert.Equal(RegionMath.MinRegionSize, tiny.Height);
    }

    [Fact]
    public void Default_is_a_centred_square()
    {
        var r = RegionMath.Default(Monitor);
        Assert.Equal(720, r.Width);
        Assert.Equal(720, r.Height);
        Assert.Equal((2560 - 720) / 2, r.X);
        Assert.Equal((1440 - 720) / 2, r.Y);
    }

    [Theory]
    [InlineData(0, 0, 100, 200)]
    [InlineData(1, 1, 699, 799)]
    [InlineData(0.5, 0.5, 400, 500)]
    [InlineData(-5, 9, 100, 799)]
    [InlineData(double.NaN, 0, 400, 200)]
    public void ToScreen_always_lands_inside_the_region(double nx, double ny, int x, int y)
    {
        var region = new CaptureRegion(100, 200, 600, 600);
        Assert.Equal((x, y), RegionMath.ToScreen(region, nx, ny));
    }

    [Fact]
    public void Fit_letterboxes_wide_sources()
    {
        Assert.Equal(new PixelRect(0, 131, 600, 338), RegionMath.Fit(new PixelSize(2560, 1440), new PixelSize(600, 600)));
        Assert.Equal(new PixelRect(0, 0, 600, 600), RegionMath.Fit(new PixelSize(800, 800), new PixelSize(600, 600)));
    }
}
