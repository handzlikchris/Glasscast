using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using GlassesRemote.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GlassesRemote.Server.Tests.Sessions;

public sealed class InputControllerTests : IDisposable
{
    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"region-{Guid.NewGuid():N}.json");
    private readonly FakeInput _input = new();
    private readonly InputController _controller;

    public InputControllerTests()
    {
        var store = new RegionStore(_storePath, NullLogger<RegionStore>.Instance);
        _controller = new InputController(_input, store, new PixelSize(2560, 1440), new CaptureRegion(100, 200, 600, 600));
    }

    public void Dispose() => File.Delete(_storePath);

    [Fact]
    public void Starts_in_view_mode_and_ignores_input_there()
    {
        Assert.Equal(ViewMode.View, _controller.Mode);
        Assert.Equal(HandleResult.IgnoredForMode, _controller.Handle(new MoveMessage(0.5, 0.5)));
        Assert.Equal(HandleResult.IgnoredForMode, _controller.Handle(new ClickMessage(MouseButton.Left)));
        Assert.Equal(HandleResult.IgnoredForMode, _controller.Handle(new TypeTextMessage("hello")));
        Assert.Empty(_input.Actions);
    }

    [Fact]
    public void Pointer_mode_maps_moves_into_the_region_and_clicks_there()
    {
        _controller.Handle(new SetModeMessage(ViewMode.Pointer));
        _controller.Handle(new MoveMessage(0, 0));
        _controller.Handle(new MoveMessage(1, 1));
        _controller.Handle(new ClickMessage(MouseButton.Left));

        Assert.Equal(["move 100,200", "move 699,799", "click Left"], _input.Actions);
    }

    [Fact]
    public void Click_without_a_prior_move_lands_in_the_region_centre()
    {
        _controller.Handle(new SetModeMessage(ViewMode.Pointer));
        _controller.Handle(new ClickMessage(MouseButton.Left));

        Assert.Equal(["move 400,500", "click Left"], _input.Actions);
    }

    [Fact]
    public void Scroll_mode_inverts_to_windows_wheel_direction()
    {
        _controller.Handle(new SetModeMessage(ViewMode.Scroll));
        _controller.Handle(new ScrollMessage(240));

        Assert.Equal(["move 400,500", "wheel -240"], _input.Actions);
    }

    [Fact]
    public void Type_mode_types_and_presses_allowlisted_keys_only_there()
    {
        _controller.Handle(new SetModeMessage(ViewMode.Pointer));
        Assert.Equal(HandleResult.IgnoredForMode, _controller.Handle(new KeyMessage(KeyCommand.Enter)));

        _controller.Handle(new SetModeMessage(ViewMode.Type));
        _controller.Handle(new TypeTextMessage("run the tests"));
        _controller.Handle(new KeyMessage(KeyCommand.Enter));

        Assert.Equal(["type run the tests", "key Enter"], _input.Actions);
    }

    [Fact]
    public void Set_region_is_clamped_saved_and_used_for_new_moves()
    {
        _controller.Handle(new SetModeMessage(ViewMode.Pointer));
        Assert.Equal(HandleResult.RegionChanged, _controller.Handle(new SetRegionMessage(2500, -10, 800, 450)));
        Assert.Equal(new CaptureRegion(1760, 0, 800, 450), _controller.Region);

        _controller.Handle(new MoveMessage(0, 0));
        Assert.Equal("move 1760,0", _input.Actions[^1]);

        var reloaded = new RegionStore(_storePath, NullLogger<RegionStore>.Instance).Load();
        Assert.Equal(_controller.Region, reloaded);
    }

    [Fact]
    public void Overview_mode_streams_the_whole_monitor()
    {
        Assert.Equal(new PixelRect(100, 200, 600, 600), _controller.CurrentSource);
        _controller.Handle(new SetModeMessage(ViewMode.Overview));
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), _controller.CurrentSource);
    }
}
