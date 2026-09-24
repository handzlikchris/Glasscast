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

    internal SessionLease(PairingCoordinator owner, string id, IPAddress remoteAddress, DateTimeOffset startedAt)
    {
        _owner = owner;
        Id = id;
        RemoteAddress = remoteAddress;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public IPAddress RemoteAddress { get; }

    public DateTimeOffset StartedAt { get; }

    public CancellationToken Ended => _ended.Token;

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

    public PairingCoordinator(IOptions<PairingOptions> options, TimeProvider time, AlertLog alerts,
        ILogger<PairingCoordinator> logger)
    {
        _options = options.Value;
        _time = time;
        _alerts = alerts;
        _logger = logger;
        _perIpLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsPerIp);
        _globalLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsGlobal);
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
            lease = new SessionLease(this, Secrets.NewId(), remote, _time.GetUtcNow());
            _active = lease;
            _state = State.ActiveSession;
        }

        _logger.LogInformation("Session {Session} started from {Remote}", lease.Id, remote);
        SessionChanged?.Invoke(Describe(lease));
        return lease;
    }

    /// <summary>Signals the active session to close. The slot frees once its handler disposes the lease.</summary>
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
