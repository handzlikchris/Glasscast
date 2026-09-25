using System.Text.Json;

namespace GlassesRemote.Server.Pairing;

/// <summary>
/// "These glasses were approved": lets the device that was approved start new sessions with a
/// device token instead of a fresh approval, until <see cref="ExpiresAt"/> (a fixed time after
/// the approval, never extended). The token changes on every use; only hashes are kept. A token
/// that was already swapped for a newer one is remembered so its reuse can be caught.
/// </summary>
public sealed record DeviceGrant(string Id, byte[] CurrentHash, byte[]? PreviousHash, DateTimeOffset ExpiresAt);

/// <summary>
/// Keeps the one device grant across server restarts, as hashes in a small JSON file (a hash
/// can't be turned back into a token). No file configured means memory only.
/// </summary>
public sealed class DeviceGrantStore(string? path, ILogger logger)
{
    private sealed record Stored(string Id, string CurrentHash, string? PreviousHash, DateTimeOffset ExpiresAt);

    public DeviceGrant? Load()
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is null
                ? null
                : new DeviceGrant(stored.Id, Convert.FromBase64String(stored.CurrentHash),
                    stored.PreviousHash is null ? null : Convert.FromBase64String(stored.PreviousHash), stored.ExpiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            logger.LogWarning(ex, "Could not read the remembered device from {Path}; pairing is needed", path);
            return null;
        }
    }

    public void Save(DeviceGrant? grant)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            if (grant is null)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new Stored(grant.Id, Convert.ToBase64String(grant.CurrentHash),
                grant.PreviousHash is null ? null : Convert.ToBase64String(grant.PreviousHash), grant.ExpiresAt);
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the remembered device to {Path}", path);
        }
    }
}
