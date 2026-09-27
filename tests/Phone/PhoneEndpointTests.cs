using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Tests.Hosting;

namespace GlassesRemote.Server.Tests.Phone;

/// <summary>The phone companion socket and phone sessions, over real WebSockets with a fake companion.</summary>
public sealed class PhoneEndpointTests : IAsyncLifetime
{
    private TestServerHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new TestServerHost(new Dictionary<string, string?> { ["Companion:StartTimeout"] = "00:00:03" });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("https://glasses.test")]
    [InlineData("https://evil.example")]
    public async Task Companion_socket_refuses_web_pages(string origin)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("/ws/companion", origin));
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.BadOrigin);
    }

    [Fact]
    public async Task Companion_pairs_through_the_popup_and_keeps_only_a_hash()
    {
        var token = await PairCompanionAsync();

        Assert.Equal("Test phone", _host.Companion.PairedName);
        Assert.True(File.Exists(_host.CompanionGrantFile));
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(_host.CompanionGrantFile));

        using var companion = await ConnectCompanionAsync(token);
        Assert.NotNull(_host.Companion.Current);
        Assert.DoesNotContain(_host.Logs, line => line.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_pairing_fails_generically()
    {
        using var socket = await _host.ConnectAsync("/ws/companion", origin: null);
        await socket.SendAsync(new { type = "pair", name = "Stranger" });
        await socket.ReceiveAsync("pairCode");
        Assert.True(_host.Companion.Reject(_host.Companion.PendingRequest!.Request.Id));

        Assert.Equal("pairFailed", (await socket.ReceiveAsync()).GetProperty("type").GetString());
        Assert.Null(_host.Companion.PairedName);
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.PairingRejected);
    }

    [Fact]
    public async Task Unknown_companion_tokens_are_refused()
    {
        await PairCompanionAsync();
        using var socket = await _host.ConnectAsync("/ws/companion", origin: null);
        await socket.SendAsync(new { type = "auth", token = "not-the-token" });

        Assert.Equal("authFailed", (await socket.ReceiveAsync()).GetProperty("type").GetString());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.AuthenticationFailed);
    }

    [Fact]
    public async Task Companion_must_say_something_first()
    {
        using var socket = await _host.ConnectAsync("/ws/companion", origin: null);
        await socket.WaitForCloseAsync();
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.AuthenticationTimedOut);
    }

    [Fact]
    public async Task Phone_session_relays_signalling_and_never_touches_the_pc()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await StartPhoneSessionAsync();

        Assert.Equal("asking", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        await companion.ReceiveAsync("sessionStart");

        await companion.SendAsync(new { type = "sessionState", state = "live" });
        await companion.SendAsync(new { type = "rtcOffer", sdp = "v=0\r\nphone-offer\r\n" });
        await companion.SendAsync(new
        {
            type = "iceCandidate", candidate = "candidate:1 1 udp 1 192.168.1.50 40000 typ host", sdpMid = "0", sdpMLineIndex = 0,
        });

        Assert.Equal("live", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        Assert.Equal("v=0\r\nphone-offer\r\n", (await glasses.ReceiveAsync("rtcOffer")).GetProperty("sdp").GetString());
        var candidate = await glasses.ReceiveAsync("iceCandidate");
        Assert.Contains("192.168.1.50", candidate.GetProperty("candidate").GetString());

        await glasses.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\nglasses-answer\r\n" });
        await glasses.SendAsync(new
        {
            type = "iceCandidate", candidate = "candidate:2 1 udp 1 10.1.1.1 50000 typ host", sdpMid = "0", sdpMLineIndex = 0,
        });
        Assert.Equal("v=0\r\nglasses-answer\r\n", (await companion.ReceiveAsync("rtcAnswer")).GetProperty("sdp").GetString());
        Assert.Contains("10.1.1.1", (await companion.ReceiveAsync("iceCandidate")).GetProperty("candidate").GetString());

        await glasses.SendAsync(new { type = "ping", t = 1 });
        await glasses.ReceiveAsync("pong");

        // No capture, no encoder, no input, no keep-awake: the PC only relayed.
        Assert.Empty(_host.Peers.Created);
        Assert.Empty(_host.Input.Actions);
        Assert.Equal(0, _host.KeepAwake.Active);
        Assert.Null(_host.CastArea.Current);
    }

    [Fact]
    public async Task Glasses_input_in_a_phone_session_is_a_violation()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await StartPhoneSessionAsync();
        await companion.ReceiveAsync("sessionStart");

        await glasses.SendAsync(new { type = "click", button = "left" });

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await glasses.WaitForCloseAsync());
        Assert.Equal("invalid message", glasses.Socket.CloseStatusDescription);
        Assert.Empty(_host.Input.Actions);
        await companion.ReceiveAsync("sessionEnd");
    }

    [Fact]
    public async Task Declining_on_the_phone_ends_the_session()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await StartPhoneSessionAsync();
        await companion.ReceiveAsync("sessionStart");

        await companion.SendAsync(new { type = "sessionState", state = "declined" });

        await glasses.WaitForCloseAsync();
        Assert.Equal("phone declined", glasses.Socket.CloseStatusDescription);
        // The slot frees once the close handshake finishes (up to 2 s: this test client never answers it).
        for (var i = 0; i < 200 && _host.Coordinator.ActiveSession is not null; i++)
        {
            await Task.Delay(20);
        }
        Assert.Null(_host.Coordinator.ActiveSession);
    }

    [Fact]
    public async Task Losing_the_phone_ends_the_session()
    {
        var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await StartPhoneSessionAsync();
        await companion.ReceiveAsync("sessionStart");

        await companion.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        companion.Dispose();

        await glasses.WaitForCloseAsync();
        Assert.Equal("phone offline", glasses.Socket.CloseStatusDescription);
    }

    [Fact]
    public async Task Glasses_leaving_stops_the_phone()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        var glasses = await StartPhoneSessionAsync();
        await companion.ReceiveAsync("sessionStart");

        await glasses.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        glasses.Dispose();

        await companion.ReceiveAsync("sessionEnd");
        Assert.False(_host.Companion.Current!.InSession);
    }

    [Fact]
    public async Task A_session_waits_for_the_companion_to_connect()
    {
        var token = await PairCompanionAsync();
        using var glasses = await StartPhoneSessionAsync();
        Assert.Equal("offline", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());

        using var companion = await ConnectCompanionAsync(token);
        await companion.ReceiveAsync("sessionStart");
        Assert.Equal("asking", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task No_phone_within_the_start_timeout_ends_the_session()
    {
        using var glasses = await StartPhoneSessionAsync();
        await glasses.WaitForCloseAsync(timeoutMs: 10_000);
        Assert.Equal("phone offline", glasses.Socket.CloseStatusDescription);
    }

    [Fact]
    public async Task Forgetting_the_phone_drops_it_and_its_token()
    {
        var token = await PairCompanionAsync();
        using var companion = await ConnectCompanionAsync(token);

        _host.Companion.Forget("test");

        await companion.WaitForCloseAsync();
        Assert.Equal("forgotten", companion.Socket.CloseStatusDescription);
        using var again = await _host.ConnectAsync("/ws/companion", origin: null);
        await again.SendAsync(new { type = "auth", token });
        Assert.Equal("authFailed", (await again.ReceiveAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_second_companion_connection_replaces_the_first()
    {
        var token = await PairCompanionAsync();
        using var first = await ConnectCompanionAsync(token);
        using var second = await ConnectCompanionAsync(token);

        await first.WaitForCloseAsync();
        Assert.Equal("replaced", first.Socket.CloseStatusDescription);
        Assert.NotNull(_host.Companion.Current);
    }

    [Fact]
    public async Task Companion_protocol_violations_close_it()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        await companion.SendAsync(new { type = "typeText", text = "hi" });

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await companion.WaitForCloseAsync());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.ProtocolViolation);
    }

    private async Task<string> PairCompanionAsync()
    {
        using var socket = await _host.ConnectAsync("/ws/companion", origin: null);
        await socket.SendAsync(new { type = "pair", name = "Test phone" });
        var code = await socket.ReceiveAsync("pairCode");

        var pending = _host.Companion.PendingRequest!;
        Assert.Equal(code.GetProperty("code").GetString(), pending.Request.Code);
        Assert.True(_host.Companion.Approve(pending.Request.Id));

        return (await socket.ReceiveAsync("paired")).GetProperty("token").GetString()!;
    }

    private async Task<TestSocket> ConnectCompanionAsync(string token)
    {
        var socket = await _host.ConnectAsync("/ws/companion", origin: null);
        await socket.SendAsync(new { type = "auth", token });
        Assert.Equal("authenticated", (await socket.ReceiveAsync()).GetProperty("type").GetString());
        return socket;
    }

    private async Task<TestSocket> StartPhoneSessionAsync()
    {
        var token = await _host.PairAsync();
        var session = await _host.ConnectAsync("/ws/session");
        await session.SendAsync(new { type = "authenticate", token, target = "phone" });
        Assert.Equal("authenticated", (await session.ReceiveAsync()).GetProperty("type").GetString());
        return session;
    }
}
