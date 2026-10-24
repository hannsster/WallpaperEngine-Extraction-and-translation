namespace WallpaperStudio.Services;

/// <summary>
/// Decodes the small set of GPU texture formats Wallpaper Engine uses inside
/// <c>.tex</c> files into straight RGBA bytes. Formats: 0=RGBA8888, 4=BC3 (DXT5),
/// 6=BC2 (DXT3), 7=BC1 (DXT1), 8=RG8, 9=R8. The block-compression layouts are
/// the public S3TC/BC specifications; nothing here is Wallpaper-Engine specific.
/// </summary>
public static class BlockTextureDecoder
{
    public const int FormatRgba8888 = 0;
    public const int FormatBc3 = 4;
    public const int FormatBc2 = 6;
    public const int FormatBc1 = 7;
    public const int FormatRg8 = 8;
    public const int FormatR8 = 9;

    private const int MaxEdge = 32768;

    public static int BytesPerPixel(int format) => format switch
    {
        FormatRgba8888 => 4,
        FormatRg8 => 2,
        FormatR8 => 1,
        _ => 0
    };

    /// <summary>Expected decoded byte count for a raw mip payload, or -1 for unknown formats.</summary>
    public static long ExpectedRawSize(int format, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxEdge || height > MaxEdge)
        {
            return -1;
        }

