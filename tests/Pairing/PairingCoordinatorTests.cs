using System.Net;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GlassesRemote.Server.Tests.Pairing;

public sealed class PairingCoordinatorTests : IDisposable
{
    private static readonly IPAddress Glasses = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress Stranger = IPAddress.Parse("198.51.100.66");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
    private readonly AlertLog _alerts;
    private readonly PairingCoordinator _coordinator;

    public PairingCoordinatorTests()
    {
        _alerts = new AlertLog(_time, NullLogger<AlertLog>.Instance);
        _coordinator = new PairingCoordinator(Options.Create(new PairingOptions()), _time, _alerts,
            NullLogger<PairingCoordinator>.Instance);
    }

    public void Dispose() => _coordinator.Dispose();

    private async Task<string> PairAndApproveAsync()
    {
        var request = _coordinator.TryOpenRequest(Glasses)!;
        Assert.True(_coordinator.Approve(request.Id));
        return (await request.Outcome).Token!;
    }

    [Fact]
    public async Task Approved_request_receives_a_token_that_starts_a_session()
    {
        var request = _coordinator.TryOpenRequest(Glasses);
        Assert.NotNull(request);
        Assert.Matches("^[A-Z2-9]{3}-[A-Z2-9]{3}$", request.Code);

        Assert.True(_coordinator.Approve(request.Id));
        var outcome = await request.Outcome;
        Assert.Equal(PairingOutcomeKind.Approved, outcome.Kind);

        using var lease = _coordinator.TryAuthenticate(outcome.Token!, Glasses);
        Assert.NotNull(lease);
        Assert.Equal(Glasses.ToString(), _coordinator.ActiveSession?.RemoteAddress);
    }

    // Brief test 1: pairing cannot succeed without an open, approved request.
    [Fact]
    public void Cannot_approve_an_unknown_request_or_authenticate_without_approval()
    {
        Assert.False(_coordinator.Approve("not-a-request"));
        Assert.Null(_coordinator.TryAuthenticate(Secrets.NewToken(), Stranger));
    }

    // Brief test 2: pairing expires after 60 seconds.
    [Fact]
    public async Task Pending_request_expires_after_the_timeout()
    {
        var request = _coordinator.TryOpenRequest(Glasses)!;

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(request.Outcome.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PairingOutcomeKind.Expired, (await request.Outcome).Kind);
        Assert.False(_coordinator.Approve(request.Id));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.PairingTimedOut);

