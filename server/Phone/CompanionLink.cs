using System.Net;
using System.Threading.Channels;
using GlassesRemote.Server.Sessions;

namespace GlassesRemote.Server.Phone;

/// <summary>
/// One authenticated companion connection. At most one glasses session (a <see cref="PhoneRelay"/>)
/// uses it at a time; the companion's signalling messages go to that session's inbox.
/// </summary>
public sealed class CompanionLink
{
    private readonly SocketIO _io;
    private readonly CancellationTokenSource _closed = new();
    private readonly object _gate = new();
    private Channel<CompanionMessage>? _inbox;

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

    /// <summary>Starts a session on this phone; null if one is already running.</summary>
    public ChannelReader<CompanionMessage>? BeginSession()
    {
        lock (_gate)
        {
            if (_inbox is not null)
            {
                return null;
            }

            _inbox = Channel.CreateBounded<CompanionMessage>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            return _inbox.Reader;
        }
    }

    public void EndSession(ChannelReader<CompanionMessage> reader)
    {
        lock (_gate)
        {
            if (_inbox?.Reader == reader)
            {
                _inbox.Writer.TryComplete();
                _inbox = null;
            }
        }
    }

    public bool InSession
    {
        get
        {
            lock (_gate)
            {
                return _inbox is not null;
            }
        }
    }

    /// <summary>Hands a signalling message to the running session. Without one it is stale: dropped.</summary>
    internal void Deliver(CompanionMessage message)
    {
        lock (_gate)
        {
            _inbox?.Writer.TryWrite(message);
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
            _inbox?.Writer.TryComplete();
        }
    }
}
