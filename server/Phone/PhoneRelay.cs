using System.Net.WebSockets;
using System.Threading.Channels;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>Everything a phone session needs, resolved once from DI.</summary>
public sealed record PhoneServices(
    CompanionRegistry Registry,
    IOptions<CompanionOptions> Companion,
    IOptions<ControlSessionOptions> Session,
    AlertLog Alerts,
    TimeProvider Time,
    ILogger<PhoneRelay> Logger);

/// <summary>
/// A glasses session whose target is the phone. The PC is only the meeting point: it asks the
/// companion to start, passes the phone's offer and ICE candidates to the glasses and their
/// answer and candidates back, and ends both sides together. Video and input go straight between
/// phone and glasses (WebRTC); nothing of either passes through here.
///
/// The glasses may send only rtcAnswer, iceCandidate and ping in a phone session.
/// </summary>
public sealed class PhoneRelay
{
    private readonly SocketIO _io;
    private readonly SessionLease _lease;
    private readonly PhoneServices _s;
    private readonly ControlSessionOptions _session;
    private readonly CompanionOptions _companion;
    private volatile CompanionLink? _link;
    private long _lastMessageTimestamp;

    public PhoneRelay(SocketIO io, SessionLease lease, PhoneServices services)
    {
        _io = io;
        _lease = lease;
        _s = services;
        _session = services.Session.Value;
        _companion = services.Companion.Value;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _lease.Ended);
        cts.CancelAfter(_session.MaxDuration);
        var ct = cts.Token;

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "session ended";
        _lastMessageTimestamp = _s.Time.GetTimestamp();
        _s.Logger.LogInformation("Session {Session} controls the phone", _lease.Id);
        try
        {
            var receiving = ReceiveLoopAsync(ct);
            var phone = PhoneLoopAsync(ct);
            var watching = HeartbeatWatchAsync(ct);

            var finished = await Task.WhenAny(receiving, phone, watching);
            if (finished == receiving && await ResultOrNull(receiving) is { } violation)
            {
                closeStatus = WebSocketCloseStatus.PolicyViolation;
                closeReason = violation;
                _lease.ForgetDevice(violation);
            }
            else if (finished != receiving && !ct.IsCancellationRequested && await ResultOrNull(finished) is { } reason)
            {
                closeReason = reason;
            }

            cts.Cancel();
            await Task.WhenAll(Swallow(receiving), Swallow(phone), Swallow(watching));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        catch (Exception ex)
        {
            _s.Logger.LogError(ex, "Phone session {Session} failed", _lease.Id);
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeReason = "server error";
        }
        finally
        {
            if (_lease.Ended.IsCancellationRequested)
            {
                closeReason = _lease.Superseded ? "replaced" : "terminated";
            }
            await _io.CloseQuietlyAsync(closeStatus, closeReason);
            _s.Logger.LogInformation("Phone session {Session} closed: {Reason}", _lease.Id, closeReason);
        }
    }

