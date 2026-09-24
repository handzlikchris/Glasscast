using System.Text.Json;

namespace GlassesRemote.Server.Desktop;

/// <summary>Remembers the last committed region between runs (a small JSON file).</summary>
public sealed class RegionStore
{
    private readonly string _path;
    private readonly ILogger<RegionStore> _logger;

    public RegionStore(string path, ILogger<RegionStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassesRemote", "region.json");

    public CaptureRegion? Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<CaptureRegion>(File.ReadAllText(_path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read saved region from {Path}", _path);
            return null;
        }
    }

    public void Save(CaptureRegion region)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(region));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save region to {Path}", _path);
        }
    }
}
