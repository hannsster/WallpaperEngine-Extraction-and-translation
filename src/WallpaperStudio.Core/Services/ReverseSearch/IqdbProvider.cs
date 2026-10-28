using System.Net;
using System.Text.RegularExpressions;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>
/// IQDB is a keyless, multi-booru reverse image search: one upload is checked
/// against Danbooru, Konachan, yande.re, Gelbooru, Sankaku, e-shuushuu, Zerochan
/// and Anime-Pictures at once. It has no JSON API, so the app posts the query
/// frame to the same public form a browser uses and reads the result table.
/// Only the search form and its result page are touched; there is no login,
/// no cookie jar and no script execution. IQDB only reports a page and a match
/// score, so a hit is enriched through the booru's own public API when possible.
/// </summary>
public sealed class IqdbProvider : IReverseImageProvider, IDisposable
{
    private const string Endpoint = "https://iqdb.org/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(2);

    // IQDB's markup is stable but old HTML 4; these patterns only look at the
    // handful of cells a result card is built from.
    private static readonly Regex ResultPattern = new(
        """<td\s+class\s*=\s*['"]image['"]\s*>\s*<a\s+href\s*=\s*['"](?<page>[^'"]+)['"][^>]*>\s*<img[^>]*?src\s*=\s*['"](?<thumb>[^'"]+)['"]""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SimilarityPattern = new(
        @"(?<value>\d{1,3})\s*%\s*similarity",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DimensionPattern = new(
        @"<td>\s*(?<width>\d{2,5})\s*[x×]\s*(?<height>\d{2,5})\b[^<]*</td>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RatingPattern = new(
        @"<td>\s*\d{2,5}\s*[x×]\s*\d{2,5}\s*\[(?<rating>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ErrorPattern = new(
        """<div\s+class\s*=\s*['"]err['"][^>]*>(?<message>.*?)</div>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    public IqdbProvider(HttpClient client)
    {
        _client = client;
    }

    public string Id => "iqdb";

    public string Label => "IQDB";

    public bool IsEnabled(AppSettings settings) => settings.ReverseSearchEnabled && settings.ReverseSearchIqdb;

    public bool IsConfigured(AppSettings settings) => true;

    public string ConfigurationHint => "Enable IQDB in Settings to search the major anime boorus.";

    public async Task<ProviderResponse> SearchAsync(
        ReverseImageQuery query,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        string body;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // IQDB explicitly asks clients not to fire queries in parallel.
            var wait = MinimumSpacing - (DateTimeOffset.UtcNow - _lastCall);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            body = await ReverseSearchHttp.PostMultipartAsync(
                    _client, Endpoint, [], "file", query.Jpeg, query.FileName, RequestTimeout, cancellationToken)
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
        var error = ErrorPattern.Match(body);
        if (error.Success)
        {
            var message = Clean(error.Groups["message"].Value);
            // The search page carries this banner for JavaScript-free browsers;
            // it is not an answer to our query.
            if (!string.IsNullOrWhiteSpace(message) &&
                !message.Contains("direct query links", StringComparison.OrdinalIgnoreCase))
            {
                throw new ReverseSearchProviderException($"IQDB: {message}");
            }
        }

        if (!body.Contains("Search results", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReverseSearchProviderException("IQDB answered with an unexpected page instead of results.");
        }

        var results = new List<ReverseSearchResult>();
        foreach (Match match in ResultPattern.Matches(body))
        {
            var segment = Block(body, match.Index);
            if (segment is null)
            {
                continue;
            }

            var similarityMatch = SimilarityPattern.Match(segment);
            if (!similarityMatch.Success ||
                !int.TryParse(similarityMatch.Groups["value"].Value, out var percent) ||
                percent <= 0)
            {
                continue;
            }

            var pageUrl = Normalize(match.Groups["page"].Value, isPage: true);
            if (pageUrl is null)
            {
                continue;
            }

            var width = 0;
            var height = 0;
            var dimensionMatch = DimensionPattern.Match(segment);
            if (dimensionMatch.Success)
            {
                _ = int.TryParse(dimensionMatch.Groups["width"].Value, out width);
                _ = int.TryParse(dimensionMatch.Groups["height"].Value, out height);
            }

            var (booruSite, booruId) = BooruIdentity(pageUrl);
            var rating = RatingPattern.Match(segment) is { Success: true } ratingMatch
                ? Clean(ratingMatch.Groups["rating"].Value)
                : null;

            results.Add(new ReverseSearchResult
            {
                ProviderId = "iqdb",
                ProviderLabel = "IQDB",
                SourceName = SiteName(pageUrl),
                PageUrl = pageUrl,
                // IQDB serves a cached preview at this path; it is never the full image.
                ThumbnailUrl = Normalize(match.Groups["thumb"].Value, isPage: false),
                Width = width,
                Height = height,
                Similarity = Math.Round(Math.Clamp(percent / 100d, 0, 1), 4),
                Identification = string.IsNullOrWhiteSpace(rating) ||
                                 rating.Equals("Safe", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : rating,
                BooruSite = booruSite,
                BooruPostId = booruId
            });
        }

        Logger.Info($"IQDB returned {results.Count} result(s).");
        return new ProviderResponse(results);
    }

    /// <summary>One result card's markup: from its image cell to the end of its table.</summary>
    private static string? Block(string body, int start)
    {
        var end = body.IndexOf("</table>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            end = Math.Min(body.Length, start + 4000);
        }

        return end > start ? body[start..end] : null;
    }

    private static string? Normalize(string? value, bool isPage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var url = WebUtility.HtmlDecode(value.Trim());
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (url.StartsWith('/'))
        {
            url = "https://iqdb.org" + url;
        }

        return ReverseSearchHttp.IsHttpUrl(url) ? url : null;
    }

    /// <summary>Only the boorus with a public JSON API are worth enriching.</summary>
    private static (string? Site, int? Id) BooruIdentity(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            return (null, null);
        }

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host.EndsWith("danbooru.donmai.us", StringComparison.Ordinal) &&
            segments.Length >= 2 &&
            segments[0].Equals("posts", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[1], out var danbooruId))
        {
            return ("danbooru", danbooruId);
        }

        if ((host.EndsWith("konachan.com", StringComparison.Ordinal) ||
             host.EndsWith("yande.re", StringComparison.Ordinal)) &&
            segments.Length >= 3 &&
            segments[0].Equals("post", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("show", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(segments[2], out var moeId))
        {
            return (host.EndsWith("yande.re", StringComparison.Ordinal) ? "yandere" : "konachan", moeId);
        }

        return (null, null);
    }

    private static string SiteName(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
        {
            return "Artwork";
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        return host switch
        {
            "danbooru.donmai.us" => "Danbooru",
            "konachan.com" or "konachan.net" => "Konachan",
            "yande.re" => "yande.re",
            "gelbooru.com" => "Gelbooru",
            "chan.sankakucomplex.com" => "Sankaku Channel",
            "e-shuushuu.net" => "e-shuushuu",
            "zerochan.net" => "Zerochan",
            "anime-pictures.net" => "Anime-Pictures",
            _ => host
        };
    }

    private static string Clean(string html)
        => WebUtility.HtmlDecode(TagPattern.Replace(html, " ")).Trim();

    public void Dispose() => _gate.Dispose();
}
