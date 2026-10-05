using System.Net;
using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>
/// <c>/ws/companion</c>: the phone companion app's socket. It registers once (<c>pair</c>: through
/// the Approve popup on the PC, at once on a hosted relay) and afterwards authenticates with its
/// token (<c>auth</c>), then stays connected so glasses can start phone sessions. It carries
/// signalling only, and the connect codes typed into the companion (<c>claim</c>).
///
/// The app is not a browser, so it sends no Origin. A request that has one came from a web page
/// and is refused: no site can open this socket from someone's browser.
/// </summary>
public static class CompanionEndpoint
{
    public static void MapCompanionEndpoint(this WebApplication app) => app.Map("/ws/companion", CompanionAsync);

    private static async Task CompanionAsync(HttpContext context, CompanionRegistry registry,
        IOptions<CompanionOptions> options, AlertLog alerts, TimeProvider time, ILogger<CompanionLink> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;
        if (!string.IsNullOrEmpty(context.Request.Headers.Origin.ToString()))
        {
            alerts.Raise(AlertKind.BadOrigin, remote, "Companion socket refused: the request came from a web page");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.CompleteAsync();
            return;
        }

        var settings = options.Value;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var io = new SocketIO(socket, CompanionProtocol.MaxMessageBytes);
        var ct = context.RequestAborted;

        CompanionMessage? first;
        using (var deadline = new CancellationTokenSource(settings.AuthTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct))
        {
            try
            {
                var raw = await io.ReceiveAsync(linked.Token);
                if (raw is null)
                {
                    return;
                }
                CompanionProtocol.TryParse(raw.Value.Span, out first, out _);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                alerts.Raise(AlertKind.AuthenticationTimedOut, remote, "Companion sent nothing within the deadline");
                await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "authFailed");
                return;
            }
            catch (InvalidClientMessageException ex)
            {
                alerts.Raise(AlertKind.ProtocolViolation, remote, $"Companion: {ex.Message}");
                return;
            }
        }

