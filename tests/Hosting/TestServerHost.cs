using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Desktop;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Media;
using GlassesRemote.Server.Pairing;
using GlassesRemote.Server.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GlassesRemote.Server.Tests.Hosting;

/// <summary>The real server pipeline on an in-memory TestServer, with fake desktop and media.</summary>
public sealed class TestServerHost : IAsyncDisposable
{
    public const string Origin = "https://glasses.test";

    private readonly WebApplication _app;
    private readonly string _regionFile = Path.Combine(Path.GetTempPath(), $"region-{Guid.NewGuid():N}.json");

    public TestServerHost(Dictionary<string, string?>? settings = null, string[]? args = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Web:AllowedOrigins:0"] = Origin,
            ["Web:ClientRoot"] = "does-not-exist",
            ["Session:AuthTimeout"] = "00:00:00.500",
            ["Desktop:RegionFile"] = _regionFile,
            ["Media:FramesPerSecond"] = "30",
        };
        foreach (var (key, value) in settings ?? new())
        {
            config[key] = value;
        }

        _app = ServerApp.Create(args ?? [], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(config);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new CapturingLoggerProvider(Logs));
            builder.Logging.SetMinimumLevel(LogLevel.Trace);

            builder.Services.AddSingleton<IScreen>(new FakeScreen());
            builder.Services.AddSingleton<ICaptureSource>(Capture);
            builder.Services.AddSingleton<IInputInjector>(Input);
            builder.Services.AddSingleton<IWindowSwitcher>(Windows);
            builder.Services.AddSingleton<IKeepAwake>(KeepAwake);
            builder.Services.AddSingleton<IMediaPeerFactory>(Peers);
            builder.Services.AddSingleton<IFrameEncoderFactory>(new FakeEncoderFactory());
        });

        _app.StartAsync().GetAwaiter().GetResult();
    }

    public ConcurrentQueue<string> Logs { get; } = new();

    public FakeInput Input { get; } = new();

    public FakeWindowSwitcher Windows { get; } = new();

    /// <summary>The hello message of the last session started with <see cref="StartSessionAsync"/>.</summary>
    public JsonElement LastHello { get; private set; }

    public FakeCapture Capture { get; } = new();

    public FakeKeepAwake KeepAwake { get; } = new();

    public FakePeerFactory Peers { get; } = new();

    public PairingCoordinator Coordinator => _app.Services.GetRequiredService<PairingCoordinator>();

    public AlertLog Alerts => _app.Services.GetRequiredService<AlertLog>();

    public CastArea CastArea => _app.Services.GetRequiredService<CastArea>();

    public HttpClient CreateHttpClient() => _app.GetTestServer().CreateClient();

    public async Task<TestSocket> ConnectAsync(string path, string? origin = Origin)
    {
        var client = _app.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (origin is not null)
            {
                request.Headers["Origin"] = origin;
            }
        };
        var socket = await client.ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);
        return new TestSocket(socket);
    }

    /// <summary>Runs the whole pairing flow and returns the token the glasses would receive.</summary>
    public async Task<string> PairAsync()
    {
        using var pair = await ConnectAsync("/ws/pair");
        var code = await pair.ReceiveAsync();
        Assert.Equal("pairCode", code.GetProperty("type").GetString());

        var request = Coordinator.PendingRequest!;
        Assert.Equal(code.GetProperty("code").GetString(), request.Code);
        Assert.True(Coordinator.Approve(request.Id));

        var paired = await pair.ReceiveAsync();
        Assert.Equal("paired", paired.GetProperty("type").GetString());
        return paired.GetProperty("token").GetString()!;
    }

    /// <summary>Authenticates a session socket and consumes the hello and offer.</summary>
    public async Task<TestSocket> StartSessionAsync(string token)
    {
        var session = await ConnectAsync("/ws/session");
        await session.SendAsync(new { type = "authenticate", token });
        Assert.Equal("authenticated", (await session.ReceiveAsync()).GetProperty("type").GetString());
        LastHello = await session.ReceiveAsync();
        Assert.Equal("hello", LastHello.GetProperty("type").GetString());
        Assert.Equal("rtcOffer", (await session.ReceiveAsync()).GetProperty("type").GetString());
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        File.Delete(_regionFile);
    }
}

public sealed class TestSocket(WebSocket socket) : IDisposable
{
    public WebSocket Socket => socket;

    public async Task SendAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendRawAsync(string text)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Next JSON message, skipping pongs.</summary>
    public async Task<JsonElement> ReceiveAsync(int timeoutMs = 5000)
    {
        while (true)
        {
            var text = await ReceiveTextAsync(timeoutMs) ?? throw new InvalidOperationException("socket closed");
            var element = JsonDocument.Parse(text).RootElement;
            if (element.GetProperty("type").GetString() != "pong")
            {
                return element;
            }
        }
    }

    public async Task<string?> ReceiveTextAsync(int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            length += result.Count;
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
        }
    }

    /// <summary>Drains messages until the server closes; returns the close status.</summary>
    public async Task<WebSocketCloseStatus?> WaitForCloseAsync(int timeoutMs = 5000)
    {
        while (await ReceiveTextAsync(timeoutMs) is not null)
        {
        }
        return socket.CloseStatus;
    }

    public void Dispose() => socket.Dispose();
}

internal sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
