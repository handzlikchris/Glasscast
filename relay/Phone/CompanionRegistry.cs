using System.Net;
using System.Text.Json;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>A phone's registration request, waiting for Approve/Reject in the popup on the PC.</summary>
public sealed record CompanionPairRequest(PairingRequest Request, string Name);

/// <summary>
/// A registered phone: its id (how glasses find it again; not a secret), its name, the hash of its
/// companion token, when it registered and when it last connected (to the hour).
/// </summary>
public sealed record RegisteredPhone(string Id, string Name, byte[] TokenHash, DateTimeOffset RegisteredAt, DateTimeOffset LastSeen);

/// <summary>A registered phone as the tray lists it.</summary>
public sealed record PhoneSummary(string Id, string Name, bool Connected);

/// <summary>
/// Glasses in a relay that don't know their phone yet: they show <see cref="Code"/>, someone types
/// it into a companion, and <see cref="Claimed"/> gives that phone's connection.
/// </summary>
public sealed class ConnectCode
{
    internal ConnectCode(string code) => Code = code;

    public string Code { get; }

    internal TaskCompletionSource<CompanionLink> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CompanionLink> Claimed => Completion.Task;
}

/// <summary>
/// The phones' companion apps, from the server's side:
///
/// - Registration (<see cref="CompanionOptions.Registration"/>): on the PC someone approves it in
///   the same popup as the glasses (the phone shows the code too); a hosted relay lets any
///   companion register, rate-limited and capped. Either way the phone gets a 256-bit token, of
///   which only the SHA-256 is kept (and saved, so a restart keeps the phones), and an id.
/// - Connections: one authenticated companion socket per phone (<see cref="CompanionLink"/>); a
///   newer one replaces an older one. Glasses' relays wait for theirs to be there.
/// - Routing: glasses name their phone by id. Without one (or with one this server doesn't know),
///   they get a <see cref="ConnectCode"/> to type into the companion. On the PC with a single
///   approved phone, they go straight to it.
/// - Relays: glasses open them without a login (the phone pairs and checks the glasses itself),
///   so they're rate-limited per IP and overall; so are connect codes, per phone.
///
/// Tokens never expire on their own: the companion lives on your phone, and every phone session
/// still needs someone to tap Android's screen-capture consent on the phone itself.
/// </summary>
public sealed class CompanionRegistry : IDisposable
{
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromHours(1);

    private readonly CompanionOptions _options;
    private readonly TimeProvider _time;
    private readonly AlertLog _alerts;
    private readonly ILogger<CompanionRegistry> _logger;
    private readonly SlidingWindowLimiter _perIpLimiter;
    private readonly SlidingWindowLimiter _globalLimiter;
    private readonly SlidingWindowLimiter _relayPerIpLimiter;
    private readonly SlidingWindowLimiter _relayGlobalLimiter;
    private readonly SlidingWindowLimiter _claimLimiter;
    private readonly string _phonesFile;
    private readonly string _grantFile;
    private readonly object _gate = new();

    private readonly Dictionary<string, RegisteredPhone> _phones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idsByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompanionLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<CompanionLink>> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectCode> _codes = new(StringComparer.Ordinal);

    private CompanionPairRequest? _pending;
    private ITimer? _pendingTimer;

