using System.Net;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GlassesRemote.Server.Tests.Pairing;

/// <summary>Approved glasses reconnect with a device token for a while, without the popup.</summary>
public sealed class DeviceGrantTests : IDisposable
{
    private static readonly IPAddress Glasses = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress Stranger = IPAddress.Parse("198.51.100.66");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
    private readonly string _grantFile = Path.Combine(Path.GetTempPath(), $"grant-{Guid.NewGuid():N}.json");
    private readonly AlertLog _alerts;
    private PairingCoordinator _coordinator;

    public DeviceGrantTests()
    {
        _alerts = new AlertLog(_time, NullLogger<AlertLog>.Instance);
        _coordinator = Create();
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        File.Delete(_grantFile);
    }

    private PairingCoordinator Create(TimeSpan? lifetime = null) =>
        new(Options.Create(new PairingOptions
        {
            DeviceGrantFile = _grantFile,
            DeviceGrantLifetime = lifetime ?? TimeSpan.FromHours(24),
        }), _time, _alerts, NullLogger<PairingCoordinator>.Instance);

    /// <summary>Pairs, approves and starts a session; returns it with its device token.</summary>
    private async Task<(SessionLease Lease, string DeviceToken)> ApprovedSessionAsync()
    {
        var request = _coordinator.TryOpenRequest(Glasses)!;
        Assert.True(_coordinator.Approve(request.Id));
        var lease = _coordinator.TryAuthenticate((await request.Outcome).Token!, Glasses)!;
        return (lease, lease.TakeDeviceToken()!);
    }

    private Task<SessionLease?> Resume(string token, IPAddress? from = null) =>
        _coordinator.TryResumeAsync(token, from ?? Glasses, CancellationToken.None);

    [Fact]
    public async Task An_approved_session_hands_out_a_device_token_once_that_starts_later_sessions()
    {
        var (lease, token) = await ApprovedSessionAsync();
        Assert.Null(lease.TakeDeviceToken());
        Assert.Equal(_time.GetUtcNow().AddHours(24), lease.DeviceTokenExpiresAt);
        lease.Dispose();

        using var resumed = await Resume(token);
        Assert.NotNull(resumed);
        Assert.True(resumed.Resumed);
        Assert.NotNull(_coordinator.ActiveSession);
    }

