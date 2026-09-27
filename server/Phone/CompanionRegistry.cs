using System.Net;
using System.Text.Json;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>A phone's pairing request, waiting for Approve/Reject in the popup on the PC.</summary>
public sealed record CompanionPairRequest(PairingRequest Request, string Name);

/// <summary>The paired phone: its name and the hash of its companion token.</summary>
public sealed record CompanionGrant(string Name, byte[] TokenHash, DateTimeOffset PairedAt);

/// <summary>
/// The phone companion app, from the PC's side:
///
/// - Pairing: one request at a time, rate-limited, approved or rejected in the same Approve popup
///   as the glasses (the phone shows the code too). Approval mints a 256-bit companion token; only
///   its SHA-256 is kept (and saved, so a restart keeps the phone paired). One phone at a time: a
///   new approval replaces the old one. The tray's Forget phone drops it.
/// - Connection: the one authenticated companion socket (<see cref="CompanionLink"/>). A newer
///   one replaces an older one; phone sessions wait for one to be there.
///
/// The token never expires on its own: the companion lives on your phone, and every phone session
/// still needs someone to tap Android's screen-capture consent on the phone itself.
/// </summary>
public sealed class CompanionRegistry : IDisposable
{
    private readonly CompanionOptions _options;
    private readonly TimeProvider _time;
    private readonly AlertLog _alerts;
    private readonly ILogger<CompanionRegistry> _logger;
    private readonly SlidingWindowLimiter _perIpLimiter;
    private readonly SlidingWindowLimiter _globalLimiter;
    private readonly string? _grantFile;
    private readonly object _gate = new();

    private CompanionPairRequest? _pending;
    private ITimer? _pendingTimer;
    private CompanionGrant? _grant;
    private CompanionLink? _link;
    private TaskCompletionSource<CompanionLink> _linkArrived = NewLinkSource();

