using System.Collections.Concurrent;
using System.Text.Json;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// Turns a booru post id into exact pixel dimensions and a direct file URL by
/// calling the booru's own public JSON API. SauceNAO itself does not report
/// resolution, and a resolution is what makes a "higher-res original" claim
/// meaningful.
/// </summary>
public sealed class BooruEnricher
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, BooruPost?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BooruEnricher(HttpClient client)
    {
        _client = client;
    }

    public async Task EnrichAsync(IReadOnlyList<ReverseSearchResult> results, CancellationToken cancellationToken)
    {
        var targets = results
            .Where(result => result.BooruPostId is > 0 && !string.IsNullOrWhiteSpace(result.BooruSite))
            .Take(8)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(3, 3);
        var tasks = targets.Select(async result =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var post = await GetPostAsync(result.BooruSite!, result.BooruPostId!.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (post is null)
                {
                    return;
                }

                if (post.Width > 0 && post.Height > 0)
                {
                    result.Width = post.Width;
                    result.Height = post.Height;
                }

                if (!string.IsNullOrWhiteSpace(post.FileUrl))
                {
                    result.ImageUrl = post.FileUrl;
                }

                result.Title ??= post.Title;
                result.Artist ??= post.Artist;
                if (string.IsNullOrWhiteSpace(result.PageUrl))
                {
                    result.PageUrl = post.PageUrl;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Looks up one booru post. Failures are silent: enrichment is optional.</summary>
    public async Task<BooruPost?> GetPostAsync(string site, int id, CancellationToken cancellationToken)
    {
        var key = site + ":" + id;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            BooruPost? post = site.ToLowerInvariant() switch
            {
                "danbooru" => await GetDanbooruAsync(id, cancellationToken).ConfigureAwait(false),
                "yandere" => await GetMoebooruAsync("https://yande.re", "https://yande.re/post/show/" + id, id, cancellationToken)
                    .ConfigureAwait(false),
                "konachan" => await GetMoebooruAsync("https://konachan.net", "https://konachan.com/post/show/" + id, id, cancellationToken)
                    .ConfigureAwait(false),
                _ => null
            };

            _cache[key] = post;
            return post;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Booru enrichment failed for {site}:{id}: {ex.Message}");
            _cache[key] = null;
            return null;
        }
    }

    private async Task<BooruPost?> GetDanbooruAsync(int id, CancellationToken cancellationToken)
    {
        var body = await ReverseSearchHttp
            .GetStringAsync(_client, $"https://danbooru.donmai.us/posts/{id}.json", RequestTimeout, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var width = ReverseSearchJson.ReadInt(root, "image_width");
        var height = ReverseSearchJson.ReadInt(root, "image_height");
        var fileUrl = ReverseSearchJson.FirstString(root, "file_url", "large_file_url", "source");
        var artist = ReverseSearchJson.FirstString(root, "tag_string_artist");
        var title = ReverseSearchJson.FirstString(root, "tag_string_character");

        return new BooruPost(
            width,
            height,
            NormalizeUrl(fileUrl, "https://danbooru.donmai.us"),
            title,
            artist,
            $"https://danbooru.donmai.us/posts/{id}");
    }

    private async Task<BooruPost?> GetMoebooruAsync(
        string apiHost,
        string pageUrl,
        int id,
        CancellationToken cancellationToken)
    {
        var body = await ReverseSearchHttp
            .GetStringAsync(_client, $"{apiHost}/post.json?tags=id:{id}", RequestTimeout, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        var post = root[0];
        var width = ReverseSearchJson.ReadInt(post, "width");
        var height = ReverseSearchJson.ReadInt(post, "height");
        var fileUrl = ReverseSearchJson.FirstString(post, "file_url", "jpeg_url", "sample_url");
        var artist = ReverseSearchJson.FirstString(post, "author");
        var tags = ReverseSearchJson.ReadString(post, "tags");

        return new BooruPost(
            width,
            height,
            NormalizeUrl(fileUrl, apiHost),
            null,
            artist,
            pageUrl);
    }

    private static string? NormalizeUrl(string? value, string host)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + value;
        }

        if (value.StartsWith('/'))
        {
            return host.TrimEnd('/') + value;
        }

        return ReverseSearchHttp.IsHttpUrl(value) ? value : null;
    }
}

public sealed record BooruPost(
    int Width,
    int Height,
    string? FileUrl,
    string? Title,
    string? Artist,
    string? PageUrl);
