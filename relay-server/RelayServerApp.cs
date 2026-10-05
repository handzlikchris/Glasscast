using GlassesRemote.Server.Alerts;
using GlassesRemote.Server.Hosting;
using GlassesRemote.Server.Phone;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace GlassesRemote.RelayServer;

/// <summary><c>Relay:*</c> settings.</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Where the registered phones are kept; empty = LocalAppData/GlassesRemote/relay.</summary>
    public string DataDirectory { get; set; } = "";

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "relay");
}

/// <summary>Builds the relay's web host. Program and the tests share this.</summary>
public static class RelayServerApp
{
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var config = builder.Configuration;

        // relay.json, then machine-specific values (Web:PublicHost) in the git-ignored
        // relay.Local.json: above appsettings.json, below environment variables and arguments.
        var beforeEnvironment = config.Sources.ToList().FindIndex(source =>
            source is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        beforeEnvironment = beforeEnvironment < 0 ? config.Sources.Count : beforeEnvironment;
        config.Sources.Insert(beforeEnvironment, JsonFile("relay.Local.json", builder.Environment));
        config.Sources.Insert(beforeEnvironment, JsonFile("relay.json", builder.Environment));
        var services = builder.Services;

        builder.AddGlassesWeb();
        services.Configure<RelayOptions>(config.GetSection(RelayOptions.SectionName));
        services.Configure<CompanionOptions>(config.GetSection(CompanionOptions.SectionName));
        services.AddOptions<CompanionOptions>().PostConfigure<IOptions<RelayOptions>>((companion, relay) =>
        {
            // There's no one to approve here, ever.
            companion.Registration = CompanionRegistration.Open;

            // Never the PC server's files, even on the same machine.
            var dir = relay.Value.DataDirectory is { Length: > 0 } configured ? configured : RelayOptions.DefaultDataDirectory;
            if (string.IsNullOrEmpty(companion.PhonesFile))
            {
                companion.PhonesFile = Path.Combine(dir, "phones.json");
            }
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AlertLog>();
        services.AddSingleton<CompanionRegistry>();
        services.AddSingleton<PhoneServices>();

        configure?.Invoke(builder);

        var app = builder.Build();
        var registration = app.Services.GetRequiredService<IOptions<CompanionOptions>>().Value.Registration;
        app.Logger.LogInformation("Phone relay: registration {Registration}", registration);

        app.UseGlassesWeb();
        app.MapRelayEndpoints();
        app.MapCompanionEndpoint();
        app.MapFeatures(pc: false, phone: true);
        return app;
    }

    private static JsonConfigurationSource JsonFile(string path, IWebHostEnvironment environment) =>
        new() { Path = path, Optional = true, ReloadOnChange = false, FileProvider = environment.ContentRootFileProvider };
}
