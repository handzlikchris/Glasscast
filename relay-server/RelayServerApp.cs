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

    /// <summary>
    /// Where the registered phones are kept. Relative = next to the program (its content root),
    /// never the working directory, which under IIS is system32. Empty = "data" next to the program,
    /// so on a server it sits with the site (deploys leave it alone; under IIS the pool needs Modify).
    /// </summary>
    public string DataDirectory { get; set; } = "";

    public const string DefaultDataDirectory = "data";

    /// <summary>
    /// Where versions before 2026-10-07 kept the phones by default: the account's LocalAppData,
    /// which under IIS is the app pool's hidden profile. Null when the account has none.
    /// </summary>
    public static string? OldDefaultPhonesFile
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "GlassesRemote", "relay", "phones.json");
        }
    }

    /// <summary>The folder for the phones file: <see cref="DataDirectory"/> resolved against <paramref name="contentRoot"/>.</summary>
    public string ResolveDataDirectory(string contentRoot) =>
        Path.GetFullPath(DataDirectory is { Length: > 0 } configured ? configured : DefaultDataDirectory, contentRoot);
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
        var contentRoot = builder.Environment.ContentRootPath;
        services.Configure<RelayOptions>(config.GetSection(RelayOptions.SectionName));
        services.Configure<CompanionOptions>(config.GetSection(CompanionOptions.SectionName));
        services.AddOptions<CompanionOptions>().PostConfigure<IOptions<RelayOptions>>((companion, relay) =>
        {
            // There's no one to approve here, ever.
            companion.Registration = CompanionRegistration.Open;

            // Never the PC server's files, even on the same machine.
            companion.PhonesFile = string.IsNullOrEmpty(companion.PhonesFile)
                ? Path.Combine(relay.Value.ResolveDataDirectory(contentRoot), "phones.json")
                : Path.GetFullPath(companion.PhonesFile, contentRoot);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AlertLog>();
        services.AddSingleton<CompanionRegistry>();
        services.AddSingleton<PhoneServices>();

        configure?.Invoke(builder);

        var app = builder.Build();
        var phonesFile = app.Services.GetRequiredService<IOptions<CompanionOptions>>().Value.PhonesFile!;
        app.Logger.LogInformation("Phone relay: phones kept in {PhonesFile}", phonesFile);
        WarnIfNotWritable(app.Logger, Path.GetDirectoryName(phonesFile)!);
        if (string.IsNullOrEmpty(config["Relay:DataDirectory"]) && string.IsNullOrEmpty(config["Companion:PhonesFile"]))
        {
            CopyOldPhones(app.Logger, RelayOptions.OldDefaultPhonesFile, phonesFile);
        }

        app.UseGlassesWeb();
        app.MapRelayEndpoints();
        app.MapCompanionEndpoint();
        app.MapFeatures(pc: false, phone: true);
        return app;
    }

    /// <summary>
    /// Phones that register while the folder can't be written are forgotten at the next restart
    /// (under IIS: the app pool's account needs Modify on it), so say so at startup.
    /// </summary>
    private static void WarnIfNotWritable(ILogger logger, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-check");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Can't write to {Directory}: registered phones won't survive a restart. " +
                "Give this account write access there (under IIS: Modify for IIS AppPool\\<pool name>), " +
                "or set Relay:DataDirectory", directory);
        }
    }

    /// <summary>
    /// Brings the phones over from the old default folder once, so they don't have to register
    /// again: only when there's no phones file here yet. The old file stays where it was.
    /// </summary>
    public static void CopyOldPhones(ILogger logger, string? oldFile, string phonesFile)
    {
        if (oldFile is null || File.Exists(phonesFile) || !File.Exists(oldFile)
            || Path.GetFullPath(oldFile).Equals(Path.GetFullPath(phonesFile), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Copy(oldFile, phonesFile);
            logger.LogWarning("Copied the registered phones from {OldFile} to {PhonesFile}", oldFile, phonesFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Couldn't copy the registered phones from {OldFile} to {PhonesFile}; they have to register again", oldFile, phonesFile);
        }
    }

    private static JsonConfigurationSource JsonFile(string path, IWebHostEnvironment environment) =>
        new() { Path = path, Optional = true, ReloadOnChange = false, FileProvider = environment.ContentRootFileProvider };
}
