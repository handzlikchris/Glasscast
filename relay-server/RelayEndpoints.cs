using System.Net.WebSockets;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Protocol;
using GlassesRemote.Server.Sessions;
using Microsoft.Extensions.Options;

namespace GlassesRemote.RelayServer;

/// <summary>
/// The relay's glasses endpoints: health check and the session socket, which here only ever
/// relays to a phone (first message <c>{type:"phone", phone?}</c>). There is no PC session, no
/// pairing with this server and nothing else: anything but a phone relay is refused.
/// </summary>
public static class RelayEndpoints
{
    public static void MapRelayEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Text("ok"));
        app.Map("/ws/session", SessionAsync);
    }

    private static async Task SessionAsync(HttpContext context, IOptions<WebOptions> web,
        IOptions<CompanionOptions> companion, AlertLog alerts, TimeProvider time, PhoneServices phone)
    {
        if (!await WebHosting.AcceptGuardAsync(context, web.Value, alerts))
        {
            return;
        }

        var remote = WebHosting.RemoteAddress(context);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var io = new SocketIO(socket, JsonRules.MaxMessageBytes);

        ConnectSignal? connect;
        using (var deadline = new CancellationTokenSource(companion.Value.AuthTimeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, context.RequestAborted))
        {
            try
            {
                var first = await io.ReceiveAsync(linked.Token);
                if (first is null)
                {
                    return;
                }
                connect = RelayProtocol.TryParseConnect(first.Value.Span);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                alerts.Raise(AlertKind.AuthenticationTimedOut, remote, "No first message within the deadline");
                await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "authFailed");
                return;
            }
            catch (InvalidClientMessageException ex)
            {
                alerts.Raise(AlertKind.ProtocolViolation, remote, ex.Message);
                return;
            }
        }

        if (connect is null)
        {
            alerts.Raise(AlertKind.ProtocolViolation, remote, "First session message was not phone (this server only relays)");
            try
            {
                await io.SendAsync(new { type = "authFailed" }, context.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
            await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "authFailed");
            return;
        }

        if (!phone.Registry.TryOpenRelay(remote))
        {
            await io.CloseQuietlyAsync(WebSocketCloseStatus.PolicyViolation, "rate limit");
            return;
        }

        await new PhoneRelay(io, remote, connect.Phone, phone).RunAsync(context.RequestAborted);
    }
}
