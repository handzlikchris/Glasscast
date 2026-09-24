using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GlassesRemote.Server.Sessions;

/// <summary>Size-capped text-message reads and serialised JSON writes on a WebSocket.</summary>
public sealed class SocketIO
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebSocket _socket;
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _buffer;

    public SocketIO(WebSocket socket, int maxMessageBytes)
    {
        _socket = socket;
        _maxMessageBytes = maxMessageBytes;
        _buffer = new byte[maxMessageBytes];
    }

    public WebSocket Socket => _socket;

    /// <summary>
    /// Reads one complete text message. Returns null when the peer closes.
    /// Oversized or binary messages close the socket and throw <see cref="ProtocolViolationException"/>.
    /// </summary>
    public async Task<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken ct)
    {
        var length = 0;
        while (true)
        {
            if (length >= _maxMessageBytes)
            {
                await CloseQuietlyAsync(WebSocketCloseStatus.MessageTooBig, "too big");
                throw new ProtocolViolationException("message too large");
            }

            var result = await _socket.ReceiveAsync(_buffer.AsMemory(length), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await CloseQuietlyAsync(WebSocketCloseStatus.InvalidMessageType, "text only");
                throw new ProtocolViolationException("binary message");
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return _buffer.AsMemory(0, length);
            }
        }
    }

    public async Task SendAsync(object message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseQuietlyAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(status, reason, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _socket.Abort();
        }
    }
}

public sealed class ProtocolViolationException(string message) : Exception(message);
