namespace GlassesRemote.Server.Tests.Hosting;

/// <summary>Web:AllowSameOrigin exists for LAN testing and must only work in Development.</summary>
public sealed class SameOriginTests
{
    // TestServer requests arrive as http://localhost.
    private const string SameOrigin = "http://localhost";

    private static readonly Dictionary<string, string?> Enabled = new() { ["Web:AllowSameOrigin"] = "true" };

    [Fact]
    public async Task Same_origin_is_accepted_in_development_when_enabled()
    {
        await using var host = new TestServerHost(Enabled, ["--environment=Development"]);
        using var socket = await host.ConnectAsync("/ws/session", SameOrigin);
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.Socket.State);
    }

    [Fact]
    public async Task Other_origins_are_still_refused_when_enabled()
    {
        await using var host = new TestServerHost(Enabled, ["--environment=Development"]);
        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync("/ws/session", "http://evil.example"));
    }

    [Fact]
    public async Task Flag_is_ignored_outside_development()
    {
        await using var host = new TestServerHost(Enabled, ["--environment=Production"]);
        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync("/ws/session", SameOrigin));
    }

    [Fact]
    public async Task Same_origin_is_refused_by_default()
    {
        await using var host = new TestServerHost(args: ["--environment=Development"]);
        await Assert.ThrowsAnyAsync<Exception>(() => host.ConnectAsync("/ws/session", SameOrigin));
    }
}
