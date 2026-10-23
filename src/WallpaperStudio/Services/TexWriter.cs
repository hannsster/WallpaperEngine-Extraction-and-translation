using System.Text;
using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>
/// Writes a minimal Wallpaper Engine <c>TEXB0003</c> texture that embeds a single
/// pre-compressed JPEG or PNG image (the format's image-container mode). The
/// layout was reconstructed from a real .tex file and a public open-source reader;
/// it is not an official Valve specification, so scenes built this way are
/// labelled experimental and must be verified in Wallpaper Engine before sharing.
/// </summary>
internal static class TexWriter
{
    public const int ImageTypeJpeg = 2;
    public const int ImageTypePng = 13;

    /// <summary>Writes a single-mip texture container that embeds the supplied encoded image bytes.</summary>
    public static void Write(string path, byte[] encodedImage, int width, int height, int imageType)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var textureSize = NextPowerOfTwo(Math.Max(width, height));

        using var stream = new FileStream(
            PathHelper.ToExtendedPath(path),
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);

        WriteTag(writer, "TEXV0005");
        WriteTag(writer, "TEXI0001");
        writer.Write(0);            // format: RGBA8888
        writer.Write((uint)2);      // flags: clamp UVs
        writer.Write(textureSize);  // allocated texture size (power of two)
        writer.Write(textureSize);
        writer.Write(width);        // real picture size
        writer.Write(height);
        writer.Write(0);            // reserved / unknown
        WriteTag(writer, "TEXB0003");
        writer.Write(1);            // image slot count
        writer.Write(imageType);    // 2 = JPEG, 13 = PNG
        writer.Write(1);            // mipmap count for slot 0
        writer.Write(width);
        writer.Write(height);
        writer.Write(0);            // LZ4 compressed flag
        writer.Write(0);            // decompressed size (unused when not compressed)
        writer.Write(encodedImage.Length);
        writer.Write(encodedImage);
    }

    /// <summary>Encodes a bitmap and writes it into a texture container.</summary>
    public static void Write(SKBitmap bitmap, string path, bool usePng)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(usePng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, usePng ? 100 : 92);
        Write(path, data.ToArray(), bitmap.Width, bitmap.Height, usePng ? ImageTypePng : ImageTypeJpeg);
    }

    private static void WriteTag(BinaryWriter writer, string tag)
    {
        writer.Write(Encoding.ASCII.GetBytes(tag));
        writer.Write((byte)0);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }
}