    /// <summary>
    /// Waits for the companion, starts the phone and relays its signalling to the glasses.
    /// Returns the close reason when the phone side ends the session.
    /// </summary>
    private async Task<string?> PhoneLoopAsync(CancellationToken ct)
    {
        using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startDeadline.CancelAfter(_companion.StartTimeout);

        CompanionLink link;
        try
        {
            if (_s.Registry.Current is null)
            {
                await SendStatusAsync("offline", ct);
            }
            link = await _s.Registry.WaitForLinkAsync(startDeadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "phone offline";
        }

        var inbox = link.BeginSession();
        if (inbox is null)
        {
            return "phone busy";
        }

        _link = link;
        try
        {
            await link.SendAsync(new { type = "sessionStart" }, ct);
            await SendStatusAsync("asking", ct);

            var offered = false;
            using var untilLinkGone = CancellationTokenSource.CreateLinkedTokenSource(ct, link.Closed);
            using var beforeOffer = CancellationTokenSource.CreateLinkedTokenSource(untilLinkGone.Token, startDeadline.Token);
            while (true)
            {
                CompanionMessage message;
                try
                {
                    // Before the offer, the start deadline applies; after it, only the link's life.
                    message = await inbox.ReadAsync(offered ? untilLinkGone.Token : beforeOffer.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
                {
                    ct.ThrowIfCancellationRequested();
                    if (link.Closed.IsCancellationRequested)
                    {
                        return "phone offline";
                    }
                    return "phone not ready";
                }

                switch (message)
                {
                    case CompanionStateMessage { State: PhoneState.Declined }:
                        return "phone declined";
                    case CompanionStateMessage { State: PhoneState.Ended }:
                        return "phone ended";
                    case CompanionStateMessage state:
                        await SendStatusAsync(CompanionProtocol.StateName(state.State), ct);
                        break;
                    case CompanionOfferMessage offer:
                        offered = true;
                        await _io.SendAsync(new { type = "rtcOffer", sdp = offer.Sdp }, ct);
                        break;
                    case CompanionIceMessage ice:
                        await _io.SendAsync(new
                        {
                            type = "iceCandidate",
                            candidate = ice.Candidate.Candidate,
                            sdpMid = ice.Candidate.SdpMid,
                            sdpMLineIndex = ice.Candidate.SdpMLineIndex,
                        }, ct);
                        break;
                }
            }
        }
        finally
        {
            _link = null;
            link.EndSession(inbox);
            if (!link.Closed.IsCancellationRequested)
            {
                // Tell the phone to stop capturing; it may already have.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await link.SendAsync(new { type = "sessionEnd" }, timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>Returns null when the glasses go away, or a reason when they break the rules.</summary>
    private async Task<string?> ReceiveLoopAsync(CancellationToken ct)
    {
        var rate = Math.Max(1, _session.MaxMessagesPerSecond);
        var bucket = new TokenBucket(_s.Time, rate, rate * 2);
        var confirmed = false;

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

            Interlocked.Exchange(ref _lastMessageTimestamp, _s.Time.GetTimestamp());
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
                case PingMessage ping:
                    await _io.SendAsync(new { type = "pong", t = ping.T, serverTime = _s.Time.GetUtcNow().ToUnixTimeMilliseconds() }, ct);
                    break;
                case RtcAnswerMessage answer:
                    if (_link is { } link)
                    {
                        await link.SendAsync(new { type = "rtcAnswer", sdp = answer.Sdp }, ct);
                    }
                    if (!confirmed)
                    {
                        // The answer follows "authenticated" on this socket: the glasses have their new device token.
                        confirmed = true;
                        _lease.ConfirmDeviceToken();
                    }
                    break;
                case IceCandidateMessage ice:
                    if (_link is { } target)
                    {
                        await target.SendAsync(new
                        {
                            type = "iceCandidate",
                            candidate = ice.Candidate,
                            sdpMid = ice.SdpMid,
                            sdpMLineIndex = ice.SdpMLineIndex,
                        }, ct);
                    }
                    break;
                default:
                    // Input goes to the phone over WebRTC, never through the PC.
                    _s.Alerts.Raise(AlertKind.ProtocolViolation, _lease.RemoteAddress,
                        $"{message!.GetType().Name} is not allowed in a phone session");
                    return "invalid message";
            }
        }

        return null;
    }

    private async Task<string?> HeartbeatWatchAsync(CancellationToken ct)
    {
        var check = TimeSpan.FromSeconds(Math.Clamp(_session.HeartbeatTimeout.TotalSeconds / 4, 0.25, 30));
        while (true)
        {
            await Task.Delay(check, _s.Time, ct);
            var silentFor = _s.Time.GetElapsedTime(Interlocked.Read(ref _lastMessageTimestamp));
            if (silentFor >= _session.HeartbeatTimeout)
            {
                return "no heartbeat";
            }
        }
    }

    private Task SendStatusAsync(string state, CancellationToken ct) =>
        _io.SendAsync(new { type = "phoneStatus", state }, ct);

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
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException
                                       or ChannelClosedException)
        {
        }
    }
}