    public CompanionRegistry(IOptions<CompanionOptions> options, TimeProvider time, AlertLog alerts,
        ILogger<CompanionRegistry> logger)
    {
        _options = options.Value;
        _time = time;
        _alerts = alerts;
        _logger = logger;
        _perIpLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsPerIp);
        _globalLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRequestsGlobal);
        _relayPerIpLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRelaysPerIp);
        _relayGlobalLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxRelaysGlobal);
        _claimLimiter = new SlidingWindowLimiter(time, _options.RateWindow, _options.MaxClaimsPerPhone);
        _phonesFile = string.IsNullOrEmpty(_options.PhonesFile) ? CompanionOptions.DefaultPhonesFile : _options.PhonesFile;
        _grantFile = string.IsNullOrEmpty(_options.GrantFile) ? CompanionOptions.DefaultGrantFile : _options.GrantFile;
        Load();
    }

    /// <summary>A phone asked to register and needs the popup (<see cref="CompanionRegistration.Approve"/>).</summary>
    public event Action<CompanionPairRequest>? RequestOpened;

    /// <summary>The request was approved, rejected, expired or cancelled: close the popup.</summary>
    public event Action<CompanionPairRequest>? RequestClosed;

    /// <summary>A phone registered, was forgotten, connected or disconnected.</summary>
    public event Action? Changed;

    public CompanionRegistration Registration => _options.Registration;

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

    /// <summary>The registered phones, oldest first.</summary>
    public IReadOnlyList<PhoneSummary> Phones
    {
        get
        {
            lock (_gate)
            {
                return _phones.Values.OrderBy(p => p.RegisteredAt)
                    .Select(p => new PhoneSummary(p.Id, p.Name, _links.ContainsKey(p.Id)))
                    .ToList();
            }
        }
    }

    /// <summary>Whether a phone with this id is registered here.</summary>
    public bool IsRegistered(string phoneId)
    {
        lock (_gate)
        {
            return _phones.ContainsKey(phoneId);
        }
    }

    /// <summary>
    /// The phone glasses without an id go to: on the PC (approval), the only phone if exactly one
    /// is approved, as before phones had ids. Null otherwise: they get a connect code.
    /// </summary>
    public string? SolePhone
    {
        get
        {
            lock (_gate)
            {
                return _options.Registration == CompanionRegistration.Approve && _phones.Count == 1
                    ? _phones.Keys.First()
                    : null;
            }
        }
    }

    /// <summary>That phone's companion connection, if it's connected.</summary>
    public CompanionLink? Connected(string phoneId)
    {
        lock (_gate)
        {
            return _links.GetValueOrDefault(phoneId);
        }
    }

    /// <summary>
    /// Opens a registration request for the popup, or null when one is pending or the limits are
    /// hit (answer generically). Only with <see cref="CompanionRegistration.Approve"/>.
    /// </summary>
    public CompanionPairRequest? TryOpenRequest(IPAddress remote, string name)
    {
        if (_options.Registration != CompanionRegistration.Approve)
        {
            throw new InvalidOperationException("Phones register without approval here");
        }

        CompanionPairRequest pending;
        lock (_gate)
        {
            if (!AllowRegistration(remote))
            {
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

    /// <summary>
    /// Registers a phone straight away and returns its token, or null when the limits are hit or
    /// there's no room (answer generically). Only with <see cref="CompanionRegistration.Open"/>.
    /// </summary>
    public string? TryRegister(IPAddress remote, string name)
    {
        if (_options.Registration != CompanionRegistration.Open)
        {
            throw new InvalidOperationException("Phones need approval on the PC here");
        }

        string token;
        lock (_gate)
        {
            if (!AllowRegistration(remote))
            {
                return null;
            }

            if (_phones.Count >= _options.MaxPhones && !MakeRoom())
            {
                _alerts.Raise(AlertKind.PairingRateLimited, remote, "Phone registration refused: no room for more phones");
                return null;
            }

            token = AddPhone(name);
        }

        _logger.LogInformation("Phone {Name} registered from {Remote}", name, remote);
        Changed?.Invoke();
        return token;
    }

    /// <summary>
    /// Whether glasses from <paramref name="remote"/> may open a phone relay now. Relays need no
    /// login (the phone checks the glasses), so they're rate-limited here instead.
    /// </summary>
    public bool TryOpenRelay(IPAddress remote)
    {
        lock (_gate)
        {
            if (_relayPerIpLimiter.TryAcquire(remote.ToString()) && _relayGlobalLimiter.TryAcquire("*"))
            {
                return true;
            }
        }

        _alerts.Raise(AlertKind.MessageRateLimited, remote, "Too many phone relays");
        return false;
    }

    /// <summary>Approves the pending request: the phone is registered and gets its token.</summary>
    public bool Approve(string requestId)
    {
        CompanionPairRequest pending;
        string token;
        lock (_gate)
        {
            if (_pending?.Request.Id != requestId || _time.GetUtcNow() >= _pending.Request.ExpiresAt)
            {
                return false;
            }

            pending = _pending;
            ClearPending();
            token = AddPhone(pending.Name);
        }

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

    /// <summary>The phone this companion token belongs to, or null. Notes when it was last seen.</summary>
    public RegisteredPhone? Authenticate(string token)
    {
        // Reject absurd input before hashing; a real token is 43 characters. The lookup is by the
        // SHA-256 of the token, which says nothing about how close a guess came.
        if (token.Length is 0 or > 128)
        {
            return null;
        }

        var hash = Convert.ToHexString(Secrets.HashToken(token));
        lock (_gate)
        {
            if (!_idsByHash.TryGetValue(hash, out var id))
            {
                return null;
            }

            var phone = _phones[id];
            var now = _time.GetUtcNow();
            if (now - phone.LastSeen >= LastSeenResolution)
            {
                phone = phone with { LastSeen = now };
                _phones[id] = phone;
                Save();
            }
            return phone;
        }
    }

    /// <summary>Stops trusting that phone and drops its connection. It has to register again.</summary>
    public void Forget(string phoneId, string reason)
    {
        CompanionLink? link;
        lock (_gate)
        {
            if (!RemovePhone(phoneId, out link))
            {
                return;
            }
            Save();
        }

        _logger.LogInformation("Phone forgotten: {Reason}", reason);
        link?.Close("forgotten");
        Changed?.Invoke();
    }

    /// <summary>Forgets every phone (the tray's Forget phones).</summary>
    public void ForgetAll(string reason)
    {
        List<CompanionLink> links = [];
        lock (_gate)
        {
            if (_phones.Count == 0)
            {
                return;
            }

            foreach (var id in _phones.Keys.ToList())
            {
                if (RemovePhone(id, out var link) && link is not null)
                {
                    links.Add(link);
                }
            }
            Save();
        }

        _logger.LogInformation("All phones forgotten: {Reason}", reason);
        foreach (var link in links)
        {
            link.Close("forgotten");
        }
        Changed?.Invoke();
    }

    /// <summary>An authenticated companion connection; an older one of the same phone is closed as replaced.</summary>
    public void Attach(CompanionLink link)
    {
        CompanionLink? previous;
        TaskCompletionSource<CompanionLink>? waiting;
        lock (_gate)
        {
            _links.TryGetValue(link.PhoneId, out previous);
            _links[link.PhoneId] = link;
            _waiting.Remove(link.PhoneId, out waiting);
        }

        previous?.Close("replaced");
        _logger.LogInformation("Phone companion {Name} connected from {Remote}", link.Name, link.RemoteAddress);
        waiting?.TrySetResult(link);
        Changed?.Invoke();
    }

    public void Detach(CompanionLink link)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(link.PhoneId, out var current) || current != link)
            {
                return;
            }
            _links.Remove(link.PhoneId);
        }

        _logger.LogInformation("Phone companion {Name} disconnected", link.Name);
        Changed?.Invoke();
    }

    /// <summary>That phone's connection, now or as soon as it connects.</summary>
    public Task<CompanionLink> WaitForLinkAsync(string phoneId, CancellationToken ct)
    {
        Task<CompanionLink> arrived;
        lock (_gate)
        {
            if (_links.TryGetValue(phoneId, out var link))
            {
                return Task.FromResult(link);
            }

            if (!_waiting.TryGetValue(phoneId, out var waiting))
            {
                waiting = new TaskCompletionSource<CompanionLink>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[phoneId] = waiting;
            }
            arrived = waiting.Task;
        }
        return arrived.WaitAsync(ct);
    }

    /// <summary>A fresh connect code for glasses that don't know their phone. Close it when they stop waiting.</summary>
    public ConnectCode OpenConnectCode()
    {
        lock (_gate)
        {
            string code;
            do
            {
                code = Secrets.NewConnectCode();
            } while (_codes.ContainsKey(code));

            var connect = new ConnectCode(code);
            _codes[code] = connect;
            return connect;
        }
    }

    public void CloseConnectCode(ConnectCode connect)
    {
        lock (_gate)
        {
            if (_codes.TryGetValue(connect.Code, out var current) && current == connect)
            {
                _codes.Remove(connect.Code);
            }
        }
        connect.Completion.TrySetCanceled();
    }

    /// <summary>
    /// A companion typed a connect code: the waiting glasses, taken off the list (single use), or
    /// null when no glasses wait with that code or the phone tried too many. Tell the companion,
    /// then <see cref="Complete"/> it, so its answer arrives before the relay's first message.
    /// </summary>
    public ConnectCode? TakeConnectCode(CompanionLink link, string code)
    {
        lock (_gate)
        {
            if (!_claimLimiter.TryAcquire(link.PhoneId))
            {
                _alerts.Raise(AlertKind.PairingRateLimited, link.RemoteAddress, "Too many connect codes tried by one phone");
                return null;
            }

            return _codes.Remove(code, out var connect) ? connect : null;
        }
    }

    public static void Complete(ConnectCode connect, CompanionLink link) => connect.Completion.TrySetResult(link);

    private bool AllowRegistration(IPAddress remote)
    {
        if (_perIpLimiter.TryAcquire(remote.ToString()) && _globalLimiter.TryAcquire("*"))
        {
            return true;
        }

        _alerts.Raise(AlertKind.PairingRateLimited, remote, "Too many phone pairing requests");
        return false;
    }

    /// <summary>Drops phones unseen for <see cref="CompanionOptions.ForgetAfter"/>; whether there's room now.</summary>
    private bool MakeRoom()
    {
        var cutoff = _time.GetUtcNow() - _options.ForgetAfter;
        foreach (var stale in _phones.Values.Where(p => p.LastSeen < cutoff && !_links.ContainsKey(p.Id)).ToList())
        {
            RemovePhone(stale.Id, out _);
        }
        Save();
        return _phones.Count < _options.MaxPhones;
    }

    private string AddPhone(string name)
    {
        var token = Secrets.NewToken();
        var now = _time.GetUtcNow();
        var phone = new RegisteredPhone(Secrets.NewId(), name, Secrets.HashToken(token), now, now);
        _phones[phone.Id] = phone;
        _idsByHash[Convert.ToHexString(phone.TokenHash)] = phone.Id;
        Save();
        return token;
    }

    private bool RemovePhone(string id, out CompanionLink? link)
    {
        link = null;
        if (!_phones.Remove(id, out var phone))
        {
            return false;
        }

        _idsByHash.Remove(Convert.ToHexString(phone.TokenHash));
        _links.Remove(id, out link);
        return true;
    }

    private void ClearPending()
    {
        _pendingTimer?.Dispose();
        _pendingTimer = null;
        _pending = null;
    }

    private sealed record StoredPhone(string Id, string Name, string TokenHash, DateTimeOffset RegisteredAt, DateTimeOffset LastSeen);

    private sealed record StoredGrant(string Name, string TokenHash, DateTimeOffset PairedAt);

    private void Load()
    {
        try
        {
            if (File.Exists(_phonesFile))
            {
                foreach (var stored in JsonSerializer.Deserialize<List<StoredPhone>>(File.ReadAllText(_phonesFile)) ?? [])
                {
                    var phone = new RegisteredPhone(stored.Id, stored.Name, Convert.FromBase64String(stored.TokenHash),
                        stored.RegisteredAt, stored.LastSeen);
                    _phones[phone.Id] = phone;
                    _idsByHash[Convert.ToHexString(phone.TokenHash)] = phone.Id;
                }
                return;
            }

            // The one phone paired on the PC by earlier versions keeps working: it gets an id here,
            // once. Never on a hosted relay, which may share the machine but not the PC's phone.
            if (_options.Registration == CompanionRegistration.Approve && File.Exists(_grantFile) && JsonSerializer.Deserialize<StoredGrant>(File.ReadAllText(_grantFile)) is { } grant)
            {
                var phone = new RegisteredPhone(Secrets.NewId(), grant.Name, Convert.FromBase64String(grant.TokenHash),
                    grant.PairedAt, grant.PairedAt);
                _phones[phone.Id] = phone;
                _idsByHash[Convert.ToHexString(phone.TokenHash)] = phone.Id;
                Save();
                _logger.LogInformation("Imported the paired phone {Name} from {Path}", grant.Name, _grantFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _phones.Clear();
            _idsByHash.Clear();
            _logger.LogWarning(ex, "Could not read the registered phones from {Path}; they have to register again", _phonesFile);
        }
    }

    /// <summary>Writes the phones out (under the lock): a temporary file, then a rename, so a crash can't leave half a list.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_phonesFile))!);
            var stored = _phones.Values
                .Select(p => new StoredPhone(p.Id, p.Name, Convert.ToBase64String(p.TokenHash), p.RegisteredAt, p.LastSeen))
                .ToList();
            var temp = _phonesFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored));
            File.Move(temp, _phonesFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the registered phones to {Path}", _phonesFile);
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
