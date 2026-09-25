using System.Net.WebSockets;
using System.Text.Json;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;

namespace GlassesRemote.Server.Tests.Hosting;

/// <summary>End-to-end over real WebSockets on an in-memory server (fake desktop and media).</summary>
public sealed class EndpointTests : IAsyncLifetime
{
    private TestServerHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new TestServerHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Health_responds_with_strict_security_headers()
    {
        using var http = _host.CreateHttpClient();
        var response = await http.GetAsync("/health");

        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Unknown_paths_are_not_found()
    {
        using var http = _host.CreateHttpClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/admin")).StatusCode);
    }

    // Brief test 14: an unauthorised Origin is rejected.
    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://glasses.test")]
    [InlineData("https://glasses.test:8443")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task WebSockets_refuse_other_origins(string? origin)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("/ws/pair", origin));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("/ws/session", origin));
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.BadOrigin);
    }

    [Fact]
    public void Origin_policy_matches_exactly()
    {
        string[] allowed = ["https://glasses.example.com"];
        Assert.True(OriginPolicy.IsAllowed("https://glasses.example.com", allowed));
        Assert.True(OriginPolicy.IsAllowed("https://GLASSES.example.com", allowed));
        Assert.False(OriginPolicy.IsAllowed("https://glasses.example.com.evil.com", allowed));
        Assert.False(OriginPolicy.IsAllowed("https://evil.com/glasses.example.com", allowed));
        Assert.False(OriginPolicy.IsAllowed("", allowed));
    }

    [Fact]
    public async Task Full_flow_pairs_streams_and_injects_input()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        Assert.Equal(1, _host.KeepAwake.Active);

        await session.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\nfake-answer\r\n" });
        await session.SendAsync(new { type = "setMode", mode = "pointer" });
        await session.SendAsync(new { type = "move", x = 0.0, y = 0.0 });
        await session.SendAsync(new { type = "click", button = "left" });

        await WaitUntil(() => _host.Input.Actions.Count >= 2);
        Assert.StartsWith("move ", _host.Input.Actions[0]);
        Assert.Equal("click Left", _host.Input.Actions[1]);

        await WaitUntil(() => _host.Peers.Created[0].FramesSent > 0);
    }

    [Fact]
    public async Task Media_stats_list_each_sent_frame_with_its_rtp_timestamp_and_capture_time()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await session.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\n" });

        var stats = await session.ReceiveAsync("mediaStats", timeoutMs: 5000);

        Assert.True(stats.GetProperty("fps").GetDouble() > 0);
        Assert.True(stats.GetProperty("keyframes").GetInt32() >= 1);
        var frames = stats.GetProperty("frames").EnumerateArray().ToArray();
        Assert.NotEmpty(frames);
        // FakePeer numbers frames 0, 1, 2... at the test host's 30 fps: 3000 ticks of the 90 kHz clock apart.
        Assert.Equal(0u, frames[0][0].GetUInt32());
        Assert.Equal(3000u, frames[1][0].GetUInt32());
        Assert.InRange(frames[0][1].GetInt64(), before - 1000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(3, frames[0][2].GetInt32()); // FakeEncoder's frame
    }

    [Fact]
    public async Task Stats_from_the_glasses_and_the_pc_go_to_the_stats_log_without_counting_as_input()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);
        await session.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\n" });
        await session.ReceiveAsync("mediaStats");
        await session.SendAsync(new { type = "stats", e2eMs = 142.26, plis = 3, jitterBufferMs = (double?)null });
        await session.SendAsync(new { type = "switchApp", slot = 1 });
        await session.ReceiveAsync("appSwitch");

        var lines = ReadStatsLog();
        var glasses = lines.Single(l => l.GetProperty("kind").GetString() == "glasses");
        Assert.Equal(142.3, glasses.GetProperty("e2eMs").GetDouble());
        Assert.Equal(3, glasses.GetProperty("plis").GetDouble());
        Assert.Equal(JsonValueKind.Null, glasses.GetProperty("jitterBufferMs").ValueKind);
        Assert.Contains(lines, l => l.GetProperty("kind").GetString() == "pc" && l.GetProperty("frameMaxKb").GetDouble() >= 0);
        var events = lines.Where(l => l.GetProperty("kind").GetString() == "event")
            .Select(l => l.GetProperty("event").GetString()).ToArray();
        Assert.Equal(["start", "switchApp"], events);
        Assert.All(lines, l => Assert.Equal(glasses.GetProperty("session").GetString(), l.GetProperty("session").GetString()));
        Assert.Empty(_host.Input.Actions);
    }

    private JsonElement[] ReadStatsLog()
    {
        var file = Directory.GetFiles(_host.StatsDirectory, "stats-*.jsonl").Single();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

    [Fact]
    public async Task Region_changes_are_clamped_and_echoed()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        await session.SendAsync(new { type = "setRegion", x = 2500, y = 0, width = 600, height = 600 });
        var reply = await session.ReceiveAsync();

        Assert.Equal("region", reply.GetProperty("type").GetString());
        Assert.Equal(1960, reply.GetProperty("region").GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task App_shortcuts_come_from_the_pc_config_and_fit_the_window_to_the_cast_area()
    {
        await using var host = new TestServerHost(new()
        {
            ["Apps:Shortcuts:0:Name"] = "Claude",
            ["Apps:Shortcuts:0:Title"] = "herdr",
            ["Apps:Shortcuts:1:Name"] = "Broken",
            ["Apps:Shortcuts:2:Name"] = "Browser",
            ["Apps:Shortcuts:2:Process"] = "chrome",
        });
        var token = await host.PairAsync();
        using var session = await host.StartSessionAsync(token);

        // The shortcut without a process or title is skipped, so Browser is slot 2.
        var names = host.LastHello.GetProperty("apps").EnumerateArray().Select(a => a.GetString()).ToArray();
        Assert.Equal(["Claude", "Browser"], names);

        await session.SendAsync(new { type = "setRegion", x = 100, y = 50, width = 600, height = 600 });
        await session.ReceiveAsync();
        await session.SendAsync(new { type = "switchApp", slot = 2 });
        var reply = await session.ReceiveAsync();
        Assert.Equal("appSwitch", reply.GetProperty("type").GetString());
        Assert.Equal("switched", reply.GetProperty("result").GetString());
        Assert.Equal(["Browser 100,50 600x600"], host.Windows.Calls);

        host.Windows.Result = AppSwitchResult.NotRunning;
        await session.SendAsync(new { type = "switchApp", slot = 1 });
        Assert.Equal("notRunning", (await session.ReceiveAsync()).GetProperty("result").GetString());

        await session.SendAsync(new { type = "switchApp", slot = 3 });
        Assert.Equal("failed", (await session.ReceiveAsync()).GetProperty("result").GetString());
        Assert.Equal(2, host.Windows.Calls.Count);
    }

    [Fact]
    public async Task The_cast_area_follows_the_session_region_and_clears_when_it_ends()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);
        Assert.NotNull(_host.CastArea.Current);

        await session.SendAsync(new { type = "setRegion", x = 100, y = 50, width = 600, height = 600 });
        await session.ReceiveAsync();
        Assert.Equal(new CaptureRegion(100, 50, 600, 600), _host.CastArea.Current);

        _host.Coordinator.TerminateActiveSession();
        await WaitUntil(() => _host.CastArea.Current is null);
    }

    // Brief tests 1 and 4: no pairing while a session is active, and the answer is generic.
    [Fact]
    public async Task Pairing_is_refused_generically_while_a_session_is_active()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        using var intruder = await _host.ConnectAsync("/ws/pair");
        Assert.Equal("pairFailed", (await intruder.ReceiveAsync()).GetProperty("type").GetString());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.PairingWhileBusy);
    }

    // Brief test 6: unauthenticated sockets can't occupy the slot and are closed quickly.
    [Fact]
    public async Task Silent_session_sockets_time_out_without_blocking_the_real_client()
    {
        using var idle = await _host.ConnectAsync("/ws/session");

        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        await idle.WaitForCloseAsync();
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.AuthenticationTimedOut);
    }

    [Fact]
    public async Task Wrong_first_message_or_token_fails_generically()
    {
        using (var s = await _host.ConnectAsync("/ws/session"))
        {
            await s.SendAsync(new { type = "move", x = 0.5, y = 0.5 });
            Assert.Equal("authFailed", (await s.ReceiveAsync()).GetProperty("type").GetString());
        }

        using (var s = await _host.ConnectAsync("/ws/session"))
        {
            await s.SendAsync(new { type = "authenticate", token = "not-a-real-token" });
            Assert.Equal("authFailed", (await s.ReceiveAsync()).GetProperty("type").GetString());
        }

        Assert.Empty(_host.Input.Actions);
    }

    // Brief tests 7, 8 and 9.
    [Fact]
    public async Task One_session_only_and_the_token_dies_with_it()
    {
        var token = await _host.PairAsync();
        var session = await _host.StartSessionAsync(token);

        using (var second = await _host.ConnectAsync("/ws/session"))
        {
            await second.SendAsync(new { type = "authenticate", token });
            Assert.Equal("authFailed", (await second.ReceiveAsync()).GetProperty("type").GetString());
        }

        await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        session.Dispose();
        await WaitUntil(() => _host.Coordinator.ActiveSession is null);

        using var reuse = await _host.ConnectAsync("/ws/session");
        await reuse.SendAsync(new { type = "authenticate", token });
        Assert.Equal("authFailed", (await reuse.ReceiveAsync()).GetProperty("type").GetString());
        Assert.Equal(0, _host.KeepAwake.Active);
    }

    // Brief test 11: invalid and oversized messages end the session.
    [Theory]
    [InlineData("{\"type\":\"shell\",\"cmd\":\"calc\"}")]
    [InlineData("{\"type\":\"move\",\"x\":\"a\",\"y\":0}")]
    [InlineData("not json")]
    public async Task Invalid_messages_close_the_session(string payload)
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        await session.SendRawAsync(payload);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await session.WaitForCloseAsync());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.ProtocolViolation);
        await WaitUntil(() => _host.Coordinator.ActiveSession is null);
    }

    [Fact]
    public async Task Oversized_messages_close_the_session()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        await session.SendRawAsync($"{{\"type\":\"typeText\",\"text\":\"{new string('a', 40_000)}\"}}");

        await session.WaitForCloseAsync();
        await WaitUntil(() => _host.Coordinator.ActiveSession is null);
    }

    [Fact]
    public async Task Flooding_messages_closes_the_session()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        for (var i = 0; i < 400 && session.Socket.State == WebSocketState.Open; i++)
        {
            try
            {
                await session.SendAsync(new { type = "move", x = 0.5, y = 0.5 });
            }
            catch (WebSocketException)
            {
                break;
            }
        }

        await session.WaitForCloseAsync();
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.MessageRateLimited);
    }

    // Brief test 15: terminating on the PC closes the session at once.
    [Fact]
    public async Task Terminate_closes_the_session_socket()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);

        Assert.True(_host.Coordinator.TerminateActiveSession());

        await session.WaitForCloseAsync();
        Assert.Equal("terminated", session.Socket.CloseStatusDescription);
        await WaitUntil(() => _host.Coordinator.ActiveSession is null);
    }

    // Brief test 10: tokens never reach the logs.
    [Fact]
    public async Task Tokens_never_appear_in_logs()
    {
        var token = await _host.PairAsync();
        using (var session = await _host.StartSessionAsync(token))
        {
            await session.SendAsync(new { type = "authenticate", token });
            await session.WaitForCloseAsync();
        }

        using (var reuse = await _host.ConnectAsync("/ws/session"))
        {
            await reuse.SendAsync(new { type = "authenticate", token });
            await reuse.ReceiveAsync();
        }

        Assert.NotEmpty(_host.Logs);
        Assert.DoesNotContain(_host.Logs, line => line.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_pairing_sends_a_generic_failure()
    {
        using var pair = await _host.ConnectAsync("/ws/pair");
        await pair.ReceiveAsync();

        _host.Coordinator.Reject(_host.Coordinator.PendingRequest!.Id);

        Assert.Equal("pairFailed", (await pair.ReceiveAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Closing_the_pair_socket_cancels_the_request()
    {
        using (var pair = await _host.ConnectAsync("/ws/pair"))
        {
            await pair.ReceiveAsync();
            await pair.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }

        await WaitUntil(() => _host.Coordinator.PendingRequest is null);
    }

    [Fact]
    public async Task Overview_mode_streams_the_whole_monitor()
    {
        var token = await _host.PairAsync();
        using var session = await _host.StartSessionAsync(token);
        await session.SendAsync(new { type = "rtcAnswer", sdp = "v=0\r\n" });
        await session.SendAsync(new { type = "setMode", mode = "overview" });

        await WaitUntil(() =>
        {
            lock (_host.Capture.Sources)
            {
                return _host.Capture.Sources.Contains(new PixelRect(0, 0, 2560, 1440));
            }
        });
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition not met in time");
            }
            await Task.Delay(20);
        }
    }
}