        switch (first)
        {
            case CompanionPairMessage pair when registry.Registration == CompanionRegistration.Open:
                await RegisterAsync(io, registry, pair, remote, ct);
                return;
            case CompanionPairMessage pair:
                await PairAsync(io, registry, pair, remote, ct);
                return;
            case CompanionAuthMessage auth when registry.Authenticate(auth.Token) is { } phone:
                await RunAsync(io, registry, phone, settings, alerts, time, remote, logger, ct);
                return;
            case CompanionAuthMessage:
                alerts.Raise(AlertKind.AuthenticationFailed, remote, "Companion presented an unknown token");
                await FailAsync(io, "authFailed", ct);
                return;
            default:
                alerts.Raise(AlertKind.ProtocolViolation, remote, "First companion message was not pair or auth");
                await FailAsync(io, "authFailed", ct);
                return;
        }
    }

    /// <summary>Open registration: the token at once, or a generic failure when the limits are hit.</summary>
    private static async Task RegisterAsync(SocketIO io, CompanionRegistry registry, CompanionPairMessage pair,
        IPAddress remote, CancellationToken ct)
    {
        if (registry.TryRegister(remote, pair.Name) is not { } token)
        {
            await FailAsync(io, "pairFailed", ct);
            return;
        }

        try
        {
            await io.SendAsync(new { type = "paired", token }, ct);
            await io.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, "paired");
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }

    private static async Task PairAsync(SocketIO io, CompanionRegistry registry, CompanionPairMessage pair,
        IPAddress remote, CancellationToken ct)
    {
        var pending = registry.TryOpenRequest(remote, pair.Name);
        if (pending is null)
        {
            await FailAsync(io, "pairFailed", ct);
            return;
        }

        var request = pending.Request;
        try
        {
            var seconds = (int)Math.Ceiling((request.ExpiresAt - request.CreatedAt).TotalSeconds);
            await io.SendAsync(new { type = "pairCode", code = request.Code, expiresInSeconds = seconds }, ct);

            // The phone just waits. Anything it sends, or leaving, cancels the request.
            var gone = WaitUntilGoneAsync(io, ct);
            if (await Task.WhenAny(request.Outcome, gone) == gone)
            {
                registry.Cancel(request.Id);
                await io.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, "cancelled");
                return;
            }

            var outcome = await request.Outcome;
            if (outcome.Kind == PairingOutcomeKind.Approved)
            {
                await io.SendAsync(new { type = "paired", token = outcome.Token }, ct);
                await io.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, "paired");
            }
            else
            {
                await FailAsync(io, "pairFailed", ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            registry.Cancel(request.Id);
        }
    }

    /// <summary>An authenticated companion: stays until it leaves, goes quiet, breaks the rules or is replaced.</summary>
    private static async Task RunAsync(SocketIO io, CompanionRegistry registry, RegisteredPhone phone,
        CompanionOptions settings, AlertLog alerts, TimeProvider time, IPAddress remote, ILogger logger,
        CancellationToken requestAborted)
    {
        // "authenticated" goes first: the companion ignores anything before it, and attaching wakes
        // any glasses already waiting for this phone, whose relayOpen would otherwise race it.
        try
        {
            await io.SendAsync(new { type = "authenticated" }, requestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            return;
        }

        var link = new CompanionLink(io, phone.Id, phone.Name, remote);
        registry.Attach(link);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, link.Closed);
        var ct = cts.Token;
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        string? closeReason = null;

        try
        {
            var rate = Math.Max(1, settings.MaxMessagesPerSecond);
            var bucket = new TokenBucket(time, rate, rate * 2);

            while (!ct.IsCancellationRequested)
            {
                ReadOnlyMemory<byte>? raw;
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    quiet.CancelAfter(settings.HeartbeatTimeout);
                    try
                    {
                        raw = await io.ReceiveAsync(quiet.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        closeReason = "no heartbeat";
                        break;
                    }
                }

                if (raw is null)
                {
                    break;
                }

                if (!bucket.TryTake())
                {
                    alerts.Raise(AlertKind.MessageRateLimited, remote, "Companion exceeded the message rate");
                    (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, "rate limit");
                    break;
                }

                if (!CompanionProtocol.TryParse(raw.Value.Span, out var message, out var error)
                    || message is CompanionPairMessage or CompanionAuthMessage)
                {
                    alerts.Raise(AlertKind.ProtocolViolation, remote, $"Companion message rejected ({error ?? "repeated auth"})");
                    (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, "invalid message");
                    break;
                }

                if (message is CompanionPingMessage ping)
                {
                    await io.SendAsync(new { type = "pong", t = ping.T }, ct);
                }
                else if (message is CompanionClaimMessage claim)
                {
                    // Answer first, then wake the glasses: their relayOpen follows "claimed".
                    var connect = registry.TakeConnectCode(link, claim.Code);
                    await io.SendAsync(new { type = connect is null ? "claimFailed" : "claimed" }, ct);
                    if (connect is not null)
                    {
                        CompanionRegistry.Complete(connect, link);
                    }
                }
                else
                {
                    link.Deliver(message!);
                }
            }
        }
        catch (InvalidClientMessageException ex)
        {
            alerts.Raise(AlertKind.ProtocolViolation, remote, $"Companion: {ex.Message}");
            (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, "invalid message");
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            closeReason ??= link.CloseReason ?? "closed";
            link.Close(closeReason);
            registry.Detach(link);
            await io.CloseQuietlyAsync(closeStatus, closeReason);
            logger.LogInformation("Phone companion connection closed: {Reason}", closeReason);
        }
    }

    private static async Task FailAsync(SocketIO io, string type, CancellationToken ct)
    {
        try
        {
            await io.SendAsync(new { type }, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        await io.CloseQuietlyAsync(WebSocketCloseStatus.NormalClosure, type);
    }

    private static async Task WaitUntilGoneAsync(SocketIO io, CancellationToken ct)
    {
        try
        {
            await io.ReceiveAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidClientMessageException)
        {
        }
    }
}
