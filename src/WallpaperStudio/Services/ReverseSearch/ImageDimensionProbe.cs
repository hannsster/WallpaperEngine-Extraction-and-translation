using System.Globalization;
using System.Net;

namespace WallpaperStudio.Services.ReverseSearch;

public sealed record ImageProbeResult(int Width, int Height, long? ContentLength, bool IsImage)
{
    public bool HasSize => Width > 0 && Height > 0;

    public long PixelCount => (long)Width * Height;
}

/// <summary>
/// Reads only the first bytes of a remote image to learn its dimensions. This
/// lets the app rank "higher resolution" results without downloading the whole
/// file, which is the part that needs explicit user consent.
/// </summary>
public sealed class ImageDimensionProbe
{
    private const int RangeBytes = 262_144;
    private const long MaximumDownloadBytes = 120L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public ImageDimensionProbe(HttpClient client)
    {
        _client = client;
    }

    public async Task<ImageProbeResult?> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!ReverseSearchHttp.IsHttpUrl(url))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Range", $"bytes=0-{RangeBytes - 1}");
            request.Headers.TryAddWithoutValidation("Accept", "image/*,*/*;q=0.8");

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var declared = ReadTotalLength(response);
            if (declared is > MaximumDownloadBytes)
            {
                return new ImageProbeResult(0, 0, declared, false);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32_768];
            while (buffer.Length < RangeBytes)
            {
                var read = await stream.ReadAsync(chunk, timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            if (SkiaImage.TryProbe(bytes, out var width, out var height))
            {
                return new ImageProbeResult(width, height, declared, true);
            }

            return new ImageProbeResult(0, 0, declared, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Dimension probe failed for '{url}': {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long? ReadTotalLength(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentRange is { HasLength: true } range && range.Length is > 0)
        {
            return range.Length;
        }

        if (response.Headers.TryGetValues("Content-Length", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) &&
            length > 0)
        {
            return length;
        }

        return response.Content.Headers.ContentLength;
    }
}
