using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace WallpaperStudio.Services.ReverseSearch;

/// <summary>Shared HTTP plumbing: one pooled client, explicit timeouts, size caps.</summary>
internal static class ReverseSearchHttp
{
    public const string UserAgent =
        "WallpaperStudio/1.3 (personal wallpaper library; +https://github.com/hannsster/WallpaperEngine-Extraction-and-translation)";

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        var client = new HttpClient(handler)
        {
            // Every call gets its own linked timeout so one slow provider cannot
            // stall the whole search.
            Timeout = Timeout.InfiniteTimeSpan
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    public static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static async Task<string> PostMultipartAsync(
        HttpClient client,
        string url,
        IEnumerable<KeyValuePair<string, string>> fields,
        string fileField,
        byte[] file,
        string fileName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        foreach (var field in fields)
        {
            content.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
        }

        var fileContent = new ByteArrayContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(fileContent, fileField, fileName);

        using var response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, timeout, cancellationToken)
            .ConfigureAwait(false);
        return await ReadBodyAsync(response, url, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> PostJsonAsync(
        HttpClient client,
        string url,
        string json,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                },
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        return await ReadBodyAsync(response, url, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> GetStringAsync(
        HttpClient client,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, url), timeout, cancellationToken)
            .ConfigureAwait(false);
        return await ReadBodyAsync(response, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> from a URL. Used for provider
    /// thumbnails during a search and for user-approved full downloads.
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(
        HttpClient client,
        string url,
        long maxBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ReverseSearchProviderException($"The server returned {(int)response.StatusCode} for this image.");
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared is > 0 && declared > maxBytes)
        {
            throw new ReverseSearchProviderException(
                $"The image is {PathHelper.HumanSize(declared.Value)}, which is larger than the {PathHelper.HumanSize(maxBytes)} limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new ReverseSearchProviderException(
                    $"The image is larger than the {PathHelper.HumanSize(maxBytes)} limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> factory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = factory();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReverseSearchProviderException("The service did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ReverseSearchProviderException("The service could not be reached. Check your connection.", ex);
        }
    }

    private static async Task<string> ReadBodyAsync(
        HttpResponseMessage response,
        string url,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 300 ? body[..300] : body;
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            throw new ReverseSearchProviderException(
                $"{host} returned {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".Trim());
        }

        return body;
    }
}
