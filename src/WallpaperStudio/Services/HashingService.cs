using System.Security.Cryptography;
using System.Text;

namespace WallpaperStudio.Services;

public sealed record HashProgress(int CompletedFiles, int TotalFiles, string CurrentFile, double Fraction);

public sealed class HashingService
{
    public async Task<string> ComputeSourceHashAsync(
        WallpaperSource source,
        IProgress<HashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<(string Role, string Path)>
        {
            ("primary", source.PrimaryFilePath)
        };

        if (!string.IsNullOrWhiteSpace(source.ProjectFilePath))
        {
            files.Add(("project", source.ProjectFilePath));
        }

        if (!string.IsNullOrWhiteSpace(source.PreviewPath) &&
            !PathHelper.PathsEqual(source.PreviewPath, source.PrimaryFilePath))
        {
            files.Add(("preview", source.PreviewPath));
        }

        files = files
            .Where(file => File.Exists(PathHelper.ToExtendedPath(file.Path)))
            .GroupBy(file => PathHelper.Normalize(file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (files.Count == 0)
        {
            return string.Empty;
        }

        var totalBytes = files.Sum(file => SafeLength(file.Path));
        long processed = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];

        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (role, path) = files[index];
            progress?.Report(new HashProgress(index, files.Count, Path.GetFileName(path), totalBytes == 0 ? 0 : (double)processed / totalBytes));

            hash.AppendData(Encoding.UTF8.GetBytes(role));
            hash.AppendData([0]);

            await using var stream = new FileStream(
                PathHelper.ToExtendedPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                processed += read;
                progress?.Report(new HashProgress(
                    index,
                    files.Count,
                    Path.GetFileName(path),
                    totalBytes == 0 ? 0 : Math.Clamp((double)processed / totalBytes, 0, 1)));
            }
        }

        progress?.Report(new HashProgress(files.Count, files.Count, string.Empty, 1));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(PathHelper.ToExtendedPath(path)).Length;
        }
        catch
        {
            return 0;
        }
    }
}
