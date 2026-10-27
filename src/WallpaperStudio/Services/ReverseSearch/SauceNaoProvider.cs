using System.Text.Json;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// SauceNAO's documented JSON API. Best at finding the Pixiv (or booru) source
/// of anime-style artwork, which is what most Wallpaper Engine wallpapers are
/// built from. Free keys are limited to roughly 150 searches per month, so calls
/// are serialised and spaced out.
/// </summary>
public sealed class SauceNaoProvider : IReverseImageProvider, IDisposable
{
    private const string Endpoint = "https://saucenao.com/search.php";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMilliseconds(1500);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public SauceNaoProvider(HttpClient client)
    {
        _client = client;
    }

    public string Id => "saucenao";

    public string Label => "SauceNAO";

    public bool IsEnabled(AppSettings settings) => settings.ReverseSearchEnabled;

    public bool IsConfigured(AppSettings settings) => !string.IsNullOrWhiteSpace(settings.SauceNaoApiKey);

    public string ConfigurationHint =>
        "Add a free SauceNAO API key in Settings. Without it, Pixiv and booru originals cannot be found.";

    public async Task<ProviderResponse> SearchAsync(
        ReverseImageQuery query,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(settings))
        {
            throw new ReverseSearchProviderException("No SauceNAO API key is configured.");
        }

        var fields = new List<KeyValuePair<string, string>>
        {
            new("output_type", "2"),
            new("api_key", settings.SauceNaoApiKey),
            new("db", "999"),
            new("numres", "10"),
            new("hide", "0")
        };

        string body;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = MinimumSpacing - (DateTimeOffset.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            body = await ReverseSearchHttp.PostMultipartAsync(
                    _client, Endpoint, fields, "file", query.Jpeg, query.FileName, RequestTimeout, cancellationToken)
                .ConfigureAwait(false);
            _lastCall = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        return Parse(body);
    }

    private static ProviderResponse Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("header", out var header))
        {
            throw new ReverseSearchProviderException("SauceNAO returned an unexpected response.");
        }

        var status = ReverseSearchJson.ReadInt(header, "status");
        if (status != 0)
        {
            var message = ReverseSearchJson.ReadString(header, "message") ?? "unknown error";
            throw new ReverseSearchProviderException($"SauceNAO rejected the request: {message}.");
        }

        var results = new List<ReverseSearchResult>();
        if (!root.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new ProviderResponse(results);
        }

        string? note = null;
        if (header.TryGetProperty("long_remaining", out var remaining) &&
            remaining.ValueKind == JsonValueKind.Number &&
            remaining.TryGetInt32(out var left) &&
            left >= 0 && left < 5)
        {
            note = $"SauceNAO quota is nearly used up ({left} searches left).";
        }

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("header", out var itemHeader))
            {
                continue;
            }

            var similarity = ReverseSearchJson.ReadDouble(itemHeader, "similarity") / 100d;
            if (similarity <= 0)
            {
                continue;
            }

            var indexId = ReverseSearchJson.ReadInt(itemHeader, "index_id");
            var indexName = ReverseSearchJson.ReadString(itemHeader, "index_name") ?? string.Empty;
            var site = IndexSite(indexId, indexName);
            var thumbnail = ReverseSearchJson.ReadString(itemHeader, "thumbnail");

            JsonElement data = default;
            if (!item.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
            {
                data = default;
            }

            var extUrl = ReadFirstUrl(data, "ext_urls");
            var pixivId = ReverseSearchJson.ReadInt(data, "pixiv_id");
            var danbooruId = ReverseSearchJson.ReadInt(data, "danbooru_id");
            var yandereId = ReverseSearchJson.ReadInt(data, "yandere_id");
            var konachanId = ReverseSearchJson.ReadInt(data, "konachan_id");
            var gelbooruId = ReverseSearchJson.ReadInt(data, "gelbooru_id");
            var sankakuId = ReverseSearchJson.ReadInt(data, "sankaku_id");
            var e621Id = ReverseSearchJson.ReadInt(data, "e621_id");

            string? pageUrl = extUrl;
            string? booruSite = null;
            int? booruId = null;

            if (danbooruId > 0)
            {
                pageUrl ??= $"https://danbooru.donmai.us/posts/{danbooruId}";
                booruSite = "danbooru";
                booruId = danbooruId;
            }
            else if (yandereId > 0)
            {
                pageUrl ??= $"https://yande.re/post/show/{yandereId}";
                booruSite = "yandere";
                booruId = yandereId;
            }
            else if (konachanId > 0)
            {
                pageUrl ??= $"https://konachan.com/post/show/{konachanId}";
                booruSite = "konachan";
                booruId = konachanId;
            }
            else if (gelbooruId > 0)
            {
                pageUrl ??= $"https://gelbooru.com/index.php?page=post&s=view&id={gelbooruId}";
            }
            else if (sankakuId > 0)
            {
                pageUrl ??= $"https://chan.sankakucomplex.com/post/show/{sankakuId}";
            }
            else if (e621Id > 0)
            {
                pageUrl ??= $"https://e621.net/posts/{e621Id}";
            }
            else if (pixivId > 0)
            {
                pageUrl ??= $"https://www.pixiv.net/artworks/{pixivId}";
            }

            var isAnime = indexId is 21 or 22 ||
                          indexName.Contains("Anime", StringComparison.OrdinalIgnoreCase);

            var title = ReverseSearchJson.FirstString(data, "title", "jp_name", "eng_name", "characters", "material", "studio");
            var artist = ReverseSearchJson.FirstString(data, "member_name", "creator", "author_name", "author");

            string? identification = null;
            if (isAnime)
            {
                var source = ReverseSearchJson.FirstString(data, "source", "anime");
                var time = ReverseSearchJson.ReadDouble(data, "est_time");
                if (!string.IsNullOrWhiteSpace(source))
                {
                    identification = time > 0
                        ? $"{source} at {TimeSpan.FromSeconds(time):m\\:ss}"
                        : source;
                }
            }

            results.Add(new ReverseSearchResult
            {
                ProviderId = "saucenao",
                ProviderLabel = "SauceNAO",
                SourceName = site,
                Title = title,
                Artist = artist,
                PageUrl = pageUrl,
                ThumbnailUrl = thumbnail,
                Similarity = Math.Round(similarity, 4),
                Identification = identification,
                IsIdentificationOnly = isAnime,
                BooruSite = booruSite,
                BooruPostId = booruId,
                PixivId = pixivId > 0 ? pixivId : null
            });
        }

        Logger.Info($"SauceNAO returned {results.Count} result(s).");
        return new ProviderResponse(results, note);
    }

    private static string IndexSite(int indexId, string indexName)
    {
        var colon = indexName.IndexOf(':');
        if (colon >= 0 && colon + 1 < indexName.Length)
        {
            return indexName[(colon + 1)..].Trim();
        }

        return indexId switch
        {
            5 or 6 => "Pixiv",
            9 => "Danbooru",
            11 => "Yande.re",
            12 => "Konachan",
            16 => "Gelbooru",
            18 => "Sankaku Channel",
            21 or 22 => "Anime",
            _ => "Artwork"
        };
    }

    private static string? ReadFirstUrl(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(name, out var urls) ||
            urls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var url in urls.EnumerateArray())
        {
            var value = url.GetString();
            if (ReverseSearchHttp.IsHttpUrl(value))
            {
                return value;
            }
        }

        return null;
    }

    public void Dispose() => _gate.Dispose();
}
