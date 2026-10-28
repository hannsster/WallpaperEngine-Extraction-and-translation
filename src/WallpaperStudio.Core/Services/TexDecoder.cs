using System.Buffers.Binary;
using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>
/// Read-only parser for Wallpaper Engine <c>.tex</c> textures (TEXB0001–TEXB0004).
/// It understands the two storage modes: a plain embedded JPEG/PNG image
/// container (image type 2/13) and raw or LZ4-compressed GPU pixel data
/// (RGBA8/BC1/BC2/BC3/RG8/R8). The container layout was reconstructed from real
/// files and a public open-source reader; it is not an official Valve
/// specification, so extraction is best-effort and every result is validated
/// before it is offered.
/// </summary>
public static class TexDecoder
{
    public const int ImageTypeJpeg = 2;
    public const int ImageTypePng = 13;

    private const int MaxEdge = 32768;
    private const int MinimumHeaderBytes = 64;

    /// <summary>
    /// Reads the picture size (the map dimensions the scene uses, which can be
    /// smaller than the padded texture) plus the container type and pixel format.
    /// Only the texture header needs to be present, so this also works on a
    /// partial read.
    /// </summary>
    public static bool TryReadInfo(byte[] data, out int width, out int height, out int imageType, out int format)
    {
        width = 0;
        height = 0;
        imageType = -1;
        format = -1;
        if (!TryParseHeader(data, out var header))
        {
            return false;
        }

        width = header.PictureWidth;
        height = header.PictureHeight;
        imageType = header.ImageType;
        format = header.Format;
        return width > 0 && height > 0;
    }

    /// <summary>True when the payload is a self-contained JPEG/PNG image rather than GPU pixel data.</summary>
    public static bool IsImageContainer(int imageType) => imageType is ImageTypeJpeg or ImageTypePng;

    /// <summary>
    /// Decodes the first mip of the first slot into a bitmap at the stored mip
    /// size. Callers that want the scene picture size should crop to the value
    /// reported by <see cref="TryReadInfo"/>.
    /// </summary>
    public static SKBitmap? Decode(byte[] data)
    {
        if (!TryParseHeader(data, out var header))
        {
            return null;
        }

        if (header.SourceSize <= 0 ||
            header.PayloadOffset < 0 ||
            (long)header.PayloadOffset + header.SourceSize > data.Length)
        {
            return null;
        }

        var payload = new ReadOnlySpan<byte>(data, header.PayloadOffset, header.SourceSize);

        if (IsImageContainer(header.ImageType))
        {
            return SkiaImage.DecodeOriented(payload.ToArray());
        }

        if (!BlockTextureDecoder.IsSupported(header.Format))
        {
            return null;
        }

        byte[] raw;
        if (header.Lz4Compressed)
        {
            if (!Lz4BlockDecoder.TryDecompress(payload, header.DecompressedSize, out raw))
            {
                Logger.Warn("A .tex texture declared LZ4 data that could not be decompressed.");
                return null;
            }
        }
        else
        {
            raw = payload.ToArray();
        }

        if (!BlockTextureDecoder.TryDecodeToRgba(header.Format, raw, header.MipWidth, header.MipHeight, out var rgba))
        {
            return null;
        }

        return CreateBitmap(rgba, header.MipWidth, header.MipHeight);
    }

    private static SKBitmap? CreateBitmap(byte[] rgba, int width, int height)
    {
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not build a bitmap from decoded texture pixels.", ex);
            return null;
        }
    }

    private static bool TryParseHeader(byte[] data, out TexHeader header)
    {
        header = default;
        if (data.Length < MinimumHeaderBytes)
        {
            return false;
        }

        var offset = 0;

        // "TEXV0005\0" then "TEXI0001\0": each version tag is 8 characters plus a NUL byte.
        if (!TryReadTag(data, ref offset, "TEX", out _))
        {
            return false;
        }

        if (!TryReadTag(data, ref offset, "TEX", out _))
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out var format))
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out _)) // flags
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out var textureWidth) ||
            !TryReadInt32(data, ref offset, out var textureHeight) ||
            !TryReadInt32(data, ref offset, out var mapWidth) ||
            !TryReadInt32(data, ref offset, out var mapHeight) ||
            !TryReadInt32(data, ref offset, out _)) // reserved / unknown
        {
            return false;
        }

        if (!TryReadTag(data, ref offset, "TEXB", out var texbVersion))
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out var slotCount) || slotCount < 1 || slotCount > 8192)
        {
            return false;
        }

        var imageType = -1;
        if (texbVersion >= 3 && !TryReadInt32(data, ref offset, out imageType))
        {
            return false;
        }

        if (texbVersion >= 4 && !TryReadInt32(data, ref offset, out _)) // extra int32, present from TEXB0004
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out var mipCount) || mipCount < 1 || mipCount > 64)
        {
            return false;
        }

        if (!TryReadInt32(data, ref offset, out var mipWidth) ||
            !TryReadInt32(data, ref offset, out var mipHeight))
        {
            return false;
        }

        var lz4 = false;
        var decompressedSize = 0;
        if (texbVersion > 1)
        {
            if (!TryReadInt32(data, ref offset, out var lz4Flag) ||
                !TryReadInt32(data, ref offset, out decompressedSize))
            {
                return false;
            }

            lz4 = lz4Flag == 1;
        }

        if (!TryReadInt32(data, ref offset, out var sourceSize) || sourceSize <= 0)
        {
            return false;
        }

        if (mipWidth <= 0 || mipHeight <= 0 || mipWidth > MaxEdge || mipHeight > MaxEdge)
        {
            return false;
        }

        var pictureWidth = mapWidth > 0 && mapWidth <= mipWidth ? mapWidth : mipWidth;
        var pictureHeight = mapHeight > 0 && mapHeight <= mipHeight ? mapHeight : mipHeight;

        header = new TexHeader(
            format,
            imageType,
            pictureWidth,
            pictureHeight,
            mipWidth,
            mipHeight,
            textureWidth,
            textureHeight,
            lz4,
            decompressedSize,
            sourceSize,
            offset);
        return true;
    }

    private static bool TryReadTag(byte[] data, ref int offset, string prefix, out int version)
    {
        version = 0;
        if (offset < 0 || offset + 9 > data.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (data[offset + index] != prefix[index])
            {
                return false;
            }
        }

        var digits = 0;
        for (var index = 4; index < 8; index++)
        {
            var c = data[offset + index];
            if (c < '0' || c > '9')
            {
                return false;
            }

            digits = (digits * 10) + (c - '0');
        }

        version = digits;
        offset += 9;
        return true;
    }

    private static bool TryReadInt32(byte[] data, ref int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;
        return true;
    }

    private readonly record struct TexHeader(
        int Format,
        int ImageType,
        int PictureWidth,
        int PictureHeight,
        int MipWidth,
        int MipHeight,
        int TextureWidth,
        int TextureHeight,
        bool Lz4Compressed,
        int DecompressedSize,
        int SourceSize,
        int PayloadOffset);
}
