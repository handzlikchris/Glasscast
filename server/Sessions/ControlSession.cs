using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Protocol;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Sessions;

/// <summary>Everything a control session needs, resolved once from DI.</summary>
public sealed record SessionServices(
    IOptions<ControlSessionOptions> Options,
    IScreen Screen,
    RegionStore RegionStore,
    IInputInjector Input,
    IKeepAwake KeepAwake,
    IMediaPeerFactory Peers,
    IFrameEncoderFactory Encoders,
    FramePump Pump,
    AlertLog Alerts,
    TimeProvider Time,
    ILogger<ControlSession> Logger);

/// <summary>
/// One authenticated session: WebRTC signalling, video, and input, until the
/// glasses disconnect, the PC terminates it, or a limit is hit.
/// </summary>
public sealed class ControlSession
{
    private readonly SocketIO _io;
    private readonly SessionLease _lease;
    private readonly SessionServices _s;
    private readonly ControlSessionOptions _options;
    private long _lastInputTimestamp;

    public ControlSession(SocketIO io, SessionLease lease, SessionServices services)
    {
        _io = io;
        _lease = lease;
        _s = services;
        _options = services.Options.Value;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _lease.Ended);
        cts.CancelAfter(_options.MaxDuration);
        var ct = cts.Token;

        using var awake = _s.KeepAwake.Acquire();
        var controller = new InputController(_s.Input, _s.RegionStore, _s.Screen.PrimarySize, _s.RegionStore.Load());
        using var encoder = _s.Encoders.Create();
        using var peer = _s.Peers.Create(encoder.Codec);
        peer.Closed += () => SafeCancel(cts);

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "session ended";
        try
        {
            await _io.SendAsync(new
            {
                type = "hello",
                monitor = new { width = controller.Monitor.Width, height = controller.Monitor.Height },
                region = controller.Region,
                mode = "view",
                codec = encoder.Codec,
            }, ct);

            await _io.SendAsync(new { type = "rtcOffer", sdp = await peer.CreateOfferAsync() }, ct);

            _lastInputTimestamp = _s.Time.GetTimestamp();
            var receiving = ReceiveLoopAsync(controller, peer, ct);
            var streaming = _s.Pump.RunAsync(peer, encoder, () => controller.CurrentSource, null, ct);
            var watching = IdleWatchAsync(ct);

            var finished = await Task.WhenAny(receiving, streaming, watching);
            if (finished == receiving && await ResultOrNull(receiving) is { } violation)
            {
                closeStatus = WebSocketCloseStatus.PolicyViolation;
                closeReason = violation;
            }
            else if (finished == watching && !ct.IsCancellationRequested)
            {
                closeReason = "idle";
            }

            SafeCancel(cts);
            await Task.WhenAll(Swallow(receiving), Swallow(streaming), Swallow(watching));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Disconnect, terminate or limit: fall through to close.
        }
        finally
        {
            if (_lease.Ended.IsCancellationRequested)
            {
                closeReason = "terminated";
            }
            await _io.CloseQuietlyAsync(closeStatus, closeReason);
            _s.Logger.LogInformation("Session {Session} closed: {Reason}", _lease.Id, closeReason);
        }
    }

    /// <summary>Returns null when the client goes away, or a reason when it breaks the rules.</summary>
    private async Task<string?> ReceiveLoopAsync(InputController controller, IMediaPeer peer, CancellationToken ct)
    {
        var rate = Math.Max(1, _options.MaxMessagesPerSecond);
        var bucket = new TokenBucket(_s.Time, rate, rate * 2);

        while (!ct.IsCancellationRequested)
        {
            ReadOnlyMemory<byte>? raw;
            try
            {
                raw = await _io.ReceiveAsync(ct);
            }
            catch (InvalidClientMessageException ex)
            {
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, ex.Message);
                return "invalid message";
            }

            if (raw is null)
            {
                return null;
            }

            if (!bucket.TryTake())
            {
                _s.Alerts.Raise(AlertKind.MessageRateLimited, _lease.RemoteAddress, "Session exceeded the message rate");
                return "rate limit";
            }

            if (!ControlProtocol.TryParse(raw.Value.Span, out var message, out var error))
            {
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, $"Rejected message ({error})");
                return "invalid message";
            }

            switch (message)
            {
                case AuthenticateMessage:
                    _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress, "Repeated authenticate");
                    return "invalid message";

                case RtcAnswerMessage answer:
                    if (!peer.ApplyAnswer(answer.Sdp))
                    {
                        return "bad answer";
                    }
                    break;

                case IceCandidateMessage candidate:
                    peer.AddRemoteCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
                    break;

                case PingMessage ping:
                    await _io.SendAsync(new { type = "pong", t = ping.T, serverTime = _s.Time.GetUtcNow().ToUnixTimeMilliseconds() }, ct);
                    break;

                default:
                    Interlocked.Exchange(ref _lastInputTimestamp, _s.Time.GetTimestamp());
                    if (controller.Handle(message!) == HandleResult.RegionChanged)
                    {
                        await _io.SendAsync(new { type = "region", region = controller.Region }, ct);
                    }
                    break;
            }
        }

        return null;
    }

    private async Task IdleWatchAsync(CancellationToken ct)
    {
        var check = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, _options.IdleTimeout.TotalSeconds / 4)));
        while (true)
        {
            await Task.Delay(check, _s.Time, ct);
            var idleFor = _s.Time.GetElapsedTime(Interlocked.Read(ref _lastInputTimestamp));
            if (idleFor >= _options.IdleTimeout)
            {
                _s.Logger.LogInformation("Session {Session} idle for {Idle}; closing", _lease.Id, idleFor);
                return;
            }
        }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ResultOrNull(Task<string?> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }
}
