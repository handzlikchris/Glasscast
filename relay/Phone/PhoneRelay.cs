using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>Everything a phone relay needs, resolved once from DI.</summary>
public sealed record PhoneServices(
    CompanionRegistry Registry,
    IOptions<CompanionOptions> Companion,
    AlertLog Alerts,
    TimeProvider Time,
    ILogger<PhoneRelay> Logger);

/// <summary>
/// Glasses that want their phone (first message <c>phone</c>, with the phone's id once they know
/// it; without, they get a connect code to type into the companion). The server is only the meeting point:
/// the glasses' page can't reach the phone until a WebRTC connection exists, so the first
/// messages go through here. It passes pairing, authentication and signalling between the glasses
/// and the companion, and decides nothing: the phone pairs the glasses (approval on the phone),
/// checks them on every session, and asks for its own capture consent. Keys and MACs pass through
/// as opaque strings; the PC holds no secret of the glasses' or the phone's.
///
/// Once the video is up the glasses close the relay, and the session goes on without the PC:
/// losing this PC (or its internet) doesn't end it (architecture/phone-mode.md).
///
/// The glasses may send only pairing, authentication, signalling and ping messages here: never
/// input, which goes straight to the phone over WebRTC.
/// </summary>
public sealed class PhoneRelay
{
    private readonly SocketIO _io;
    private readonly IPAddress _remote;
    private readonly string? _phoneId;
    private readonly PhoneServices _s;
    private readonly CompanionOptions _companion;

    public PhoneRelay(SocketIO io, IPAddress remote, string? phoneId, PhoneServices services)
    {
        _io = io;
        _remote = remote;
        _phoneId = phoneId;
        _s = services;
        _companion = services.Companion.Value;
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        // Finding the phone has its own limit (StartTimeout, in WaitForPhoneAsync); the rest of the
        // relay's life (RelayTimeout) starts once it's found, so a slowly typed code leaves the
        // pairing and the consent their full time.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = cts.Token;

        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "relay ended";
        CompanionLink? link = null;
        RelayHandle? relay = null;
        try
        {
            link = await WaitForPhoneAsync(ct);
            if (link is null)
            {
                closeReason = "phone offline";
                return;
            }
            cts.CancelAfter(_companion.RelayTimeout);

            relay = link.OpenRelay();
            using var untilGone = CancellationTokenSource.CreateLinkedTokenSource(ct, link.Closed, relay.Replaced);
            await link.SendAsync(new { type = "relayOpen" }, untilGone.Token);
            await _io.SendAsync(new { type = "phoneStatus", state = "ready" }, untilGone.Token);
            _s.Logger.LogInformation("Glasses from {Remote} relayed to the phone", _remote);

            var fromGlasses = FromGlassesAsync(link, untilGone.Token);
            var fromPhone = FromPhoneAsync(relay, untilGone.Token);
            var finished = await Task.WhenAny(fromGlasses, fromPhone);
            var reason = await ResultOrNull(finished);
            if (finished == fromGlasses && reason is not null)
            {
                closeStatus = WebSocketCloseStatus.PolicyViolation;
            }

            closeReason = reason
                          ?? (relay.Replaced.IsCancellationRequested ? "replaced"
                              : link.Closed.IsCancellationRequested ? "phone offline"
                              : ct.IsCancellationRequested && !requestAborted.IsCancellationRequested ? "timeout"
                              : "relay ended");
            untilGone.Cancel();
            await Task.WhenAll(Swallow(fromGlasses), Swallow(fromPhone));
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            if (relay?.Replaced.IsCancellationRequested == true)
            {
                closeReason = "replaced";
            }
            else if (link?.Closed.IsCancellationRequested == true)
            {
                closeReason = "phone offline";
            }
            else if (!requestAborted.IsCancellationRequested && ct.IsCancellationRequested)
            {
                closeReason = "timeout";
            }
        }
        catch (Exception ex)
        {
            _s.Logger.LogError(ex, "Phone relay for {Remote} failed", _remote);
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeReason = "server error";
        }
        finally
        {
            if (link is not null && relay is not null && link.CloseRelay(relay) && !link.Closed.IsCancellationRequested)
            {
                // The phone drops anything the glasses hadn't finished (a pairing prompt, a consent
                // dialog, a connection not yet up). A session already live carries on without us.
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await link.SendAsync(new { type = "relayClosed" }, timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                }
            }
            await _io.CloseQuietlyAsync(closeStatus, closeReason);
            _s.Logger.LogInformation("Phone relay for {Remote} closed: {Reason}", _remote, closeReason);
        }
    }

    /// <summary>
    /// The glasses' phone, connected: the one they named, now or once it connects (null if it
    /// doesn't within the start timeout); else the PC's only phone; else whichever companion
    /// someone types the connect code into, within the relay's life.
    /// </summary>
    private async Task<CompanionLink?> WaitForPhoneAsync(CancellationToken ct)
    {
        var phoneId = _phoneId is { } named && _s.Registry.IsRegistered(named) ? named : _s.Registry.SolePhone;
        if (phoneId is null)
        {
            return await WaitForClaimAsync(ct);
        }

        if (phoneId != _phoneId)
        {
            await _io.SendAsync(new { type = "phoneFound", phone = phoneId }, ct);
        }

        if (_s.Registry.Connected(phoneId) is { } now)
        {
            return now;
        }

        // Offline, or gone for good: a companion that was unpaired (or reinstalled) and registered
        // again is a new phone here, and the glasses would wait for the old one forever. So they
        // also get a connect code; whichever comes first wins: that phone back, or a claim.
        await _io.SendAsync(new { type = "phoneStatus", state = "offline" }, ct);
        var connect = _s.Registry.OpenConnectCode();
        try
        {
            await _io.SendAsync(new { type = "connectCode", code = connect.Code }, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_companion.StartTimeout);
            var back = _s.Registry.WaitForLinkAsync(phoneId, deadline.Token);
            var claimed = connect.Claimed.WaitAsync(deadline.Token);
            var first = await Task.WhenAny(back, claimed);
            var link = await first;
            if (first == claimed)
            {
                await _io.SendAsync(new { type = "phoneFound", phone = link.PhoneId }, ct);
                _s.Logger.LogInformation("Glasses from {Remote} found their phone {Name} by connect code", _remote, link.Name);
            }
            return link;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            _s.Registry.CloseConnectCode(connect);
        }
    }