        return format switch
        {
            FormatBc1 => (long)((width + 3) / 4) * ((height + 3) / 4) * 8,
            FormatBc2 or FormatBc3 => (long)((width + 3) / 4) * ((height + 3) / 4) * 16,
            FormatRgba8888 => (long)width * height * 4,
            FormatRg8 => (long)width * height * 2,
            FormatR8 => (long)width * height,
            _ => -1
        };
    }

    public static bool IsSupported(int format)
        => format is FormatRgba8888 or FormatBc1 or FormatBc2 or FormatBc3 or FormatRg8 or FormatR8;

    public static bool TryDecodeToRgba(int format, byte[] data, int width, int height, out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (width <= 0 || height <= 0 || width > MaxEdge || height > MaxEdge || !IsSupported(format))
        {
            return false;
        }

        var expected = ExpectedRawSize(format, width, height);
        if (expected < 0 || data.Length < expected)
        {
            return false;
        }

        var output = new byte[(long)width * height * 4];
        switch (format)
        {
            case FormatRgba8888:
                Buffer.BlockCopy(data, 0, output, 0, (int)expected);
                break;
            case FormatBc1:
                DecodeBc1(data, output, width, height);
                break;
            case FormatBc2:
                DecodeBc2(data, output, width, height);
                break;
            case FormatBc3:
                DecodeBc3(data, output, width, height);
                break;
            case FormatRg8:
                DecodeRg8(data, output, width, height);
                break;
            case FormatR8:
                DecodeR8(data, output, width, height);
                break;
            default:
                return false;
        }

        rgba = output;
        return true;
    }

    private static void DecodeBc1(byte[] data, byte[] output, int width, int height)
    {
        var blocksPerRow = (width + 3) / 4;
        var blocksPerColumn = (height + 3) / 4;
        var source = 0;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[4];

        for (var blockY = 0; blockY < blocksPerColumn; blockY++)
        {
            for (var blockX = 0; blockX < blocksPerRow; blockX++)
            {
                var c0 = data[source] | (data[source + 1] << 8);
                var c1 = data[source + 2] | (data[source + 3] << 8);
                var bits = data[source + 4] | (data[source + 5] << 8) | (data[source + 6] << 16) | (data[source + 7] << 24);
                source += 8;

                BuildColorPalette(c0, c1, colors, alpha, fourColorMode: c0 > c1);
                WriteBlock(output, width, height, blockX, blockY, colors, bits, alpha, 0);
            }
        }
    }

    private static void DecodeBc2(byte[] data, byte[] output, int width, int height)
    {
        var blocksPerRow = (width + 3) / 4;
        var blocksPerColumn = (height + 3) / 4;
        var source = 0;
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[4];

        for (var blockY = 0; blockY < blocksPerColumn; blockY++)
        {
            for (var blockX = 0; blockX < blocksPerRow; blockX++)
            {
                var explicitAlpha = (ulong)((uint)(data[source] | (data[source + 1] << 8) | (data[source + 2] << 16) | (data[source + 3] << 24)))
                                    | ((ulong)(uint)(data[source + 4] | (data[source + 5] << 8) | (data[source + 6] << 16) | (data[source + 7] << 24)) << 32);
                var c0 = data[source + 8] | (data[source + 9] << 8);
                var c1 = data[source + 10] | (data[source + 11] << 8);
                var bits = data[source + 12] | (data[source + 13] << 8) | (data[source + 14] << 16) | (data[source + 15] << 24);
                source += 16;

                BuildColorPalette(c0, c1, colors, alpha, fourColorMode: true);
                WriteBlock(output, width, height, blockX, blockY, colors, bits, alpha, explicitAlpha);
            }
        }
    }

    private static void DecodeBc3(byte[] data, byte[] output, int width, int height)
    {
        var blocksPerRow = (width + 3) / 4;
        var blocksPerColumn = (height + 3) / 4;
        var source = 0;
        Span<byte> alphaPalette = stackalloc byte[8];
        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[4];

        for (var blockY = 0; blockY < blocksPerColumn; blockY++)
        {
            for (var blockX = 0; blockX < blocksPerRow; blockX++)
            {
                var a0 = data[source];
                var a1 = data[source + 1];
                alphaPalette[0] = a0;
                alphaPalette[1] = a1;
                if (a0 > a1)
                {
                    for (var i = 1; i <= 6; i++)
                    {
                        alphaPalette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                    }
                }
                else
                {
                    for (var i = 1; i <= 4; i++)
                    {
                        alphaPalette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                    }

                    alphaPalette[6] = 0;
                    alphaPalette[7] = 255;
                }

                var alphaBits = (ulong)(uint)(data[source + 2] | (data[source + 3] << 8) | (data[source + 4] << 16) | (data[source + 5] << 24))
                                | ((ulong)(uint)(data[source + 6] | (data[source + 7] << 8)) << 32);

                var c0 = data[source + 8] | (data[source + 9] << 8);
                var c1 = data[source + 10] | (data[source + 11] << 8);
                var bits = data[source + 12] | (data[source + 13] << 8) | (data[source + 14] << 16) | (data[source + 15] << 24);
                source += 16;

                BuildColorPalette(c0, c1, colors, alpha, fourColorMode: true);
                WriteBlockInterpolatedAlpha(output, width, height, blockX, blockY, colors, bits, alphaPalette, alphaBits);
            }
        }
    }

    private static void DecodeRg8(byte[] data, byte[] output, int width, int height)
    {
        var source = 0;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                output[destination] = data[source];
                output[destination + 1] = data[source + 1];
                output[destination + 2] = 0;
                output[destination + 3] = 255;
                source += 2;
                destination += 4;
            }
        }
    }

    private static void DecodeR8(byte[] data, byte[] output, int width, int height)
    {
        var source = 0;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[source++];
                output[destination] = value;
                output[destination + 1] = value;
                output[destination + 2] = value;
                output[destination + 3] = 255;
                destination += 4;
            }
        }
    }

    private static void BuildColorPalette(int c0, int c1, Span<byte> colors, Span<byte> alpha, bool fourColorMode)
    {
        Expand565(c0, out var r0, out var g0, out var b0);
        Expand565(c1, out var r1, out var g1, out var b1);

        colors[0] = r0; colors[1] = g0; colors[2] = b0;
        colors[4] = r1; colors[5] = g1; colors[6] = b1;

        if (fourColorMode)
        {
            colors[8] = (byte)((2 * r0 + r1) / 3); colors[9] = (byte)((2 * g0 + g1) / 3); colors[10] = (byte)((2 * b0 + b1) / 3);
            colors[12] = (byte)((r0 + 2 * r1) / 3); colors[13] = (byte)((g0 + 2 * g1) / 3); colors[14] = (byte)((b0 + 2 * b1) / 3);
            alpha[0] = alpha[1] = alpha[2] = alpha[3] = 255;
        }
        else
        {
            colors[8] = (byte)((r0 + r1) / 2); colors[9] = (byte)((g0 + g1) / 2); colors[10] = (byte)((b0 + b1) / 2);
            colors[12] = 0; colors[13] = 0; colors[14] = 0;
            alpha[0] = alpha[1] = alpha[2] = 255;
            alpha[3] = 0;
        }
    }

    private static void WriteBlock(byte[] output, int width, int height, int blockX, int blockY, Span<byte> colors, int bits, Span<byte> alpha, ulong explicitAlpha)
    {
        for (var pixelY = 0; pixelY < 4; pixelY++)
        {
            var y = blockY * 4 + pixelY;
            if (y >= height)
            {
                break;
            }

            for (var pixelX = 0; pixelX < 4; pixelX++)
            {
                var x = blockX * 4 + pixelX;
                if (x >= width)
                {
                    continue;
                }

                var pixel = pixelY * 4 + pixelX;
                var colorIndex = (bits >> (2 * pixel)) & 0x3;
                var destination = (y * width + x) * 4;
                output[destination] = colors[colorIndex * 4];
                output[destination + 1] = colors[colorIndex * 4 + 1];
                output[destination + 2] = colors[colorIndex * 4 + 2];
                output[destination + 3] = explicitAlpha != 0
                    ? (byte)(((explicitAlpha >> (4 * pixel)) & 0xF) * 17)
                    : alpha[colorIndex];
            }
        }
    }

    private static void WriteBlockInterpolatedAlpha(byte[] output, int width, int height, int blockX, int blockY, Span<byte> colors, int bits, Span<byte> alphaPalette, ulong alphaBits)
    {
        for (var pixelY = 0; pixelY < 4; pixelY++)
        {
            var y = blockY * 4 + pixelY;
            if (y >= height)
            {
                break;
            }

            for (var pixelX = 0; pixelX < 4; pixelX++)
            {
                var x = blockX * 4 + pixelX;
                if (x >= width)
                {
                    continue;
                }

                var pixel = pixelY * 4 + pixelX;
                var colorIndex = (bits >> (2 * pixel)) & 0x3;
                var alphaIndex = (int)((alphaBits >> (3 * pixel)) & 0x7);
                var destination = (y * width + x) * 4;
                output[destination] = colors[colorIndex * 4];
                output[destination + 1] = colors[colorIndex * 4 + 1];
                output[destination + 2] = colors[colorIndex * 4 + 2];
                output[destination + 3] = alphaPalette[alphaIndex];
            }
        }
    }

    private static void Expand565(int color, out byte r, out byte g, out byte b)
    {
        var red = (color >> 11) & 0x1F;
        var green = (color >> 5) & 0x3F;
        var blue = color & 0x1F;
        r = (byte)((red << 3) | (red >> 2));
        g = (byte)((green << 2) | (green >> 4));
        b = (byte)((blue << 3) | (blue >> 2));
    }
}
