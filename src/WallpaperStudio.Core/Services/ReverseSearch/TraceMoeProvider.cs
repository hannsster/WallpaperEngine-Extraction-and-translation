using System.Text.Json;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// trace.moe identifies the anime, episode and timestamp of an anime frame.
/// It is keyless, so it is the one provider that works out of the box, but it
/// only identifies the scene: it cannot return a higher-resolution artwork.
/// </summary>
public sealed class TraceMoeProvider : IReverseImageProvider
{
    private const string Endpoint = "https://api.trace.moe/search?anilistInfo&cutBorders";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);

    private readonly HttpClient _client;

    public TraceMoeProvider(HttpClient client)
    {
        _client = client;
    }

    public string Id => "tracemoe";

    public string Label => "trace.moe";

    public bool IsEnabled(AppSettings settings) => settings.ReverseSearchEnabled && settings.ReverseSearchTraceMoe;

    public bool IsConfigured(AppSettings settings) => true;

    public string ConfigurationHint => "Enable trace.moe in Settings to identify anime scenes.";

    public async Task<ProviderResponse> SearchAsync(
        ReverseImageQuery query,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var body = await ReverseSearchHttp.PostMultipartAsync(
                _client,
                Endpoint,
                [],
                "image",
                query.Jpeg,
                query.FileName,
                RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        return Parse(body);
    }

    private static ProviderResponse Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(error.GetString()))
        {
            throw new ReverseSearchProviderException($"trace.moe: {error.GetString()}");
        }

        var results = new List<ReverseSearchResult>();
        if (!root.TryGetProperty("result", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new ProviderResponse(results);
        }

        foreach (var item in items.EnumerateArray())
        {
            var similarity = ReverseSearchJson.ReadDouble(item, "similarity");
            if (similarity <= 0)
            {
                continue;
            }

            var from = ReverseSearchJson.ReadDouble(item, "from");
            var episode = ReverseSearchJson.ReadString(item, "episode");
            var fileName = ReverseSearchJson.ReadString(item, "filename");
            var image = ReverseSearchJson.ReadString(item, "image");

            string? title = null;
            string? artist = null;
            string? pageUrl = null;
            var animeId = 0;

            if (item.TryGetProperty("anilist", out var anilist) && anilist.ValueKind == JsonValueKind.Object)
            {
                animeId = ReverseSearchJson.ReadInt(anilist, "id");

                if (anilist.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Object)
                {
                    title = ReverseSearchJson.FirstString(titles, "english", "romaji", "native");
                }

                var synonyms = ReverseSearchJson.FirstString(anilist, "synonyms");
                artist = synonyms;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                title = title!.Trim();
            }

            if (animeId > 0)
            {
                pageUrl = $"https://anilist.co/anime/{animeId}";
            }

            var stamp = TimeSpan.FromSeconds(Math.Max(0, from));
            var identification = string.IsNullOrWhiteSpace(episode)
                ? $"scene at {stamp:m\\:ss}"
                : $"episode {episode} at {stamp:m\\:ss}";

            results.Add(new ReverseSearchResult
            {
                ProviderId = "tracemoe",
                ProviderLabel = "trace.moe",
                SourceName = "Anime",
                Title = title ?? fileName,
                Artist = artist,
                PageUrl = pageUrl,
                ThumbnailUrl = ReverseSearchHttp.IsHttpUrl(image) ? image : null,
                Similarity = Math.Round(Math.Min(1, similarity), 4),
                Identification = identification,
                IsIdentificationOnly = true
            });
        }

        Logger.Info($"trace.moe returned {results.Count} result(s).");
        return new ProviderResponse(results);
    }
}
