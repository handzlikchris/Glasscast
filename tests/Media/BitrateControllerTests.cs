using GlassesRemote.Server.Media;

namespace GlassesRemote.Server.Tests.Media;

/// <summary>The target follows the glasses' loss and bandwidth estimate, within bounds.</summary>
public sealed class BitrateControllerTests
{
    private static BitrateController Busy(int start = 1000)
    {
        var controller = new BitrateController(minKbps: 200, maxKbps: 2500, startKbps: start);
        controller.OnSent(start); // the stream uses its whole target
        return controller;
    }

    [Fact]
    public void Heavy_loss_cuts_by_half_the_loss()
    {
        var controller = Busy();
        controller.OnFeedback(new ReceiverFeedback(0.2, null));
        Assert.Equal(900, controller.TargetKbps);
    }

    [Fact]
    public void A_clean_busy_link_climbs_to_the_max_but_a_still_screen_doesnt()
    {
        var busy = Busy();
        for (var i = 0; i < 30; i++)
        {
            busy.OnSent(busy.TargetKbps);
            busy.OnFeedback(new ReceiverFeedback(0, null));
        }
        Assert.Equal(2500, busy.TargetKbps);

        var still = Busy();
        still.OnSent(80);
        still.OnFeedback(new ReceiverFeedback(0, null));
        Assert.Equal(1000, still.TargetKbps);
    }

    [Fact]
    public void Some_loss_holds_the_target()
    {
        var controller = Busy();
        controller.OnFeedback(new ReceiverFeedback(0.05, null));
        Assert.Equal(1000, controller.TargetKbps);
    }

    [Fact]
    public void Remb_is_recorded_but_not_acted_on()
    {
        var controller = Busy();
        controller.OnFeedback(new ReceiverFeedback(null, 300));
        Assert.Equal(1000, controller.TargetKbps);
        Assert.Equal((300, (double?)null), controller.LastFeedback);
    }

    [Fact]
    public void Feedback_can_be_recorded_without_adapting()
    {
        var controller = Busy();
        controller.OnFeedback(new ReceiverFeedback(0.5, 900), adapt: false);
        Assert.Equal(1000, controller.TargetKbps);
        Assert.Equal((900, (double?)0.5), controller.LastFeedback);
    }

    [Fact]
    public void Stays_within_bounds()
    {
        var controller = Busy(start: 300);
        for (var i = 0; i < 10; i++)
        {
            controller.OnFeedback(new ReceiverFeedback(0.9, null));
        }
        Assert.Equal(200, controller.TargetKbps);
    }
}