    public CompanionRegistry(IOptions<CompanionOptions> options, TimeProvider time, AlertLog alerts,
        ILogger<CompanionRegistry> logger)
    {
        _options = options.Value;
        _time = time;
        _alerts = alerts;
        _logger = logger;
        _perIpLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsPerIp);
        _globalLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsGlobal);
        _grantFile = string.IsNullOrEmpty(_options.GrantFile) ? CompanionOptions.DefaultGrantFile : _options.GrantFile;
        _grant = LoadGrant();
    }

    /// <summary>A phone asked to pair: show the popup.</summary>
    public event Action<CompanionPairRequest>? RequestOpened;

    /// <summary>The request was approved, rejected, expired or cancelled: close the popup.</summary>
    public event Action<CompanionPairRequest>? RequestClosed;

    /// <summary>Paired, forgotten, connected or disconnected.</summary>
    public event Action? Changed;

    public CompanionPairRequest? PendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>The paired phone's name, or null when none is paired.</summary>
    public string? PairedName
    {
        get
        {
            lock (_gate)
            {
                return _grant?.Name;
            }
        }
    }

    /// <summary>The connected companion, if any.</summary>
    public CompanionLink? Current
    {
        get
        {
            lock (_gate)
            {
                return _link;
            }
        }
    }

    /// <summary>Opens a pairing request, or null when one is pending or the limits are hit (answer generically).</summary>
    public CompanionPairRequest? TryOpenRequest(IPAddress remote, string name)
    {
        CompanionPairRequest pending;
        lock (_gate)
        {
            if (!_perIpLimiter.TryAcquire(remote.ToString()) || !_globalLimiter.TryAcquire("*"))
            {
                _alerts.Raise(AlertKind.PairingRateLimited, remote, "Too many phone pairing requests");
                return null;
            }

            if (_pending is not null)
            {
                _alerts.Raise(AlertKind.PairingWhileBusy, remote, "Phone pairing attempted while another was waiting");
                return null;
            }

            var now = _time.GetUtcNow();
            var request = new PairingRequest(Secrets.NewId(), Secrets.NewPairingCode(), remote, now, now + _options.RequestTimeout);
            pending = new CompanionPairRequest(request, name);
            _pending = pending;
            _pendingTimer = _time.CreateTimer(_ => Close(request.Id, PairingOutcomeKind.Expired), null,
                _options.RequestTimeout, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Phone pairing request {Code} opened from {Remote}", pending.Request.Code, remote);
        RequestOpened?.Invoke(pending);
        return pending;
    }

    /// <summary>Approves the pending request: the phone gets its token, and replaces any paired before.</summary>
    public bool Approve(string requestId)
    {
        CompanionPairRequest pending;
        string token;
        CompanionLink? replaced;
        lock (_gate)
        {
            if (_pending?.Request.Id != requestId || _time.GetUtcNow() >= _pending.Request.ExpiresAt)
            {
                return false;
            }

            pending = _pending;
            ClearPending();
            token = Secrets.NewToken();
            _grant = new CompanionGrant(pending.Name, Secrets.HashToken(token), _time.GetUtcNow());
            SaveGrant(_grant);
            replaced = _link;
            _link = null;
        }

        replaced?.Close("replaced");
        _logger.LogInformation("Phone pairing request {Code} approved", pending.Request.Code);
        pending.Request.Completion.TrySetResult(new PairingOutcome(PairingOutcomeKind.Approved, token));
        RequestClosed?.Invoke(pending);
        Changed?.Invoke();
        return true;
    }

    public bool Reject(string requestId) => Close(requestId, PairingOutcomeKind.Rejected);

    /// <summary>The phone went away before a decision.</summary>
    public void Cancel(string requestId) => Close(requestId, PairingOutcomeKind.Cancelled);

    private bool Close(string requestId, PairingOutcomeKind outcome)
    {
        CompanionPairRequest pending;
        lock (_gate)
        {
            if (_pending?.Request.Id != requestId)
            {
                return false;
            }

            pending = _pending;
            ClearPending();
        }

        _logger.LogInformation("Phone pairing request {Code} closed: {Outcome}", pending.Request.Code, outcome);
        if (outcome == PairingOutcomeKind.Rejected)
        {
            _alerts.Raise(AlertKind.PairingRejected, pending.Request.RemoteAddress, "Phone pairing request rejected on the PC");
        }
        else if (outcome == PairingOutcomeKind.Expired)
        {
            _alerts.Raise(AlertKind.PairingTimedOut, pending.Request.RemoteAddress, "Phone pairing request timed out without approval");
        }
        pending.Request.Completion.TrySetResult(new PairingOutcome(outcome));
        RequestClosed?.Invoke(pending);
        return true;
    }

    /// <summary>Constant-time check of a companion token against the paired phone's hash.</summary>
    public bool Authenticate(string token)
    {
        lock (_gate)
        {
            return _grant is { } grant && Secrets.TokenMatches(token, grant.TokenHash);
        }
    }

    /// <summary>Stops trusting the paired phone and drops its connection. It has to pair again.</summary>
    public void Forget(string reason)
    {
        CompanionLink? link;
        lock (_gate)
        {
            if (_grant is null)
            {
                return;
            }

            _grant = null;
            SaveGrant(null);
            link = _link;
            _link = null;
        }

        _logger.LogInformation("Paired phone forgotten: {Reason}", reason);
        link?.Close("forgotten");
        Changed?.Invoke();
    }

    /// <summary>The authenticated companion connection; an older one is closed as replaced.</summary>
    public void Attach(CompanionLink link)
    {
        CompanionLink? previous;
        TaskCompletionSource<CompanionLink> arrived;
        lock (_gate)
        {
            previous = _link;
            _link = link;
            arrived = _linkArrived;
            _linkArrived = NewLinkSource();
        }

        previous?.Close("replaced");
        _logger.LogInformation("Phone companion {Name} connected from {Remote}", link.Name, link.RemoteAddress);
        arrived.TrySetResult(link);
        Changed?.Invoke();
    }

    public void Detach(CompanionLink link)
    {
        lock (_gate)
        {
            if (_link != link)
            {
                return;
            }
            _link = null;
        }

        _logger.LogInformation("Phone companion {Name} disconnected", link.Name);
        Changed?.Invoke();
    }

    /// <summary>The connected companion, now or as soon as one connects.</summary>
    public Task<CompanionLink> WaitForLinkAsync(CancellationToken ct)
    {
        Task<CompanionLink> arrived;
        lock (_gate)
        {
            if (_link is { } link)
            {
                return Task.FromResult(link);
            }
            arrived = _linkArrived.Task;
        }
        return arrived.WaitAsync(ct);
    }

    private void ClearPending()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        _pending = null;
    }

    private static TaskCompletionSource<CompanionLink> NewLinkSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record StoredGrant(string Name, string TokenHash, DateTimeOffset PairedAt);

    private CompanionGrant? LoadGrant()
    {
        if (_grantFile is null || !File.Exists(_grantFile))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredGrant>(File.ReadAllText(_grantFile));
            return stored is null ? null : new CompanionGrant(stored.Name, Convert.FromBase64String(stored.TokenHash), stored.PairedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read the paired phone from {Path}; it has to pair again", _grantFile);
            return null;
        }
    }

    private void SaveGrant(CompanionGrant? grant)
    {
        if (_grantFile is null)
        {
            return;
        }

        try
        {
            if (grant is null)
            {
                File.Delete(_grantFile);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_grantFile)!);
            File.WriteAllText(_grantFile, JsonSerializer.Serialize(
                new StoredGrant(grant.Name, Convert.ToBase64String(grant.TokenHash), grant.PairedAt)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the paired phone to {Path}", _grantFile);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pendingTimer?.Dispose();
        }
    }
}
