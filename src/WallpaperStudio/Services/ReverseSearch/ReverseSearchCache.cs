using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// Per-query JSON cache plus provider-thumbnail storage. Results are kept so a
/// rescan or restart does not spend API quota again, and so the "original found"
/// badge survives offline.
/// </summary>
public sealed class ReverseSearchCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions UsageOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, ReverseSearchSnapshot> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ReverseSearchSnapshot? Load(string key)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<ReverseSearchSnapshot>(json, Options);
            if (snapshot is null)
            {
                return null;
            }

            snapshot.FromCache = true;
            _memory[key] = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read reverse-search cache '{key}'.", ex);
            return null;
        }
    }

    public void Save(ReverseSearchSnapshot snapshot)
    {
        _memory[snapshot.Key] = snapshot;
        try
        {
            var json = JsonSerializer.Serialize(snapshot, Options);
            AtomicFile.WriteAllText(PathFor(snapshot.Key), json);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not write the reverse-search cache.", ex);
        }
    }

    public string QueryImagePath(string key)
        => Path.Combine(AppPaths.ReverseSearchQueryDirectory, key + ".jpg");

    public string ThumbnailPath(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.ReverseSearchThumbnailDirectory, hash[..24] + ".jpg");
    }

    public ReverseSearchUsage LoadUsage()
    {
        try
        {
            if (!File.Exists(AppPaths.ReverseSearchUsageFile))
            {
                return new ReverseSearchUsage();
            }

            var json = File.ReadAllText(AppPaths.ReverseSearchUsageFile);
            return JsonSerializer.Deserialize<ReverseSearchUsage>(json, UsageOptions) ?? new ReverseSearchUsage();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read reverse-search usage: {ex.Message}");
            return new ReverseSearchUsage();
        }
    }

    public void SaveUsage(ReverseSearchUsage usage)
    {
        try
        {
            AtomicFile.WriteAllText(AppPaths.ReverseSearchUsageFile, JsonSerializer.Serialize(usage, UsageOptions));
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not save reverse-search usage: {ex.Message}");
        }
    }

    private static string PathFor(string key)
        => Path.Combine(AppPaths.ReverseSearchCacheDirectory, key + ".json");
}
