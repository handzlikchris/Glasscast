using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace WebRtcSpike;

/// <summary>
/// One browser connection: plain-WebSocket signalling plus a send-only VP8
/// video track carrying the synthetic test pattern. No authentication; the
/// spike never touches the real desktop.
/// </summary>
public sealed class SpikeSession
{
    private const int MaxMessageBytes = 64 * 1024;
    private const int RtpClockRate = 90_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebSocket _socket;
    private readonly SpikeOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public SpikeSession(WebSocket socket, SpikeOptions options, ILogger logger)
    {
        _socket = socket;
        _options = options;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(_options.MaxSessionMinutes));

        var peer = new RTCPeerConnection(new RTCConfiguration(), bindPort: _options.MediaPort);
        try
        {
            var track = new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.VP8, 96), MediaStreamStatusEnum.SendOnly);
            peer.addTrack(track);

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.onconnectionstatechange += state =>
            {
                _logger.LogInformation("Peer connection state: {State}", state);
                if (state == RTCPeerConnectionState.connected)
                {
                    connected.TrySetResult();
                }
                else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    cts.Cancel();
                }
            };
            peer.oniceconnectionstatechange += state => _logger.LogInformation("ICE state: {State}", state);

            var offer = peer.createOffer();
            await peer.setLocalDescription(offer);

            var publicIp = IPAddress.TryParse(_options.PublicIp, out var ip) ? ip : null;
            var sdp = SdpCandidates.Rewrite(offer.sdp, publicIp, _options.MediaPort, _options.IncludeLanCandidates);
            await SendAsync(new { type = "offer", sdp }, cts.Token);
            _logger.LogInformation("Offer sent (public candidate: {PublicIp}:{Port}, LAN candidates: {Lan})",
                publicIp?.ToString() ?? "none", _options.MediaPort, _options.IncludeLanCandidates);

            var receiving = ReceiveLoopAsync(peer, cts.Token);
            var streaming = StreamTestPatternAsync(peer, connected.Task, cts.Token);

            await Task.WhenAny(receiving, streaming);
            cts.Cancel();
            await Task.WhenAll(Swallow(receiving), Swallow(streaming));
        }
        finally
        {
            peer.close();
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            _logger.LogInformation("Spike session ended");
        }
    }

    private async Task StreamTestPatternAsync(RTCPeerConnection peer, Task connected, CancellationToken ct)
    {
        await connected.WaitAsync(ct);

        using var renderer = new TestPatternRenderer();
        using var encoder = new VpxVideoEncoder { TargetKbps = (uint)_options.TargetKbps };
        encoder.ForceKeyFrame();

        var fps = Math.Clamp(_options.FramesPerSecond, 1, 60);
        var rtpDuration = (uint)(RtpClockRate / fps);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));

        long frameNumber = 0;
        var window = Stopwatch.StartNew();
        int framesInWindow = 0;
        long bytesInWindow = 0;
        double encodeMsInWindow = 0;

        while (await timer.WaitForNextTickAsync(ct))
        {
            var serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var started = Stopwatch.GetTimestamp();

            var bgra = renderer.Render(frameNumber++, serverTime);
            var encoded = encoder.EncodeVideo(TestPatternRenderer.Width, TestPatternRenderer.Height, bgra,
                VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

            encodeMsInWindow += Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (encoded is { Length: > 0 })
            {
                peer.SendVideo(rtpDuration, encoded);
                framesInWindow++;
                bytesInWindow += encoded.Length;
            }

            if (window.ElapsedMilliseconds >= 1000)
            {
                var seconds = window.Elapsed.TotalSeconds;
                await SendAsync(new
                {
                    type = "serverStats",
                    fps = Math.Round(framesInWindow / seconds, 1),
                    kbps = Math.Round(bytesInWindow * 8 / 1000.0 / seconds),
                    renderEncodeMs = framesInWindow > 0 ? Math.Round(encodeMsInWindow / framesInWindow, 2) : 0,
                    frames = frameNumber,
                }, ct);

                window.Restart();
                framesInWindow = 0;
                bytesInWindow = 0;
                encodeMsInWindow = 0;
            }
        }
    }

    private async Task ReceiveLoopAsync(RTCPeerConnection peer, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes];

        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length >= buffer.Length)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", ct);
                    return;
                }
                result = await _socket.ReceiveAsync(buffer.AsMemory(length), ct);
                length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            await HandleMessageAsync(peer, buffer.AsMemory(0, length), ct);
        }
    }

    private async Task HandleMessageAsync(RTCPeerConnection peer, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "answer":
                var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = root.GetProperty("sdp").GetString(),
                });
                _logger.LogInformation("Answer applied: {Result}", result);
                if (result != SetDescriptionResultEnum.OK)
                {
                    await SendAsync(new { type = "error", message = $"answer rejected: {result}" }, ct);
                }
                break;

            case "candidate":
                var candidate = root.GetProperty("candidate").GetString();
                if (!string.IsNullOrEmpty(candidate))
                {
                    peer.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candidate,
                        sdpMid = root.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                        sdpMLineIndex = root.TryGetProperty("sdpMLineIndex", out var idx) && idx.ValueKind == JsonValueKind.Number
                            ? idx.GetUInt16()
                            : (ushort)0,
                    });
                }
                break;

            case "ping":
                await SendAsync(new
                {
                    type = "pong",
                    t = root.GetProperty("t").GetDouble(),
                    serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }, ct);
                break;

            default:
                _logger.LogWarning("Ignoring unknown message type {Type}", type);
                break;
        }
    }

    private async Task SendAsync(object message, CancellationToken ct)
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

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }
}
