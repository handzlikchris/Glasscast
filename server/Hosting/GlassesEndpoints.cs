using System.Net;
using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Hosting;

/// <summary>
/// The only public endpoints: health check, pairing socket and session socket (the phone
/// companion's socket is in <see cref="CompanionEndpoint"/>).
/// Failures are deliberately generic ("pairFailed" / "authFailed"): the client
/// can't tell a busy server from a rejection, a timeout or a bad token.
/// </summary>
public static class GlassesEndpoints
{
    private const int PairSocketMaxMessageBytes = 1024;

    public static void MapGlassesEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Text("ok"));
        app.Map("/ws/pair", PairAsync);
        app.Map("/ws/session", SessionAsync);
    }

    private static async Task PairAsync(HttpContext context, PairingCoordinator coordinator,
        IOptions<WebOptions> web, AlertLog alerts)
    {
        if (!await WebHosting.AcceptGuardAsync(context, web.Value, alerts))
        {
            return;
        }

        var remote = WebHosting.RemoteAddress(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var io = new SocketIO(socket, PairSocketMaxMessageBytes);
        var ct = context.RequestAborted;

        var request = coordinator.TryOpenRequest(remote);
        if (request is null)
        {
            await FailAsync(io, "pairFailed", ct);
            return;
        }

        try
        {
            var secondsLeft = (int)Math.Ceiling((request.ExpiresAt - request.CreatedAt).TotalSeconds);
            await io.SendAsync(new { type = "pairCode", code = request.Code, expiresInSeconds = secondsLeft }, ct);

            // The glasses just wait here. Anything they send, or closing, ends the request.
            var gone = WaitUntilGoneAsync(io, ct);
            if (await Task.WhenAny(request.Outcome, gone) == gone)
            {
                coordinator.Cancel(request.Id);
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
            coordinator.Cancel(request.Id);
        }
    }

    private static async Task SessionAsync(HttpContext context, PairingCoordinator coordinator,
        IOptions<WebOptions> web, IOptions<ControlSessionOptions> session, AlertLog alerts, TimeProvider time,
        SessionServices services, PhoneServices phone)
    {
        if (!await WebHosting.AcceptGuardAsync(context, web.Value, alerts))
        {
            return;
        }

        var remote = WebHosting.RemoteAddress(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var io = new SocketIO(socket, ControlProtocol.MaxMessageBytes);

        // The first message must be "authenticate" (an approval's token) or "resume" (a remembered
        // device's token), within a few seconds. Until then the socket holds nothing: the session
        // slot is only taken by a valid token. "phone" instead asks for a relay to the phone's
        // companion, which pairs and checks the glasses itself.
        ControlMessage? auth;
        using (var authDeadline = new CancellationTokenSource(session.Value.AuthTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(authDeadline.Token, context.RequestAborted))
        {
            try
            {
                var first = await io.ReceiveAsync(linked.Token);
                if (first is null)
                {
                    return;
                }

                ControlProtocol.TryParse(first.Value.Span, out var message, out _);
                auth = message is AuthenticateMessage or ResumeMessage or ConnectPhoneMessage ? message : null;
            }
            catch (OperationCanceledException) when (authDeadline.IsCancellationRequested)
            {
                alerts.Raise(AlertKind.AuthenticationTimedOut, remote, "No authentication within the deadline");
                await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "authFailed");
                return;
            }
            catch (InvalidClientMessageException ex)
            {
                alerts.Raise(AlertKind.ProtocolViolation, remote, ex.Message);
                return;
            }
        }

        if (auth is null)
        {
            alerts.Raise(AlertKind.ProtocolViolation, remote, "First session message was not authenticate");
            await FailAsync(io, "authFailed", context.RequestAborted);
            return;
        }

        if (auth is ConnectPhoneMessage connect)
        {
            // Not a PC session: no lease, no approval on the PC. The phone decides who gets in.
            if (!phone.Registry.TryOpenRelay(remote))
            {
                await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "rate limit");
                return;
            }
            await new PhoneRelay(io, remote, connect.Phone, phone).RunAsync(context.RequestAborted);
            return;
        }

        using var lease = auth switch
        {
            AuthenticateMessage approved => coordinator.TryAuthenticate(approved.Token, remote),
            ResumeMessage resume => await coordinator.TryResumeAsync(resume.Token, remote, context.RequestAborted),
            _ => null,
        };
        if (lease is null)
        {
            await FailAsync(io, "authFailed", context.RequestAborted);
            return;
        }

        // A new device token goes down this socket only, once; the server keeps its hash.
        var deviceToken = lease.TakeDeviceToken();
        await io.SendAsync(deviceToken is null
            ? (object)new { type = "authenticated" }
            : new
            {
                type = "authenticated",
                deviceToken,
                deviceTokenExpiresAt = lease.DeviceTokenExpiresAt!.Value.ToUnixTimeMilliseconds(),
            }, context.RequestAborted);
        await new ControlSession(io, lease, services).RunAsync(context.RequestAborted);
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
