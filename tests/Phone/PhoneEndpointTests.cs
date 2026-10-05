using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Tests.Hosting;
using static GlassesRemote.Server.Tests.Phone.CompanionProtocolTests;

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

        Assert.Equal("Test phone", Assert.Single(_host.Companion.Phones).Name);
        Assert.True(File.Exists(_host.PhonesFile));
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(_host.PhonesFile));

        using var companion = await ConnectCompanionAsync(token);
        Assert.True(Assert.Single(_host.Companion.Phones).Connected);
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
        Assert.Empty(_host.Companion.Phones);
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
    public async Task Relay_passes_pairing_and_signalling_and_never_touches_the_pc()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");
        Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());

        // Pairing and authentication go through as they are: the phone decides.
        await glasses.SendAsync(new { type = "pairStart", commit = Mac });
        Assert.Equal(Mac, (await companion.ReceiveAsync("pairStart")).GetProperty("commit").GetString());
        await companion.SendAsync(new { type = "pairKey", key = Key });
        Assert.Equal(Key, (await glasses.ReceiveAsync("pairKey")).GetProperty("key").GetString());
        await glasses.SendAsync(new { type = "pairReveal", key = Key });
        await companion.ReceiveAsync("pairReveal");
        await companion.SendAsync(new { type = "paired" });
        await glasses.ReceiveAsync("paired");

        await glasses.SendAsync(new { type = "hello", id = Id, nonce = Nonce });
        var hello = await companion.ReceiveAsync("hello");
        Assert.Equal(Id, hello.GetProperty("id").GetString());
        await companion.SendAsync(new { type = "challenge", nonce = Nonce, mac = Mac });
        Assert.Equal(Mac, (await glasses.ReceiveAsync("challenge")).GetProperty("mac").GetString());
        await glasses.SendAsync(new { type = "proof", mac = Mac });
        await companion.ReceiveAsync("proof");

        await companion.SendAsync(new { type = "sessionState", state = "asking" });
        Assert.Equal("asking", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        await companion.SendAsync(new { type = "sessionState", state = "live" });
        await companion.SendAsync(new { type = "rtcOffer", sdp = "v=0\r\nphone-offer\r\n", mac = Mac });
        await companion.SendAsync(new
        {
            type = "iceCandidate", candidate = "candidate:1 1 udp 1 192.168.1.50 40000 typ host", sdpMid = "0", sdpMLineIndex = 0,
        });

        Assert.Equal("live", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        var offer = await glasses.ReceiveAsync("rtcOffer");
        Assert.Equal("v=0\r\nphone-offer\r\n", offer.GetProperty("sdp").GetString());
        Assert.Equal(Mac, offer.GetProperty("mac").GetString());
        Assert.Contains("192.168.1.50", (await glasses.ReceiveAsync("iceCandidate")).GetProperty("candidate").GetString());

        await glasses.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\nglasses-answer\r\n", mac = Mac });
        await glasses.SendAsync(new
        {
            type = "iceCandidate", candidate = "candidate:2 1 udp 1 10.1.1.1 50000 typ host", sdpMid = "0", sdpMLineIndex = 0,
        });
        Assert.Equal("v=0\r\nglasses-answer\r\n", (await companion.ReceiveAsync("rtcAnswer")).GetProperty("sdp").GetString());
        Assert.Contains("10.1.1.1", (await companion.ReceiveAsync("iceCandidate")).GetProperty("candidate").GetString());

        await glasses.SendAsync(new { type = "ping", t = 1 });
        await glasses.ReceiveAsync("pong");

        // No capture, no encoder, no input, no keep-awake, no PC session: the PC only relayed.
        Assert.Empty(_host.Peers.Created);
        Assert.Empty(_host.Input.Actions);
        Assert.Equal(0, _host.KeepAwake.Active);
        Assert.Null(_host.CastArea.Current);
        Assert.Null(_host.Coordinator.ActiveSession);
    }

    [Fact]
    public async Task A_relay_needs_no_approval_on_the_pc_and_logs_no_secret()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        Assert.Null(_host.Coordinator.PendingRequest);
        await glasses.SendAsync(new { type = "hello", id = Id, nonce = Nonce });
        await companion.ReceiveAsync("hello");
        Assert.DoesNotContain(_host.Logs, line => line.Contains(Nonce, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Glasses_input_in_a_relay_is_a_violation()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        await glasses.SendAsync(new { type = "tap", x = 0.5, y = 0.5 });

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await glasses.WaitForCloseAsync());
        Assert.Equal("invalid message", glasses.Socket.CloseStatusDescription);
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.ProtocolViolation);
        await companion.ReceiveAsync("relayClosed");
    }

    [Fact]
    public async Task Declining_on_the_phone_closes_the_relay()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        await companion.SendAsync(new { type = "sessionState", state = "declined" });

        await glasses.WaitForCloseAsync();
        Assert.Equal("phone declined", glasses.Socket.CloseStatusDescription);
    }

    [Fact]
    public async Task Losing_the_phone_closes_the_relay()
    {
        var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        await companion.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        companion.Dispose();

        await glasses.WaitForCloseAsync();
        Assert.Equal("phone offline", glasses.Socket.CloseStatusDescription);
    }

    [Fact]
    public async Task Glasses_leaving_tells_the_phone_and_nothing_more()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        await glasses.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        glasses.Dispose();

        // Only "relayClosed": a session already live on the phone carries on without the PC.
        await companion.ReceiveAsync("relayClosed");
        Assert.False(OnlyPhoneLink.HasRelay);
    }

    [Fact]
    public async Task Newer_glasses_replace_an_older_relay()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var first = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");
        using var second = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");

        await first.WaitForCloseAsync();
        Assert.Equal("replaced", first.Socket.CloseStatusDescription);
        await second.SendAsync(new { type = "hello", id = Id, nonce = Nonce });
        await companion.ReceiveAsync("hello");
    }

    [Fact]
    public async Task Phone_messages_without_a_relay_are_dropped()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        await companion.SendAsync(new { type = "challenge", nonce = Nonce, mac = Mac });

        using var glasses = await OpenRelayAsync();
        await companion.ReceiveAsync("relayOpen");
        await companion.SendAsync(new { type = "paired" });
        Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        Assert.Equal("paired", (await glasses.ReceiveAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_relay_waits_for_the_companion_to_connect()
    {
        var token = await PairCompanionAsync();
        using var glasses = await OpenRelayAsync();
        Assert.Equal("offline", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());

        using var companion = await ConnectCompanionAsync(token);
        await companion.ReceiveAsync("relayOpen");
        Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task No_phone_within_the_start_timeout_closes_the_relay()
    {
        await PairCompanionAsync();
        using var glasses = await OpenRelayAsync();
        await glasses.WaitForCloseAsync(timeoutMs: 10_000);
        Assert.Equal("phone offline", glasses.Socket.CloseStatusDescription);
    }

    [Fact]
    public async Task Relays_are_rate_limited_per_address()
    {
        string? last = null;
        for (var i = 0; i < 12; i++)
        {
            using var glasses = await OpenRelayAsync();
            if (i < 11)
            {
                continue;
            }
            await glasses.WaitForCloseAsync();
            last = glasses.Socket.CloseStatusDescription;
        }

        Assert.Equal("rate limit", last);
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.MessageRateLimited);
    }

    [Fact]
    public async Task Forgetting_the_phone_drops_it_and_its_token()
    {
        var token = await PairCompanionAsync();
        using var companion = await ConnectCompanionAsync(token);

        _host.Companion.ForgetAll("test");

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
        Assert.NotNull(OnlyPhoneLink);
    }

    [Fact]
    public async Task Companion_protocol_violations_close_it()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        await companion.SendAsync(new { type = "typeText", text = "hi" });

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await companion.WaitForCloseAsync());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.ProtocolViolation);
    }

    [Fact]
    public async Task Glasses_without_a_phone_id_go_to_the_pcs_only_phone()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();

        Assert.Equal(Assert.Single(_host.Companion.Phones).Id, (await glasses.ReceiveAsync("phoneFound")).GetProperty("phone").GetString());
        Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        await companion.ReceiveAsync("relayOpen");
    }

    [Fact]
    public async Task With_two_phones_the_glasses_get_a_connect_code_and_the_phone_it_is_typed_into_answers()
    {
        using var first = await ConnectCompanionAsync(await PairCompanionAsync());
        using var second = await ConnectCompanionAsync(await PairCompanionAsync());
        var secondId = _host.Companion.Phones[1].Id;

        using var glasses = await OpenRelayAsync();
        var code = (await glasses.ReceiveAsync("connectCode")).GetProperty("code").GetString()!;
        await second.SendAsync(new { type = "claim", code });

        await second.ReceiveAsync("claimed");
        await second.ReceiveAsync("relayOpen");
        Assert.Equal(secondId, (await glasses.ReceiveAsync("phoneFound")).GetProperty("phone").GetString());
        Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
        Assert.False(_host.Companion.Connected(_host.Companion.Phones[0].Id)!.HasRelay);
    }

    [Fact]
    public async Task Glasses_naming_their_phone_reach_only_that_phone()
    {
        using var first = await ConnectCompanionAsync(await PairCompanionAsync());
        using var second = await ConnectCompanionAsync(await PairCompanionAsync());
        var firstId = _host.Companion.Phones[0].Id;

        using var glasses = await OpenRelayAsync(firstId);

        Assert.Equal("ready", (await glasses.ReceiveAsync()).GetProperty("state").GetString());
        await first.ReceiveAsync("relayOpen");
        Assert.False(_host.Companion.Connected(_host.Companion.Phones[1].Id)!.HasRelay);
    }

    [Fact]
    public async Task An_unknown_phone_id_gets_a_connect_code()
    {
        using var first = await ConnectCompanionAsync(await PairCompanionAsync());
        using var second = await ConnectCompanionAsync(await PairCompanionAsync());

        using var glasses = await OpenRelayAsync(new string('A', 22));

        await glasses.ReceiveAsync("connectCode");
    }

    [Fact]
    public async Task Connect_codes_are_single_use_and_wrong_ones_fail()
    {
        using var first = await ConnectCompanionAsync(await PairCompanionAsync());
        using var second = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        var code = (await glasses.ReceiveAsync("connectCode")).GetProperty("code").GetString()!;
        var wrong = code == "AAA-AAA" ? "BBB-BBB" : "AAA-AAA";

        await first.SendAsync(new { type = "claim", code = wrong });
        await first.ReceiveAsync("claimFailed");
        await first.SendAsync(new { type = "claim", code });
        await first.ReceiveAsync("claimed");
        await second.SendAsync(new { type = "claim", code });
        await second.ReceiveAsync("claimFailed");
    }

    [Fact]
    public async Task A_phone_may_only_try_a_few_connect_codes()
    {
        using var first = await ConnectCompanionAsync(await PairCompanionAsync());
        using var second = await ConnectCompanionAsync(await PairCompanionAsync());
        using var glasses = await OpenRelayAsync();
        var code = (await glasses.ReceiveAsync("connectCode")).GetProperty("code").GetString()!;
        var wrong = code == "AAA-AAA" ? "BBB-BBB" : "AAA-AAA";

        for (var i = 0; i < 5; i++)
        {
            await first.SendAsync(new { type = "claim", code = wrong });
            await first.ReceiveAsync("claimFailed");
        }
        await first.SendAsync(new { type = "claim", code });

        await first.ReceiveAsync("claimFailed");
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.PairingRateLimited);
    }

    [Fact]
    public async Task A_malformed_connect_code_is_a_violation()
    {
        using var companion = await ConnectCompanionAsync(await PairCompanionAsync());
        await companion.SendAsync(new { type = "claim", code = "abc-def" });

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await companion.WaitForCloseAsync());
    }

    [Fact]
    public async Task Open_registration_needs_no_popup_and_never_picks_a_phone_for_the_glasses()
    {
        await using var host = new TestServerHost(new Dictionary<string, string?> { ["Companion:Registration"] = "Open" });
        var opened = false;
        host.Companion.RequestOpened += _ => opened = true;

        using var socket = await host.ConnectAsync("/ws/companion", origin: null);
        await socket.SendAsync(new { type = "pair", name = "Someone's phone" });
        var token = (await socket.ReceiveAsync("paired")).GetProperty("token").GetString()!;

        Assert.False(opened);
        Assert.Equal("Someone's phone", Assert.Single(host.Companion.Phones).Name);
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(host.PhonesFile));

        using var companion = await host.ConnectAsync("/ws/companion", origin: null);
        await companion.SendAsync(new { type = "auth", token });
        await companion.ReceiveAsync("authenticated");
        using var glasses = await host.ConnectAsync("/ws/session");
        await glasses.SendAsync(new { type = "phone" });
        await glasses.ReceiveAsync("connectCode");
    }

    [Fact]
    public async Task Open_registration_stops_at_the_phone_limit()
    {
        await using var host = new TestServerHost(new Dictionary<string, string?>
        {
            ["Companion:Registration"] = "Open",
            ["Companion:MaxPhones"] = "1",
        });

        for (var i = 0; i < 2; i++)
        {
            using var socket = await host.ConnectAsync("/ws/companion", origin: null);
            await socket.SendAsync(new { type = "pair", name = "Phone" });
            Assert.Equal(i == 0 ? "paired" : "pairFailed", (await socket.ReceiveAsync()).GetProperty("type").GetString());
        }
        Assert.Single(host.Companion.Phones);
    }

    [Fact]
    public async Task The_phone_paired_before_phones_had_ids_keeps_working()
    {
        await using var before = new TestServerHost();
        const string token = "an-old-companion-token-from-companion-grant-json";
        await File.WriteAllTextAsync(before.CompanionGrantFile, System.Text.Json.JsonSerializer.Serialize(new
        {
            Name = "Old phone",
            TokenHash = Convert.ToBase64String(GlassesRemote.Server.Pairing.Secrets.HashToken(token)),
            PairedAt = DateTimeOffset.UtcNow,
        }));

        await using var host = new TestServerHost(new Dictionary<string, string?>
        {
            ["Companion:GrantFile"] = before.CompanionGrantFile,
        });

        Assert.Equal("Old phone", Assert.Single(host.Companion.Phones).Name);
        Assert.True(File.Exists(host.PhonesFile));
        using var companion = await host.ConnectAsync("/ws/companion", origin: null);
        await companion.SendAsync(new { type = "auth", token });
        await companion.ReceiveAsync("authenticated");
    }

    private CompanionLink OnlyPhoneLink => _host.Companion.Connected(Assert.Single(_host.Companion.Phones).Id)!;

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

    private async Task<TestSocket> OpenRelayAsync(string? phone = null)
    {
        var socket = await _host.ConnectAsync("/ws/session");
        await socket.SendAsync(phone is null ? new { type = "phone" } : (object)new { type = "phone", phone });
        return socket;
    }
}
