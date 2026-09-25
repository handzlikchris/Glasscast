using GlassesRemote.Server.Desktop;

namespace GlassesRemote.Server.Tests.Desktop;

public sealed class CastAreaTests
{
    [Fact]
    public void Raises_changes_once_per_new_area_and_on_clear()
    {
        var area = new CastArea();
        var seen = new List<CaptureRegion?>();
        area.Changed += seen.Add;

        area.Set(new CaptureRegion(10, 20, 600, 600));
        area.Set(new CaptureRegion(10, 20, 600, 600));
        area.Set(new CaptureRegion(40, 20, 600, 600));
        area.Set(null);
        area.Set(null);

        Assert.Equal([new CaptureRegion(10, 20, 600, 600), new CaptureRegion(40, 20, 600, 600), null], seen);
        Assert.Null(area.Current);
    }
}
