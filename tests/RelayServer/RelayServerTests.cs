using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using GlassesRemote.RelayServer;
using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Phone;
using GlassesRemote.Server.Tests.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GlassesRemote.Server.Tests.RelayServer;

/// <summary>The headless relay server's pipeline on an in-memory TestServer.</summary>
public sealed class RelayServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public RelayServerHost(Dictionary<string, string?>? settings = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Web:AllowedOrigins:0"] = TestServerHost.Origin,
            ["Web:ClientRoot"] = "does-not-exist",
            ["Relay:DataDirectory"] = DataDirectory,
            ["Companion:AuthTimeout"] = "00:00:00.500",
        };
        foreach (var (key, value) in settings ?? new())
        {
            config[key] = value;
        }

        _app = RelayServerApp.Create([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(config);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new CapturingLoggerProvider(Logs));
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}");

    public ConcurrentQueue<string> Logs { get; } = new();

    public AlertLog Alerts => _app.Services.GetRequiredService<AlertLog>();

    public CompanionRegistry Companion => _app.Services.GetRequiredService<CompanionRegistry>();

    public HttpClient CreateHttpClient() => _app.GetTestServer().CreateClient();

    public async Task<TestSocket> ConnectAsync(string path, string? origin = TestServerHost.Origin)
    {
        var client = _app.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (origin is not null)
            {
                request.Headers["Origin"] = origin;
            }
        };
        return new TestSocket(await client.ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}

public sealed class RelayServerTests : IAsyncLifetime
{
    private RelayServerHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new RelayServerHost();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task It_says_it_offers_the_phone_only()
    {
        using var http = _host.CreateHttpClient();
        Assert.Equal("ok", await http.GetStringAsync("/health"));
        Assert.Equal("""{"pc":false,"phone":true}""", await http.GetStringAsync("/features"));
        Assert.Contains("frame-ancestors 'none'", (await http.GetAsync("/health")).Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("""{"type":"authenticate","token":"t"}""")]
    [InlineData("""{"type":"resume","token":"t"}""")]
    [InlineData("""{"type":"phone","token":"t"}""")]
    [InlineData("""{"type":"setMode","mode":"pointer"}""")]
    public async Task The_session_socket_only_relays_to_phones(string first)
    {
        using var socket = await _host.ConnectAsync("/ws/session");
        await socket.SendRawAsync(first);

        Assert.Equal("authFailed", (await socket.ReceiveAsync()).GetProperty("type").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await socket.WaitForCloseAsync());
        Assert.Contains(_host.Alerts.Recent(), a => a.Kind == AlertKind.ProtocolViolation);
    }

    [Fact]
    public async Task There_is_no_pc_pairing_here()
    {
        using var http = _host.CreateHttpClient();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/ws/pair")).StatusCode);
    }

    [Fact]
    public async Task Sockets_from_other_sites_are_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("/ws/session", "https://evil.example"));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("/ws/companion", TestServerHost.Origin));
    }

    [Fact]
    public async Task A_phone_registers_itself_and_glasses_find_it_by_connect_code_then_by_id()
    {
        using (var pairing = await _host.ConnectAsync("/ws/companion", origin: null))
        {
            await pairing.SendAsync(new { type = "pair", name = "Someone's phone" });
            var token = (await pairing.ReceiveAsync("paired")).GetProperty("token").GetString()!;
            Assert.True(File.Exists(Path.Combine(_host.DataDirectory, "phones.json")));
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(_host.DataDirectory, "phones.json")));

            using var companion = await _host.ConnectAsync("/ws/companion", origin: null);
            await companion.SendAsync(new { type = "auth", token });
            await companion.ReceiveAsync("authenticated");

            string phone;
            using (var glasses = await _host.ConnectAsync("/ws/session"))
            {
                await glasses.SendAsync(new { type = "phone" });
                var code = (await glasses.ReceiveAsync("connectCode")).GetProperty("code").GetString()!;
                await companion.SendAsync(new { type = "claim", code });
                await companion.ReceiveAsync("claimed");
                await companion.ReceiveAsync("relayOpen");
                phone = (await glasses.ReceiveAsync("phoneFound")).GetProperty("phone").GetString()!;
                Assert.Equal("ready", (await glasses.ReceiveAsync("phoneStatus")).GetProperty("state").GetString());
            }

            using var again = await _host.ConnectAsync("/ws/session");
            await again.SendAsync(new { type = "phone", phone });
            Assert.Equal("ready", (await again.ReceiveAsync()).GetProperty("state").GetString());
            Assert.DoesNotContain(_host.Logs, line => line.Contains(token, StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// The relay server is meant for a machine that must never be controllable from the glasses: none
/// of the PC server's capture, input, window or media code may be in it, even unused.
/// </summary>
public sealed class RelayServerIsolationTests
{
    private static readonly string[] Forbidden =
    [
        "GlassesRemote.Server", "SIPSorcery", "SIPSorceryMedia", "NAudio", "Vortice", "Concentus",
        "System.Windows.Forms", "Microsoft.WindowsDesktop",
    ];

    [Fact]
    public void The_relay_server_loads_none_of_the_pc_servers_code()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([typeof(RelayServerApp).Assembly]);
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                Assert.DoesNotContain(Forbidden, f => name == f || name.StartsWith(f + ".", StringComparison.Ordinal));
                if (seen.Add(name) && name.StartsWith("GlassesRemote", StringComparison.Ordinal))
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
            }
        }

        Assert.Contains("GlassesRemote.Relay", seen);
    }

    [Fact]
    public void The_relay_library_and_server_target_every_platform()
    {
        foreach (var assembly in new[] { typeof(RelayServerApp).Assembly, typeof(CompanionRegistry).Assembly })
        {
            assembly.ManifestModule.GetPEKind(out var kind, out var machine);
            Assert.True(kind.HasFlag(PortableExecutableKinds.ILOnly), assembly.GetName().Name);
            Assert.False(kind.HasFlag(PortableExecutableKinds.PE32Plus) && machine == ImageFileMachine.AMD64,
                $"{assembly.GetName().Name} is x64-only");
            var framework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName;
            Assert.DoesNotContain("windows", framework, StringComparison.OrdinalIgnoreCase);
        }
    }
}
