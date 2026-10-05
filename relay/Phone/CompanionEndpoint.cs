using System.Net;
using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Phone;

/// <summary>
/// <c>/ws/companion</c>: the phone companion app's socket. It pairs once through the Approve popup
/// (<c>pair</c>) and afterwards authenticates with its token (<c>auth</c>), then stays connected so
/// the glasses can start phone sessions. It carries signalling only.
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
            case CompanionPairMessage pair:
                await PairAsync(io, registry, pair, remote, ct);
                return;
            case CompanionAuthMessage auth when registry.Authenticate(auth.Token):
                await RunAsync(io, registry, settings, alerts, time, remote, logger, ct);
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
    private static async Task RunAsync(SocketIO io, CompanionRegistry registry, CompanionOptions settings,
        AlertLog alerts, TimeProvider time, IPAddress remote, ILogger logger, CancellationToken requestAborted)
    {
        var link = new CompanionLink(io, registry.PairedName ?? "phone", remote);
        registry.Attach(link);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, link.Closed);
        var ct = cts.Token;
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        string? closeReason = null;

        try
        {
            await io.SendAsync(new { type = "authenticated" }, ct);
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
