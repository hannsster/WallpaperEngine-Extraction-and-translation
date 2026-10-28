using System.Text.Json;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// Google Cloud Vision "web detection". It is a documented, paid-but-free-tier
/// API that finds pages containing the image and visually similar images, which
/// covers non-anime wallpapers that SauceNAO cannot see.
/// </summary>
public sealed class GoogleVisionProvider : IReverseImageProvider
{
    private const string Endpoint = "https://vision.googleapis.com/v1/images:annotate";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;

    public GoogleVisionProvider(HttpClient client)
    {
        _client = client;
    }

    public string Id => "googlevision";

    public string Label => "Google Vision";

    public bool IsEnabled(AppSettings settings) => settings.ReverseSearchEnabled;

    public bool IsConfigured(AppSettings settings) => !string.IsNullOrWhiteSpace(settings.GoogleVisionApiKey);

    public string ConfigurationHint =>
        "Add a Google Cloud Vision API key in Settings. The Web Detection free tier covers about 1,000 searches per month.";

    public async Task<ProviderResponse> SearchAsync(
        ReverseImageQuery query,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(settings))
        {
            throw new ReverseSearchProviderException("No Google Vision API key is configured.");
        }

        var payload = BuildRequest(query, settings.GoogleVisionApiKey);
        var url = Endpoint + "?key=" + Uri.EscapeDataString(settings.GoogleVisionApiKey);
        var body = await ReverseSearchHttp.PostJsonAsync(_client, url, payload, RequestTimeout, cancellationToken)
            .ConfigureAwait(false);

        return Parse(body);
    }

    private static string BuildRequest(ReverseImageQuery query, string apiKey)
    {
        _ = apiKey;
        var base64 = Convert.ToBase64String(query.Jpeg);
        return $$"""
        {
          "requests": [
            {
              "image": { "content": "{{base64}}" },
              "features": [ { "type": "WEB_DETECTION", "maxResults": 20 } ]
            }
          ]
        }
        """;
    }

    private static ProviderResponse Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("responses", out var responses) ||
            responses.ValueKind != JsonValueKind.Array ||
            responses.GetArrayLength() == 0)
        {
            throw new ReverseSearchProviderException("Google Vision returned an unexpected response.");
        }

        var first = responses[0];
        if (first.TryGetProperty("error", out var error))
        {
            var message = ReverseSearchJson.ReadString(error, "message") ?? "unknown error";
            throw new ReverseSearchProviderException($"Google Vision: {message}");
        }

        if (!first.TryGetProperty("webDetection", out var web) || web.ValueKind != JsonValueKind.Object)
        {
            return new ProviderResponse([]);
        }

        var results = new List<ReverseSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url, double similarity, string sourceName, string? title, bool isImage)
        {
            if (!ReverseSearchHttp.IsHttpUrl(url) || !seen.Add(url!))
            {
                return;
            }

            results.Add(new ReverseSearchResult
            {
                ProviderId = "googlevision",
                ProviderLabel = "Google Vision",
                SourceName = sourceName,
                Title = title,
                PageUrl = url,
                ImageUrl = isImage ? url : null,
                Similarity = similarity
            });
        }

        if (web.TryGetProperty("fullMatchingImages", out var full))
        {
            foreach (var image in Enumerate(full))
            {
                var url = ReverseSearchJson.ReadString(image, "url");
                Add(url, 0.95, SiteName(url), null, LooksLikeImage(url));
            }
        }

        if (web.TryGetProperty("pagesWithMatchingImages", out var pages))
        {
            foreach (var page in Enumerate(pages))
            {
                var url = ReverseSearchJson.ReadString(page, "url");
                var title = ReverseSearchJson.ReadString(page, "pageTitle");
                Add(url, 0.88, SiteName(url), title, false);
            }
        }

        if (web.TryGetProperty("partialMatchingImages", out var partial))
        {
            foreach (var image in Enumerate(partial))
            {
                var url = ReverseSearchJson.ReadString(image, "url");
                Add(url, 0.82, SiteName(url), null, LooksLikeImage(url));
            }
        }

        if (web.TryGetProperty("visuallySimilarImages", out var similar))
        {
            foreach (var image in Enumerate(similar))
            {
                var url = ReverseSearchJson.ReadString(image, "url");
                Add(url, 0.62, SiteName(url), null, LooksLikeImage(url));
            }
        }

        string? note = null;
        if (web.TryGetProperty("bestGuessLabels", out var labels))
        {
            var label = Enumerate(labels)
                .Select(item => ReverseSearchJson.ReadString(item, "label"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(label))
            {
                note = $"Google's best guess: {label}.";
            }
        }

        Logger.Info($"Google Vision returned {results.Count} result(s).");
        return new ProviderResponse(results, note);
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement array)
        => array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static bool LooksLikeImage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string SiteName(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Web";
        }

        var host = uri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return host;
    }
}