        // Back to idle: a fresh request can open.
        Assert.NotNull(_coordinator.TryOpenRequest(Glasses));
    }

    // Brief test 3: only the approved request gets the token; nobody else can even open one meanwhile.
    [Fact]
    public async Task Only_one_request_can_be_pending_and_others_get_nothing()
    {
        var mine = _coordinator.TryOpenRequest(Glasses)!;
        Assert.Null(_coordinator.TryOpenRequest(Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.PairingWhileBusy && a.RemoteAddress == Stranger.ToString());

        _coordinator.Approve(mine.Id);
        Assert.NotNull((await mine.Outcome).Token);
    }

    // Brief test 4: pairing closes immediately after success.
    [Fact]
    public async Task No_new_pairing_while_a_token_is_issued_or_a_session_is_active()
    {
        var token = await PairAndApproveAsync();
        Assert.Null(_coordinator.TryOpenRequest(Stranger));

        using var lease = _coordinator.TryAuthenticate(token, Glasses);
        Assert.NotNull(lease);
        Assert.Null(_coordinator.TryOpenRequest(Stranger));
    }

    // Brief test 5: a second client cannot race and obtain a session.
    [Fact]
    public async Task Concurrent_authentications_with_the_same_token_yield_exactly_one_session()
    {
        var token = await PairAndApproveAsync();

        var leases = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => _coordinator.TryAuthenticate(token, Glasses))
            .ToList();

        Assert.Single(leases, l => l is not null);
        foreach (var lease in leases)
        {
            lease?.Dispose();
        }
    }

    [Fact]
    public void Concurrent_pairing_requests_open_exactly_one()
    {
        var requests = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(i => _coordinator.TryOpenRequest(IPAddress.Parse($"198.51.100.{i + 1}")))
            .ToList();

        Assert.Single(requests, r => r is not null);
    }

    // Brief test 6: unauthenticated clients cannot occupy the slot or burn the real token.
    [Fact]
    public async Task Wrong_tokens_do_not_consume_the_issued_token()
    {
        var token = await PairAndApproveAsync();

        Assert.Null(_coordinator.TryAuthenticate("guess", Stranger));
        Assert.Null(_coordinator.TryAuthenticate(Secrets.NewToken(), Stranger));
        Assert.Null(_coordinator.TryAuthenticate(new string('A', 10_000), Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.AuthenticationFailed);

        using var lease = _coordinator.TryAuthenticate(token, Glasses);
        Assert.NotNull(lease);
    }

    // Brief test 7: only one authenticated session can be active.
    [Fact]
    public async Task Second_authentication_fails_while_a_session_is_active()
    {
        var token = await PairAndApproveAsync();
        using var first = _coordinator.TryAuthenticate(token, Glasses);
        Assert.NotNull(first);

        Assert.Null(_coordinator.TryAuthenticate(token, Glasses));
    }

    // Brief tests 8 and 9: disconnect invalidates the token, and reusing it fails.
    [Fact]
    public async Task Token_is_single_use_and_dies_with_the_session()
    {
        var token = await PairAndApproveAsync();
        var lease = _coordinator.TryAuthenticate(token, Glasses)!;
        lease.Dispose();

        Assert.Null(_coordinator.ActiveSession);
        Assert.Null(_coordinator.TryAuthenticate(token, Glasses));
    }

    [Fact]
    public async Task Issued_token_expires_if_never_used()
    {
        var token = await PairAndApproveAsync();
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(_coordinator.TryAuthenticate(token, Glasses));
        Assert.NotNull(_coordinator.TryOpenRequest(Glasses));
    }

    [Fact]
    public async Task Rejected_request_gets_no_token_and_raises_an_alert()
    {
        var request = _coordinator.TryOpenRequest(Stranger)!;
        Assert.True(_coordinator.Reject(request.Id));

        Assert.Equal(PairingOutcomeKind.Rejected, (await request.Outcome).Kind);
        Assert.Null((await request.Outcome).Token);
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.PairingRejected);
    }

    [Fact]
    public async Task Cancelled_request_frees_the_slot_without_an_alert()
    {
        var request = _coordinator.TryOpenRequest(Glasses)!;
        _coordinator.Cancel(request.Id);

        Assert.Equal(PairingOutcomeKind.Cancelled, (await request.Outcome).Kind);
        Assert.Empty(_alerts.Recent());
        Assert.NotNull(_coordinator.TryOpenRequest(Glasses));
    }

    // Brief test 15: the user can terminate control locally at once.
    [Fact]
    public async Task Terminate_signals_the_active_session()
    {
        var token = await PairAndApproveAsync();
        using var lease = _coordinator.TryAuthenticate(token, Glasses)!;

        Assert.True(_coordinator.TerminateActiveSession());
        Assert.True(lease.Ended.IsCancellationRequested);
    }

    [Fact]
    public void Pairing_requests_are_rate_limited_per_ip()
    {
        for (var i = 0; i < 5; i++)
        {
            var request = _coordinator.TryOpenRequest(Stranger);
            Assert.NotNull(request);
            _coordinator.Cancel(request.Id);
        }

        Assert.Null(_coordinator.TryOpenRequest(Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.PairingRateLimited);

        // Other sources are unaffected, and the window slides.
        Assert.NotNull(_coordinator.TryOpenRequest(Glasses));
    }

    [Fact]
    public async Task Session_events_fire_on_start_and_end()
    {
        var changes = new List<ActiveSessionInfo?>();
        _coordinator.SessionChanged += changes.Add;

        var token = await PairAndApproveAsync();
        var lease = _coordinator.TryAuthenticate(token, Glasses)!;
        lease.Dispose();

        Assert.Equal(2, changes.Count);
        Assert.NotNull(changes[0]);
        Assert.Null(changes[1]);
    }
}