    /// <summary>Shows a connect code on the glasses and waits for a companion to claim it; null after StartTimeout.</summary>
    private async Task<CompanionLink?> WaitForClaimAsync(CancellationToken ct)
    {
        var connect = _s.Registry.OpenConnectCode();
        try
        {
            await _io.SendAsync(new { type = "connectCode", code = connect.Code }, ct);

            // Like the wait for an offline phone, nothing is read from the glasses meanwhile (one
            // reader per socket); their pings are answered once the phone is found.
            CompanionLink link;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(_companion.StartTimeout);
                try
                {
                    link = await connect.Claimed.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            }
            await _io.SendAsync(new { type = "phoneFound", phone = link.PhoneId }, ct);
            _s.Logger.LogInformation("Glasses from {Remote} found their phone {Name} by connect code", _remote, link.Name);
            return link;
        }
        finally
        {
            _s.Registry.CloseConnectCode(connect);
        }
    }

    /// <summary>
    /// Passes the phone's messages to the glasses. Returns a close reason when the phone ends the
    /// relay (declined or stopped before the video was up), or null when its messages stop.
    /// </summary>
    private async Task<string?> FromPhoneAsync(RelayHandle relay, CancellationToken ct)
    {
        await foreach (var message in relay.Inbox.ReadAllAsync(ct))
        {
            object? forward = message switch
            {
                CompanionStateMessage { State: PhoneState.Declined } => null,
                CompanionStateMessage { State: PhoneState.Ended } => null,
                CompanionStateMessage state => new { type = "phoneStatus", state = CompanionProtocol.StateName(state.State) },
                CompanionPairKeyMessage pairKey => new { type = "pairKey", key = pairKey.Key },
                CompanionPairedMessage => new { type = "paired" },
                CompanionPairFailedMessage => new { type = "pairFailed" },
                CompanionChallengeMessage challenge => new { type = "challenge", nonce = challenge.Nonce, mac = challenge.Mac },
                CompanionAuthFailedMessage => new { type = "authFailed" },
                CompanionOfferMessage offer => new { type = "rtcOffer", sdp = offer.Sdp, mac = offer.Mac },
                CompanionIceMessage ice => new
                {
                    type = "iceCandidate",
                    candidate = ice.Candidate.Candidate,
                    sdpMid = ice.Candidate.SdpMid,
                    sdpMLineIndex = ice.Candidate.SdpMLineIndex,
                },
                _ => null,
            };

            switch (message)
            {
                case CompanionStateMessage { State: PhoneState.Declined }:
                    return "phone declined";
                case CompanionStateMessage { State: PhoneState.Ended }:
                    return "phone ended";
            }

            if (forward is not null)
            {
                await _io.SendAsync(forward, ct);
            }
        }

        return null;
    }

    /// <summary>Passes the glasses' messages to the phone. Returns null when they leave, or a reason when they break the rules.</summary>
    private async Task<string?> FromGlassesAsync(CompanionLink link, CancellationToken ct)
    {
        var rate = Math.Max(1, _companion.RelayMaxMessagesPerSecond);
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
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _remote, ex.Message);
                return "invalid message";
            }

            if (raw is null)
            {
                return null;
            }

            if (!bucket.TryTake())
            {
                _s.Alerts.Raise(AlertKind.MessageRateLimited, _remote, "Phone relay exceeded the message rate");
                return "rate limit";
            }

            if (!RelayProtocol.TryParse(raw.Value.Span, out var message, out var error))
            {
                // Input never goes through the PC for the phone, nor does anything else unexpected.
                _s.Alerts.Raise(AlertKind.ProtocolViolation, _remote, $"Phone relay message rejected ({error})");
                return "invalid message";
            }

            object forward = message switch
            {
                RelayPingSignal ping => new { type = "pong", t = ping.T, serverTime = _s.Time.GetUtcNow().ToUnixTimeMilliseconds() },
                PairStartSignal start => new { type = "pairStart", commit = start.Commit },
                PairRevealSignal reveal => new { type = "pairReveal", key = reveal.Key },
                HelloSignal hello => new { type = "hello", id = hello.Id, nonce = hello.Nonce },
                ProofSignal proof => new { type = "proof", mac = proof.Mac },
                AnswerSignal answer => new { type = "rtcAnswer", sdp = answer.Sdp, mac = answer.Mac },
                GlassesIceSignal ice => new
                {
                    type = "iceCandidate",
                    candidate = ice.Candidate.Candidate,
                    sdpMid = ice.Candidate.SdpMid,
                    sdpMLineIndex = ice.Candidate.SdpMLineIndex,
                },
                _ => throw new InvalidOperationException("unhandled relay message"),
            };

            if (message is RelayPingSignal)
            {
                await _io.SendAsync(forward, ct);
            }
            else
            {
                await link.SendAsync(forward, ct);
            }
        }

        return null;
    }

    private static async Task<string?> ResultOrNull(Task<string?> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException
                                       or ChannelClosedException)
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
