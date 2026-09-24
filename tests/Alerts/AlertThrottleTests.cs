using GlassesRemote.Server.Alerts;
using Microsoft.Extensions.Time.Testing;

namespace GlassesRemote.Server.Tests.Alerts;

public sealed class AlertThrottleTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-24T12:00:00Z"));

    private Alert Probe(string ip = "198.51.100.7") => new(_time.GetUtcNow(), AlertKind.AuthenticationFailed, ip, "Session authentication failed");

    [Fact]
    public void First_alert_notifies_immediately()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(1));
        var notice = throttle.Record(Probe());

        Assert.NotNull(notice);
        Assert.Contains("198.51.100.7", notice.Text);
    }

    [Fact]
    public void Burst_is_folded_into_one_later_summary()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(1));
        Assert.NotNull(throttle.Record(Probe()));

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(throttle.Record(Probe()));
        }

        Assert.Null(throttle.Flush());
        _time.Advance(TimeSpan.FromMinutes(1));

        var summary = throttle.Flush();
        Assert.NotNull(summary);
        Assert.Contains("+49 more", summary.Title);
        Assert.Null(throttle.Flush());
    }

    [Fact]
    public void Next_alert_after_the_interval_carries_the_suppressed_count()
    {
        var throttle = new AlertThrottle(_time, TimeSpan.FromMinutes(1));
        throttle.Record(Probe());
        throttle.Record(Probe());
        throttle.Record(Probe());

        _time.Advance(TimeSpan.FromSeconds(61));
        var notice = throttle.Record(Probe());

        Assert.NotNull(notice);
        Assert.Contains("+2 more", notice.Title);
    }
}
