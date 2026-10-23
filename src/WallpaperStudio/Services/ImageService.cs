using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperStudio.Services;

public sealed class ImageProbeService
{
    public Task<ResolutionInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                if (SkiaImage.TryProbe(path, out var width, out var height))
                {
                    return new ResolutionInfo(width, height);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not read image dimensions for '{path}'.", ex);
            }

            return ResolutionInfo.Unknown;
        }, cancellationToken);
}

public sealed class ThumbnailService
{
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly ImageProbeService _probe = new();

    public ImageProbeService Probe => _probe;

    /// <summary>
    /// Produces a frozen BitmapImage from a cached JPEG thumbnail. All decoding is
    /// off the UI thread; the returned image is safe to bind.
    /// </summary>
    public async Task<ImageSource?> GetAsync(
        string sourcePath,
        int maxWidth,
        string cachePrefix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(PathHelper.ToExtendedPath(sourcePath)))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cachePath = BuildCachePath(sourcePath, maxWidth, cachePrefix);
            if (!File.Exists(cachePath))
            {
                await Task.Run(() => BuildThumbnail(sourcePath, cachePath, maxWidth, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!File.Exists(cachePath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
            return CreateFrozenBitmap(bytes, maxWidth);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Thumbnail failed for '{sourcePath}'.", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string BuildCachePath(string sourcePath, int maxWidth, string prefix)
    {
        var info = new FileInfo(PathHelper.ToExtendedPath(sourcePath));
        var raw = $"{PathHelper.Normalize(sourcePath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{maxWidth}|{prefix}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        return Path.Combine(AppPaths.ThumbnailCacheDirectory, $"{prefix}-{hash[..24]}.jpg");
    }

    private static void BuildThumbnail(string sourcePath, string cachePath, int maxWidth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var original = SkiaImage.DecodeOriented(sourcePath);
        if (original is null)
        {
            Logger.Warn($"Thumbnail skipped: '{sourcePath}' is not a supported image.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var thumbnail = SkiaImage.ResizeToFit(original, maxWidth);
        SkiaImage.SaveJpeg(thumbnail, cachePath, 86);
    }

    private static ImageSource CreateFrozenBitmap(byte[] bytes, int decodeWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.StreamSource = stream;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
