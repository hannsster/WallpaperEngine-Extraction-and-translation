namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>The downscaled JPEG that is uploaded to a provider, plus context.</summary>
public sealed record ReverseImageQuery(byte[] Jpeg, int Width, int Height, string Title, string SourceKind)
{
    public string FileName => "frame.jpg";
}

/// <summary>Results plus an optional human-readable note (rate limit, truncation...).</summary>
public sealed record ProviderResponse(IReadOnlyList<ReverseSearchResult> Results, string? Note = null);

public sealed class ReverseSearchProviderException : Exception
{
    public ReverseSearchProviderException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// One reverse-image service. Implementations talk either to a documented public
/// API or to a provider's own public, keyless search form. None of them bypass a
/// login, paywall or rate limit.
/// </summary>
public interface IReverseImageProvider
{
    /// <summary>Stable id used in cache keys and logs.</summary>
    string Id { get; }

    string Label { get; }

    /// <summary>False when the user turned this provider off.</summary>
    bool IsEnabled(AppSettings settings);

    /// <summary>False when a required key is missing.</summary>
    bool IsConfigured(AppSettings settings);

    /// <summary>Shown in the UI when the provider is enabled but not usable.</summary>
    string ConfigurationHint { get; }

    Task<ProviderResponse> SearchAsync(ReverseImageQuery query, AppSettings settings, CancellationToken cancellationToken);
}

internal static class ReverseSearchJson
{
    public static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }

    public static double ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    public static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    public static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
