using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>
/// Small, dependency-light imaging layer on top of SkiaSharp (MIT licensed).
/// Everything here is synchronous; callers run it off the UI thread.
/// </summary>
internal static class SkiaImage
{
    private static readonly SKSamplingOptions ThumbnailSampling = new(SKCubicResampler.Mitchell);

    public static bool TryProbe(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                return false;
            }

            return TryGetOrientedSize(codec, out width, out height);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read image dimensions for '{path}'.", ex);
            return false;
        }
    }

    public static bool TryProbe(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var codec = SKCodec.Create(stream);
            return codec is not null && TryGetOrientedSize(codec, out width, out height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes an image and applies its EXIF orientation so the returned bitmap
    /// matches what the user sees in Explorer or a browser. Returns null for
    /// unsupported or corrupt files.
    /// </summary>
    public static SKBitmap? DecodeOriented(string path)
    {
        var origin = SKEncodedOrigin.TopLeft;
        using (var stream = OpenRead(path))
        using (var codec = SKCodec.Create(stream))
        {
            if (codec is null)
            {
                return null;
            }

            origin = codec.EncodedOrigin;
        }

        using var decodeStream = OpenRead(path);
        var bitmap = SKBitmap.Decode(decodeStream);
        if (bitmap is null)
        {
            return null;
        }

        return ApplyOrientation(bitmap, origin);
    }

    /// <summary>Decodes bytes, validates that they are complete, and returns the oriented bitmap.</summary>
    public static SKBitmap? DecodeOriented(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = SKBitmap.Decode(stream);
        if (bitmap is null)
        {
            return null;
        }

        return ApplyOrientation(bitmap, SKEncodedOrigin.TopLeft);
    }

    /// <summary>
    /// Scales the bitmap down so its longest edge is at most <paramref name="maxEdge"/>.
    /// Images that already fit are returned unchanged. Never upscales.
    /// </summary>
    public static SKBitmap ResizeToFit(SKBitmap source, int maxEdge)
    {
        if (maxEdge <= 0 || (source.Width <= maxEdge && source.Height <= maxEdge))
        {
            return source;
        }

        var scale = Math.Min((float)maxEdge / source.Width, (float)maxEdge / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);
        var resized = source.Resize(info, ThumbnailSampling);
        return resized ?? source;
    }

    public static void SaveJpeg(SKBitmap bitmap, string path, int quality)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
        using var stream = new FileStream(
            PathHelper.ToExtendedPath(path),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        data.SaveTo(stream);
    }

    public static void SavePng(SKBitmap bitmap, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new FileStream(
            PathHelper.ToExtendedPath(path),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        data.SaveTo(stream);
    }

    private static FileStream OpenRead(string path)
        => new(
            PathHelper.ToExtendedPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

    private static bool TryGetOrientedSize(SKCodec codec, out int width, out int height)
    {
        var info = codec.Info;
        var origin = codec.EncodedOrigin;
        var swapped = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

        width = swapped ? info.Height : info.Width;
        height = swapped ? info.Width : info.Height;
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Repaints the bitmap into its display orientation. The source bitmap is
    /// disposed when a new bitmap is returned.
    /// </summary>
    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source;
        }

        var swapped = origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;

        var width = swapped ? source.Height : source.Width;
        var height = swapped ? source.Width : source.Height;
        var info = new SKImageInfo(width, height, source.ColorType, source.AlphaType);
        var destination = new SKBitmap(info);

        using (var canvas = new SKCanvas(destination))
        {
            canvas.Translate(width / 2f, height / 2f);
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:
                    canvas.Scale(-1, 1);
                    break;
                case SKEncodedOrigin.BottomRight:
                    canvas.RotateDegrees(180);
                    break;
                case SKEncodedOrigin.BottomLeft:
                    canvas.Scale(1, -1);
                    break;
                case SKEncodedOrigin.LeftTop:
                    canvas.Scale(-1, 1);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightTop:
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.RightBottom:
                    canvas.Scale(1, -1);
                    canvas.RotateDegrees(90);
                    break;
                case SKEncodedOrigin.LeftBottom:
                    canvas.RotateDegrees(-90);
                    break;
            }

            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            canvas.Flush();
        }

        source.Dispose();
        return destination;
    }
}
