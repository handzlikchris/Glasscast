using Microsoft.Extensions.Options;
using WebRtcSpike;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SpikeOptions>(builder.Configuration.GetSection(SpikeOptions.SectionName));

var app = builder.Build();

SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

app.MapGet("/health", () => "ok");

// The media port is a single fixed UDP port, so only one spike session can run at a time.
var sessionGate = new SemaphoreSlim(1, 1);

app.Map("/ws/spike", async (HttpContext context, IOptions<SpikeOptions> options, ILoggerFactory loggerFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!await sessionGate.WaitAsync(0))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }

    try
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var logger = loggerFactory.CreateLogger<SpikeSession>();
        logger.LogInformation("Spike session from {Remote}", context.Connection.RemoteIpAddress);
        await new SpikeSession(socket, options.Value, logger).RunAsync(context.RequestAborted);
    }
    finally
    {
        sessionGate.Release();
    }
});

app.Run();
