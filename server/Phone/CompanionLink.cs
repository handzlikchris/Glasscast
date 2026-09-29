using System.Net;
using System.Threading.Channels;
using GlassesRemote.Server.Sessions;

namespace GlassesRemote.Server.Phone;

/// <summary>
/// A glasses connection relayed to the phone: the companion's messages for it arrive in
/// <see cref="Inbox"/>. <see cref="Replaced"/> fires when newer glasses open a relay.
/// </summary>
public sealed class RelayHandle
{
    private readonly CancellationTokenSource _replaced = new();

    internal RelayHandle()
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<CompanionMessage>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    internal Channel<CompanionMessage> Channel { get; }

    public ChannelReader<CompanionMessage> Inbox => Channel.Reader;

    public CancellationToken Replaced => _replaced.Token;

    internal void Replace()
    {
        _replaced.Cancel();
        Channel.Writer.TryComplete();
    }
}

/// <summary>
/// One authenticated companion connection. At most one glasses relay (a <see cref="PhoneRelay"/>)
/// uses it at a time, and the newest wins: the phone itself decides whether those glasses may
/// in. The companion's signalling messages go to that relay's inbox.
/// </summary>
public sealed class CompanionLink
{
    private readonly SocketIO _io;
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private RelayHandle? _relay;

    public CompanionLink(SocketIO io, string name, IPAddress remoteAddress)
    {
        _io = io;
        Name = name;
        RemoteAddress = remoteAddress;
    }

    public string Name { get; }

    public IPAddress RemoteAddress { get; }

    /// <summary>Fires when the connection goes, or the PC drops it (replaced, forgotten).</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Why the PC dropped it, for the close frame; null when the phone left.</summary>
    public string? CloseReason { get; private set; }

    public Task SendAsync(object message, CancellationToken ct) => _io.SendAsync(message, ct);

    /// <summary>Opens a relay for newly connected glasses; an older one is replaced.</summary>
    public RelayHandle OpenRelay()
    {
        RelayHandle? previous;
        var relay = new RelayHandle();
        lock (_gate)
        {
            previous = _relay;
            _relay = relay;
        }
        previous?.Replace();
        return relay;
    }

    /// <summary>Whether this relay is still the one the phone's messages go to.</summary>
    public bool CloseRelay(RelayHandle relay)
    {
        lock (_gate)
        {
            if (_relay != relay)
            {
                return false;
            }
            _relay = null;
        }
        relay.Channel.Writer.TryComplete();
        return true;
    }

    public bool HasRelay
    {
        get
        {
            lock (_gate)
            {
                return _relay is not null;
            }
        }
    }

    /// <summary>Hands a message to the open relay. Without one it is stale: dropped.</summary>
    internal void Deliver(CompanionMessage message)
    {
        lock (_gate)
        {
            _relay?.Channel.Writer.TryWrite(message);
        }
    }

    internal void Close(string? reason)
    {
        CloseReason ??= reason;
        try
        {
            _closed.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            _relay?.Channel.Writer.TryComplete();
        }
    }
}