    [Fact]
    public async Task Each_use_swaps_the_token_and_the_old_one_then_forgets_the_device()
    {
        var (lease, first) = await ApprovedSessionAsync();
        lease.Dispose();

        var resumed = (await Resume(first))!;
        var second = resumed.TakeDeviceToken()!;
        Assert.NotEqual(first, second);
        resumed.ConfirmDeviceToken(); // the glasses answered the offer: they have the second token
        resumed.Dispose();

        // Someone replays the first token: refused, the device is forgotten, even the new token dies.
        Assert.Null(await Resume(first, Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.DeviceTokenReused);
        Assert.Null(_coordinator.RememberedDeviceExpiresAt);
        Assert.Null(await Resume(second));
    }

    [Fact]
    public async Task A_new_token_that_never_arrived_lets_the_previous_one_resume_without_an_alarm()
    {
        var (lease, first) = await ApprovedSessionAsync();
        lease.ConfirmDeviceToken();
        lease.Dispose();

        // The connection drops after the swap, before "authenticated" (and so the answer) arrives.
        var dropped = (await Resume(first))!;
        var lost = dropped.TakeDeviceToken()!;
        dropped.Dispose();

        var retried = (await Resume(first))!;
        var third = retried.TakeDeviceToken()!;
        Assert.DoesNotContain(_alerts.Recent(), a => a.Kind == AlertKind.DeviceTokenReused);
        Assert.NotNull(_coordinator.RememberedDeviceExpiresAt);
        retried.ConfirmDeviceToken();
        retried.Dispose();

        // The token that never arrived is dead; once the third is confirmed, the first is reuse again.
        Assert.Null(await Resume(lost));
        Assert.NotNull(_coordinator.RememberedDeviceExpiresAt);
        Assert.Null(await Resume(first, Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.DeviceTokenReused);
        Assert.Null(await Resume(third));
    }

    [Fact]
    public async Task A_late_confirmation_from_an_older_session_does_not_confirm_a_newer_token()
    {
        var (lease, first) = await ApprovedSessionAsync();
        lease.ConfirmDeviceToken();
        lease.Dispose();

        var dropped = (await Resume(first))!;
        dropped.Dispose();
        using var retried = (await Resume(first))!;

        dropped.ConfirmDeviceToken(); // its token was replaced by the retry's
        retried.Dispose();

        using var again = await Resume(first);
        Assert.NotNull(again);
        Assert.DoesNotContain(_alerts.Recent(), a => a.Kind == AlertKind.DeviceTokenReused);
    }

    [Fact]
    public async Task Remembering_ends_a_fixed_time_after_the_approval_however_often_it_is_used()
    {
        var (lease, token) = await ApprovedSessionAsync();
        lease.Dispose();

        _time.Advance(TimeSpan.FromHours(20));
        var resumed = (await Resume(token))!;
        token = resumed.TakeDeviceToken()!;
        resumed.Dispose();

        _time.Advance(TimeSpan.FromHours(4));
        Assert.Null(await Resume(token));
        Assert.Null(_coordinator.RememberedDeviceExpiresAt);
    }

    [Fact]
    public async Task Wrong_tokens_are_refused_and_leave_the_real_one_working()
    {
        var (lease, token) = await ApprovedSessionAsync();
        lease.Dispose();

        Assert.Null(await Resume(Secrets.NewToken(), Stranger));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.AuthenticationFailed);
        using var resumed = await Resume(token);
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task The_same_glasses_take_over_their_own_stale_session()
    {
        var (stale, token) = await ApprovedSessionAsync();
        // The session handler disposes the lease once the session has closed.
        stale.Ended.Register(() => Task.Run(stale.Dispose));

        using var resumed = await Resume(token);

        Assert.NotNull(resumed);
        Assert.True(stale.Superseded);
        Assert.True(stale.Ended.IsCancellationRequested);
        Assert.Equal(resumed.Id, _coordinator.ActiveSession?.Id);
    }

    [Fact]
    public async Task No_reconnect_while_someone_else_is_pairing()
    {
        var (lease, token) = await ApprovedSessionAsync();
        lease.Dispose();
        Assert.NotNull(_coordinator.TryOpenRequest(Stranger));

        Assert.Null(await Resume(token));
        Assert.Contains(_alerts.Recent(), a => a.Kind == AlertKind.PairingWhileBusy);
    }

    [Fact]
    public async Task Ending_the_session_on_the_pc_keeps_the_glasses_remembered()
    {
        var (lease, token) = await ApprovedSessionAsync();
        Assert.True(_coordinator.TerminateActiveSession());
        lease.Dispose();

        Assert.NotNull(_coordinator.RememberedDeviceExpiresAt);
        using var resumed = await Resume(token);
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task Forget_from_the_tray_and_rule_breaking_sessions_forget_the_glasses()
    {
        var (lease, token) = await ApprovedSessionAsync();
        lease.Dispose();
        _coordinator.ForgetDevice("test");
        Assert.Null(await Resume(token));

        (lease, _) = await ApprovedSessionAsync();
        lease.ForgetDevice("invalid message");
        Assert.Null(_coordinator.RememberedDeviceExpiresAt);
        lease.Dispose();
    }

    [Fact]
    public async Task The_glasses_stay_remembered_across_a_server_restart_as_hashes_only()
    {
        var (lease, token) = await ApprovedSessionAsync();
        lease.Dispose();
        Assert.DoesNotContain(token, File.ReadAllText(_grantFile), StringComparison.Ordinal);

        _coordinator.Dispose();
        _coordinator = Create();

        using var resumed = await Resume(token);
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task A_zero_lifetime_turns_remembering_off()
    {
        _coordinator.Dispose();
        _coordinator = Create(lifetime: TimeSpan.Zero);

        var request = _coordinator.TryOpenRequest(Glasses)!;
        Assert.True(_coordinator.Approve(request.Id));
        using var lease = _coordinator.TryAuthenticate((await request.Outcome).Token!, Glasses)!;

        Assert.Null(lease.TakeDeviceToken());
        Assert.Null(_coordinator.RememberedDeviceExpiresAt);
        Assert.False(File.Exists(_grantFile));
    }
}
