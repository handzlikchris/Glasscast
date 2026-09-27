using System.Net;
using GlassesRemote.Server.Alerts;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Pairing;

public enum PairingOutcomeKind
{
    Approved,
    Rejected,
    Expired,
    Cancelled,
}

public sealed record PairingOutcome(PairingOutcomeKind Kind, string? Token = null);

public sealed record ActiveSessionInfo(string Id, string RemoteAddress, DateTimeOffset StartedAt);

/// <summary>A pairing request waiting for Approve/Reject on the PC.</summary>
public sealed class PairingRequest
{
    internal PairingRequest(string id, string code, IPAddress remoteAddress, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        Code = code;
        RemoteAddress = remoteAddress;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string Id { get; }

    /// <summary>Shown on the glasses and in the popup, so you can tell your request from anyone else's.</summary>
    public string Code { get; }

    public IPAddress RemoteAddress { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    internal TaskCompletionSource<PairingOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<PairingOutcome> Outcome => Completion.Task;
}

/// <summary>
/// Holds the single active-session slot. Dispose when the session ends;
/// <see cref="Ended"/> fires when the session is terminated from the PC.
/// </summary>
public sealed class SessionLease : IDisposable
{
    private readonly PairingCoordinator _owner;
    private readonly CancellationTokenSource _ended = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _deviceToken;
    private readonly byte[]? _deviceTokenHash;

    internal SessionLease(PairingCoordinator owner, string id, IPAddress remoteAddress, DateTimeOffset startedAt,
        string? grantId = null, bool resumed = false, string? deviceToken = null, DateTimeOffset? deviceTokenExpiresAt = null)
    {
        _owner = owner;
        Id = id;
        RemoteAddress = remoteAddress;
        StartedAt = startedAt;
        GrantId = grantId;
        Resumed = resumed;
        _deviceToken = deviceToken;
        _deviceTokenHash = deviceToken is null ? null : Secrets.HashToken(deviceToken);
        DeviceTokenExpiresAt = deviceTokenExpiresAt;
    }

    public string Id { get; }

    public IPAddress RemoteAddress { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>The remembered device this session belongs to, if any.</summary>
    public string? GrantId { get; }

    /// <summary>Started with a device token rather than a fresh approval.</summary>
    public bool Resumed { get; }

    public DateTimeOffset? DeviceTokenExpiresAt { get; }

    /// <summary>The same device reconnected and took over this session.</summary>
    public bool Superseded { get; internal set; }

    public CancellationToken Ended => _ended.Token;

    /// <summary>Completes once the session slot is free again.</summary>
    internal Task Released => _released.Task;

    /// <summary>
    /// The device token for the glasses' next session. Handed out once (in "authenticated") and
    /// then dropped here; the coordinator keeps only its hash.
    /// </summary>
    public string? TakeDeviceToken() => Interlocked.Exchange(ref _deviceToken, null);

    /// <summary>
    /// The glasses have shown they got this session's device token (they answered the offer, which
    /// comes after "authenticated" on the same socket). From now on the token before it counts as
    /// reuse. Does nothing if a newer token has been issued since.
    /// </summary>
    public void ConfirmDeviceToken() => _owner.ConfirmDeviceToken(GrantId, _deviceTokenHash);

    /// <summary>The session broke the rules: stop remembering its device.</summary>
    public void ForgetDevice(string reason) => _owner.ForgetDevice(GrantId, reason);

    internal void Signal()
    {
        try
        {
            _ended.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _owner.EndSession(this);

    internal void MarkReleased() => _released.TrySetResult();
}

/// <summary>
/// Server-side pairing and session state machine:
///
///   Idle → PendingRequest → TokenIssued → ActiveSession → Idle
///
/// Every transition happens under one lock, so two clients can never both win.
/// Only one request can be pending, and only one session active, at a time.
/// The raw token is handed back once to the approved request; only its SHA-256
/// hash is kept, and it is consumed on first use.
///
/// The first session after an approval also remembers the device (a <see cref="DeviceGrant"/>):
/// it gets a device token to start later sessions without the popup, until a fixed time after
/// the approval. Each use swaps it for a new one; presenting a swapped-out token forgets the
/// device, once the glasses have confirmed they got the new one (until then the connection may
/// have dropped before it arrived, so the old token still resumes). Still one session at a time:
/// only the same device may take over its own session.
/// </summary>
public sealed class PairingCoordinator : IDisposable
{
    private enum State
    {
        Idle,
        PendingRequest,
        TokenIssued,
        ActiveSession,
    }

    private readonly TimeProvider _time;
    private readonly PairingOptions _options;
    private readonly AlertLog _alerts;
    private readonly ILogger<PairingCoordinator> _logger;
    private readonly SlidingWindowLimiter _perIpLimiter;
    private readonly SlidingWindowLimiter _globalLimiter;
    private readonly object _gate = new();

    private State _state = State.Idle;
    private PairingRequest? _pending;
    private ITimer? _pendingTimer;
    private byte[]? _issuedTokenHash;
    private ITimer? _tokenTimer;
    private SessionLease? _active;
    private readonly DeviceGrantStore _grantStore;
    private DeviceGrant? _grant;

    public PairingCoordinator(IOptions<PairingOptions> options, TimeProvider time, AlertLog alerts,
        ILogger<PairingCoordinator> logger)
    {
        _options = options.Value;
        _time = time;
        _alerts = alerts;
        _logger = logger;
        _perIpLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsPerIp);
        _globalLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsGlobal);
        _grantStore = new DeviceGrantStore(_options.DeviceGrantFile, logger);
        _grant = _options.DeviceGrantLifetime > TimeSpan.Zero ? _grantStore.Load() : null;
    }

    /// <summary>How long an approval lets the glasses reconnect without the popup (zero: never).</summary>
    public TimeSpan DeviceGrantLifetime => _options.DeviceGrantLifetime;

    /// <summary>A device was remembered (until the given time) or forgotten (null).</summary>
    public event Action<DateTimeOffset?>? DeviceGrantChanged;

    /// <summary>Until when approved glasses can reconnect without the popup; null when none are remembered.</summary>
    public DateTimeOffset? RememberedDeviceExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return CurrentGrant()?.ExpiresAt;
            }
        }
    }

