namespace WallpaperStudio.Services;

/// <summary>
/// Conservative, read-only fallback for .pkg files without a sibling preview.
/// It only looks for standalone JPEG/PNG streams embedded in the package and
/// validates that they decode and resemble a wallpaper preview. It never
/// decompresses or interprets the scene package format itself.
/// </summary>
public sealed class PkgPreviewExtractor
{
    private const long HeadScanBytes = 96L * 1024 * 1024;
    private const long TailScanBytes = 24L * 1024 * 1024;
    private const int ChunkSize = 4 * 1024 * 1024;
    private const int MaxImageBytes = 48 * 1024 * 1024;

    public Task<string?> TryExtractAsync(string pkgPath, CancellationToken cancellationToken)
        => Task.Run(() => TryExtract(pkgPath, cancellationToken), cancellationToken);

    private static string? TryExtract(string pkgPath, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(PathHelper.ToExtendedPath(pkgPath));
            if (!info.Exists || info.Length < 16 * 1024)
            {
                return null;
            }

            var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{PathHelper.Normalize(pkgPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24];

            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var cached = Path.Combine(AppPaths.FrameCacheDirectory, $"pkg-{cacheKey}{extension}");
                if (File.Exists(cached))
                {
                    return cached;
                }
            }

            using var stream = new FileStream(
                PathHelper.ToExtendedPath(pkgPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                ChunkSize,
                FileOptions.SequentialScan);

            var headLength = Math.Min(info.Length, HeadScanBytes);
            var found = ScanRegion(stream, 0, headLength, cancellationToken);
            if (found is not null)
            {
                return found;
            }

            if (info.Length > headLength + TailScanBytes)
            {
                found = ScanRegion(stream, info.Length - TailScanBytes, TailScanBytes, cancellationToken);
                if (found is not null)
                {
                    return found;
                }
            }

            Logger.Info($"No safe embedded preview candidate found in .pkg '{pkgPath}'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warn($".pkg preview fallback failed for '{pkgPath}'.", ex);
            return null;
        }
    }

    private static string? ScanRegion(FileStream stream, long start, long length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return null;
        }

        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[ChunkSize + 8];
        var carry = 0;
        long position = start;
        var remaining = length;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length - carry, remaining);
            var read = stream.Read(buffer, carry, toRead);
            if (read <= 0)
            {
                break;
            }

            var count = carry + read;
            var baseOffset = position - carry;

            for (var index = 0; index <= count - 8; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isJpeg = buffer[index] == 0xFF && buffer[index + 1] == 0xD8 && buffer[index + 2] == 0xFF;
                var isPng = buffer[index] == 0x89 && buffer[index + 1] == 0x50 && buffer[index + 2] == 0x4E && buffer[index + 3] == 0x47
                            && buffer[index + 4] == 0x0D && buffer[index + 5] == 0x0A && buffer[index + 6] == 0x1A && buffer[index + 7] == 0x0A;

                if (!isJpeg && !isPng)
                {
                    continue;
                }

                var absoluteOffset = baseOffset + index;
                var extracted = TryExtractImage(stream, absoluteOffset, isPng, cancellationToken);
                if (extracted is not null)
                {
                    return extracted;
                }

                stream.Seek(position + read, SeekOrigin.Begin);
            }

            carry = Math.Min(8, count);
            Array.Copy(buffer, count - carry, buffer, 0, carry);
            position += read;
            remaining -= read;
        }

        return null;
    }

    private static string? TryExtractImage(FileStream stream, long offset, bool isPng, CancellationToken cancellationToken)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            var buffer = new byte[1024 * 1024];
            var total = 0;
            var trailing = new byte[8];
            var trailingCount = 0;

            while (total < MaxImageBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
                total += read;

                // Search the newly received bytes together with the previous 8-byte tail.
                var searchBytes = new byte[trailingCount + read];
                Array.Copy(trailing, 0, searchBytes, 0, trailingCount);
                Array.Copy(buffer, 0, searchBytes, trailingCount, read);

                var endIndex = isPng ? FindPngEnd(searchBytes) : FindJpegEnd(searchBytes);
                if (endIndex >= 0)
                {
                    // searchBytes begins trailingCount bytes before the newly read block.
                    var absoluteEnd = total - read - trailingCount + endIndex;
                    var bytes = memory.ToArray();
                    var finalLength = (int)absoluteEnd;
                    if (finalLength <= 0 || finalLength > bytes.Length)
                    {
                        return null;
                    }

                    var imageBytes = new byte[finalLength];
                    Array.Copy(bytes, imageBytes, finalLength);
                    return ValidateAndSave(imageBytes, isPng);
                }

                trailingCount = Math.Min(8, searchBytes.Length);
                Array.Copy(searchBytes, searchBytes.Length - trailingCount, trailing, 0, trailingCount);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? ValidateAndSave(byte[] imageBytes, bool isPng)
    {
        try
        {
            if (!SkiaImage.TryProbe(imageBytes, out var width, out var height) || width < 960 || height < 540)
            {
                return null;
            }

            var aspect = (double)width / height;
            if (aspect < 1.2 || aspect > 2.4)
            {
                return null;
            }

            // A signature match alone is not enough: the stream has to decode
            // cleanly before it is offered as a preview.
            using (var verification = SkiaImage.DecodeOriented(imageBytes))
            {
                if (verification is null)
                {
                    return null;
                }
            }

            var extension = isPng ? ".png" : ".jpg";
            var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..24];
            var output = Path.Combine(AppPaths.FrameCacheDirectory, $"pkg-{cacheKey}{extension}");
            File.WriteAllBytes(PathHelper.ToExtendedPath(output), imageBytes);
            Logger.Info($"Extracted a validated embedded preview ({width}x{height}) from a .pkg.");
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static int FindJpegEnd(byte[] bytes)
    {
        for (var index = 0; index <= bytes.Length - 2; index++)
        {
            if (bytes[index] == 0xFF && bytes[index + 1] == 0xD9)
            {
                return index + 2;
            }
        }

        return -1;
    }

    private static int FindPngEnd(byte[] bytes)
    {
        ReadOnlySpan<byte> marker = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82];
        var span = bytes.AsSpan();
        var index = span.IndexOf(marker);
        return index < 0 ? -1 : index + marker.Length;
    }
}
