using System.Net;
using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.Server.Hosting;

/// <summary>
/// The only public endpoints: health check, pairing socket and session socket.
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
        if (!await AcceptGuardAsync(context, web.Value, alerts))
        {
            return;
        }

        var remote = RemoteAddress(context);
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
        SessionServices services)
    {
        if (!await AcceptGuardAsync(context, web.Value, alerts))
        {
            return;
        }

        var remote = RemoteAddress(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var io = new SocketIO(socket, ControlProtocol.MaxMessageBytes);

        // The first message must be "authenticate", within a few seconds. Until then the
        // socket holds nothing: the session slot is only taken by a valid token.
        AuthenticateMessage? auth;
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
                auth = message as AuthenticateMessage;
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

        using var lease = coordinator.TryAuthenticate(auth.Token, remote);
        if (lease is null)
        {
            await FailAsync(io, "authFailed", context.RequestAborted);
            return;
        }

        await io.SendAsync(new { type = "authenticated" }, context.RequestAborted);
        await new ControlSession(io, lease, services).RunAsync(context.RequestAborted);
    }

    /// <summary>WebSocket upgrade and exact Origin match; a bad Origin is refused before accepting.</summary>
    private static async Task<bool> AcceptGuardAsync(HttpContext context, WebOptions web, AlertLog alerts)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!OriginPolicy.IsAllowed(origin, web.AllowedOrigins))
        {
            alerts.Raise(AlertKind.BadOrigin, RemoteAddress(context),
                $"WebSocket refused for origin '{Truncate(origin, 100)}'");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.CompleteAsync();
            return false;
        }

        return true;
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

    private static IPAddress RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress ?? IPAddress.None;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

public static class OriginPolicy
{
    /// <summary>Exact scheme://host[:port] match against the allowlist. A missing Origin is refused.</summary>
    public static bool IsAllowed(string? origin, IEnumerable<string> allowed)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var entry in allowed)
        {
            if (Uri.TryCreate(entry, UriKind.Absolute, out var allowedUri)
                && string.Equals(uri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
                && uri.Port == allowedUri.Port
                && uri.AbsolutePath == "/")
            {
                return true;
            }
        }

        return false;
    }
}
