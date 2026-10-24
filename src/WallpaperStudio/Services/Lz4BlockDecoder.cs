namespace WallpaperStudio.Services;

/// <summary>
/// Minimal, dependency-free decoder for the LZ4 block format used inside
/// Wallpaper Engine texture mipmaps. It only decompresses a single block into a
/// caller-supplied expected size and never allocates more than that.
/// </summary>
public static class Lz4BlockDecoder
{
    private const int MaxOutputBytes = 512 * 1024 * 1024;

    public static bool TryDecompress(ReadOnlySpan<byte> source, int expectedSize, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (expectedSize <= 0 || expectedSize > MaxOutputBytes || source.Length == 0)
        {
            return false;
        }

        var output = new byte[expectedSize];
        var sourceIndex = 0;
        var outputIndex = 0;

        while (sourceIndex < source.Length)
        {
            var token = source[sourceIndex++];

            var literalLength = token >> 4;
            if (literalLength == 15)
            {
                int extra;
                do
                {
                    if (sourceIndex >= source.Length)
                    {
                        return false;
                    }

                    extra = source[sourceIndex++];
                    literalLength += extra;
                }
                while (extra == 255);
            }

            if (literalLength > 0)
            {
                if (literalLength > source.Length - sourceIndex || literalLength > output.Length - outputIndex)
                {
                    return false;
                }

                source.Slice(sourceIndex, literalLength).CopyTo(output.AsSpan(outputIndex));
                sourceIndex += literalLength;
                outputIndex += literalLength;
            }

            if (sourceIndex >= source.Length)
            {
                break;
            }

            if (sourceIndex + 2 > source.Length)
            {
                return false;
            }

            var offset = source[sourceIndex] | (source[sourceIndex + 1] << 8);
            sourceIndex += 2;
            if (offset <= 0 || offset > outputIndex)
            {
                return false;
            }

            var matchLength = token & 0x0F;
            if (matchLength == 15)
            {
                int extra;
                do
                {
                    if (sourceIndex >= source.Length)
                    {
                        return false;
                    }

                    extra = source[sourceIndex++];
                    matchLength += extra;
                }
                while (extra == 255);
            }

            matchLength += 4;
            if (matchLength > output.Length - outputIndex)
            {
                return false;
            }

            var matchStart = outputIndex - offset;
            for (var index = 0; index < matchLength; index++)
            {
                output[outputIndex + index] = output[matchStart + index];
            }

            outputIndex += matchLength;
        }

        if (outputIndex != expectedSize)
        {
            return false;
        }

        result = output;
        return true;
    }
}