    /// <summary>A new request needs a decision: show the popup.</summary>
    public event Action<PairingRequest>? RequestOpened;

    /// <summary>The request was approved, rejected, expired or cancelled: close the popup.</summary>
    public event Action<PairingRequest>? RequestClosed;

    /// <summary>The active session started (non-null) or ended (null).</summary>
    public event Action<ActiveSessionInfo?>? SessionChanged;

    public ActiveSessionInfo? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _active is null ? null : Describe(_active);
            }
        }
    }

    public PairingRequest? PendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>
    /// Opens a pairing request, or returns null when busy or rate-limited.
    /// Callers must answer a null with the same generic failure either way.
    /// </summary>
    public PairingRequest? TryOpenRequest(IPAddress remote)
    {
        PairingRequest request;
        lock (_gate)
        {
            if (!_perIpLimiter.TryAcquire(remote.ToString()) || !_globalLimiter.TryAcquire("*"))
            {
                _alerts.Raise(AlertKind.PairingRateLimited, remote, "Too many pairing requests");
                return null;
            }

            if (_state != State.Idle)
            {
                _alerts.Raise(AlertKind.PairingWhileBusy, remote, $"Pairing attempted while {Describe(_state)}");
                return null;
            }

            var now = _time.GetUtcNow();
            request = new PairingRequest(Secrets.NewId(), Secrets.NewPairingCode(), remote, now, now + _options.RequestTimeout);
            _pending = request;
            _state = State.PendingRequest;
            _pendingTimer = _time.CreateTimer(_ => Expire(request.Id), null, _options.RequestTimeout, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Pairing request {Code} opened from {Remote}", request.Code, remote);
        RequestOpened?.Invoke(request);
        return request;
    }

    /// <summary>Approves the pending request and issues a single-use token to it.</summary>
    public bool Approve(string requestId)
    {
        PairingRequest request;
        string token;
        lock (_gate)
        {
            if (_state != State.PendingRequest || _pending?.Id != requestId || _time.GetUtcNow() >= _pending.ExpiresAt)
            {
                return false;
            }

            request = _pending;
            ClearPending();

            token = Secrets.NewToken();
            _issuedTokenHash = Secrets.HashToken(token);
            _state = State.TokenIssued;
            _tokenTimer = _time.CreateTimer(_ => ExpireToken(), null, _options.TokenUseWindow, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Pairing request {Code} approved", request.Code);
        request.Completion.TrySetResult(new PairingOutcome(PairingOutcomeKind.Approved, token));
        RequestClosed?.Invoke(request);
        return true;
    }

    public bool Reject(string requestId) =>
        Close(requestId, PairingOutcomeKind.Rejected, AlertKind.PairingRejected, "Pairing request rejected on the PC");

    /// <summary>The glasses went away before a decision.</summary>
    public void Cancel(string requestId) => Close(requestId, PairingOutcomeKind.Cancelled, alert: null, detail: null);

    private void Expire(string requestId) =>
        Close(requestId, PairingOutcomeKind.Expired, AlertKind.PairingTimedOut, "Pairing request timed out without approval");

    private bool Close(string requestId, PairingOutcomeKind outcome, AlertKind? alert, string? detail)
    {
        PairingRequest request;
        lock (_gate)
        {
            if (_state != State.PendingRequest || _pending?.Id != requestId)
            {
                return false;
            }

            request = _pending;
            ClearPending();
            _state = State.Idle;
        }

        _logger.LogInformation("Pairing request {Code} closed: {Outcome}", request.Code, outcome);
        if (alert is { } kind)
        {
            _alerts.Raise(kind, request.RemoteAddress, detail!);
        }
        request.Completion.TrySetResult(new PairingOutcome(outcome));
        RequestClosed?.Invoke(request);
        return true;
    }

    private void ExpireToken()
    {
        lock (_gate)
        {
            if (_state != State.TokenIssued)
            {
                return;
            }

            ClearToken();
            _state = State.Idle;
        }

        _logger.LogInformation("Issued token expired unused");
    }

    /// <summary>
    /// Exchanges the single-use token for the active-session slot.
    /// Returns null for any failure; callers must not reveal why.
    /// </summary>
    public SessionLease? TryAuthenticate(string presentedToken, IPAddress remote)
    {
        SessionLease lease;
        lock (_gate)
        {
            var valid = _state == State.TokenIssued
                        && _issuedTokenHash is not null
                        && Secrets.TokenMatches(presentedToken, _issuedTokenHash);
            if (!valid)
            {
                _alerts.Raise(AlertKind.AuthenticationFailed, remote, "Session authentication failed");
                return null;
            }

            ClearToken();
            var now = _time.GetUtcNow();
            string? deviceToken = null;
            if (_options.DeviceGrantLifetime > TimeSpan.Zero)
            {
                // A fresh approval remembers this device, replacing any other.
                deviceToken = Secrets.NewToken();
                _grant = new DeviceGrant(Secrets.NewId(), Secrets.HashToken(deviceToken), null, now + _options.DeviceGrantLifetime,
                    Unconfirmed: true);
                _grantStore.Save(_grant);
            }

            lease = new SessionLease(this, Secrets.NewId(), remote, now, _grant?.Id, resumed: false, deviceToken,
                deviceToken is null ? null : _grant!.ExpiresAt);
            _active = lease;
            _state = State.ActiveSession;
        }

        _logger.LogInformation("Session {Session} started from {Remote}", lease.Id, remote);
        SessionChanged?.Invoke(Describe(lease));
        if (lease.DeviceTokenExpiresAt is { } expires)
        {
            DeviceGrantChanged?.Invoke(expires);
        }
        return lease;
    }

    /// <summary>
    /// Starts a session with a device token instead of an approval. If that device's own session
    /// is still open (a dropped connection the server hasn't noticed yet), it is closed first.
    /// Returns null for any failure; callers must not reveal why.
    /// </summary>
    public async Task<SessionLease?> TryResumeAsync(string presentedToken, IPAddress remote, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            SessionLease? stale = null;
            SessionLease? lease = null;
            var reused = false;
            lock (_gate)
            {
                var grant = CurrentGrant();
                var current = grant is not null && Secrets.TokenMatches(presentedToken, grant.CurrentHash);
                var previous = !current && grant?.PreviousHash is { } previousHash && Secrets.TokenMatches(presentedToken, previousHash);
                if (previous && !grant!.Unconfirmed)
                {
                    // Already swapped for a newer token the glasses confirmed: a copy is in someone else's hands.
                    _alerts.Raise(AlertKind.DeviceTokenReused, remote, "An old device token was presented; the device was forgotten");
                    ClearGrant();
                    reused = true;
                }
                else if (!current && !previous)
                {
                    _alerts.Raise(AlertKind.AuthenticationFailed, remote, "Session authentication failed");
                    return null;
                }
                else if (_state == State.ActiveSession && _active?.GrantId == grant!.Id && attempt == 0)
                {
                    stale = _active;
                    stale.Superseded = true;
                }
                else if (_state != State.Idle)
                {
                    _alerts.Raise(AlertKind.PairingWhileBusy, remote, $"Reconnect attempted while {Describe(_state)}");
                    return null;
                }
                else
                {
                    // With the previous token, the one issued after it never reached the glasses (the
                    // connection dropped before "authenticated"): that one is dropped unused, and the
                    // previous token stays the one whose reuse is caught.
                    var deviceToken = Secrets.NewToken();
                    _grant = grant! with
                    {
                        PreviousHash = current ? grant.CurrentHash : grant.PreviousHash,
                        CurrentHash = Secrets.HashToken(deviceToken),
                        Unconfirmed = true,
                    };
                    _grantStore.Save(_grant);
                    lease = new SessionLease(this, Secrets.NewId(), remote, _time.GetUtcNow(), grant.Id, resumed: true,
                        deviceToken, grant.ExpiresAt);
                    _active = lease;
                    _state = State.ActiveSession;
                }
            }

            if (reused)
            {
                DeviceGrantChanged?.Invoke(null);
                TerminateActiveSession();
                return null;
            }

            if (lease is not null)
            {
                _logger.LogInformation("Session {Session} resumed by the remembered device from {Remote}", lease.Id, remote);
                SessionChanged?.Invoke(Describe(lease));
                return lease;
            }

            _logger.LogInformation("Session {Session} replaced: its device reconnected", stale!.Id);
            stale.Signal();
            try
            {
                await stale.Released.WaitAsync(_options.TakeoverTimeout, _time, ct);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    internal void ConfirmDeviceToken(string? grantId, byte[]? issuedHash)
    {
        lock (_gate)
        {
            if (grantId is null || issuedHash is null || _grant is not { Unconfirmed: true } grant || grant.Id != grantId
                || !grant.CurrentHash.AsSpan().SequenceEqual(issuedHash))
            {
                return;
            }

            _grant = grant with { Unconfirmed = false };
            _grantStore.Save(_grant);
        }
    }

    /// <summary>Stops remembering approved glasses: their next session needs the popup again.</summary>
    public void ForgetDevice(string reason) => ForgetDevice(grantId: null, reason);

    internal void ForgetDevice(string? grantId, string reason)
    {
        lock (_gate)
        {
            if (_grant is null || (grantId is not null && _grant.Id != grantId))
            {
                return;
            }
            ClearGrant();
        }

        _logger.LogInformation("Remembered device forgotten: {Reason}", reason);
        DeviceGrantChanged?.Invoke(null);
    }

    /// <summary>
    /// Signals the active session to close (tray, Ctrl+Shift+X). The slot frees once its handler
    /// disposes the lease. The glasses stay remembered: ending a session means "stop for now", and
    /// the user reconnects without walking back to the PC. Forget remembered glasses (tray) is the
    /// way to lock them out.
    /// </summary>
    public bool TerminateActiveSession()
    {
        SessionLease? lease;
        lock (_gate)
        {
            lease = _active;
        }

        if (lease is null)
        {
            return false;
        }

        _logger.LogInformation("Session {Session} terminated from the PC", lease.Id);
        lease.Signal();
        return true;
    }

    internal void EndSession(SessionLease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, lease))
            {
                return;
            }

            _active = null;
            _state = State.Idle;
        }

        lease.Signal();
        lease.MarkReleased();
        _logger.LogInformation("Session {Session} ended", lease.Id);
        SessionChanged?.Invoke(null);
    }

    private void ClearPending()
    {
        _pending = null;
        _pendingTimer?.Dispose();
        _pendingTimer = null;
    }

    private void ClearToken()
    {
        if (_issuedTokenHash is not null)
        {
            Array.Clear(_issuedTokenHash);
        }
        _issuedTokenHash = null;
        _tokenTimer?.Dispose();
        _tokenTimer = null;
    }

    /// <summary>The remembered device, unless its time is up (then it's dropped). Call under the lock.</summary>
    private DeviceGrant? CurrentGrant()
    {
        if (_grant is not null && _time.GetUtcNow() >= _grant.ExpiresAt)
        {
            ClearGrant();
        }
        return _grant;
    }

    private void ClearGrant()
    {
        _grant = null;
        _grantStore.Save(null);
    }

    private static ActiveSessionInfo Describe(SessionLease lease) =>
        new(lease.Id, lease.RemoteAddress.ToString(), lease.StartedAt);

    private static string Describe(State state) => state switch
    {
        State.PendingRequest => "another request was pending",
        State.TokenIssued => "a token was waiting to be used",
        State.ActiveSession => "a session was active",
        _ => "idle",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            ClearPending();
            ClearToken();
        }
    }
}
