namespace WallpaperStudio.Services;

public static class MediaFileTypes
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"
    };

    public static readonly HashSet<string> ExportImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".avi", ".mov", ".m4v"
    };

    public const string PkgExtension = ".pkg";
    public const string ProjectFileName = "project.json";
    public const string WallpaperEngineAppId = "431960";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsExportImage(string path) => ExportImageExtensions.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsPkg(string path) => string.Equals(Path.GetExtension(path), PkgExtension, StringComparison.OrdinalIgnoreCase);
}

public static class PreviewResolver
{
    private static readonly string[] PreferredNames =
    [
        "preview.jpg", "preview.jpeg", "preview.png", "preview.webp", "preview.gif"
    ];

    /// <summary>Finds only conservatively-named preview assets. Textures are never picked as previews.</summary>
    public static string? FindPreviewImage(string directory)
    {
        try
        {
            foreach (var preferred in PreferredNames)
            {
                var candidate = Path.Combine(directory, preferred);
                if (File.Exists(PathHelper.ToExtendedPath(candidate)))
                {
                    return candidate;
                }
            }

            if (!Directory.Exists(PathHelper.ToExtendedPath(directory)))
            {
                return null;
            }

            var loose = Directory.EnumerateFiles(PathHelper.ToExtendedPath(directory), "preview.*", SearchOption.TopDirectoryOnly)
                .Select(Unprefix)
                .FirstOrDefault(MediaFileTypes.IsExportImage);
            return loose;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Preview lookup failed for '{directory}': {ex.Message}");
            return null;
        }
    }

    public static string? FindFirstMedia(string directory, Func<string, bool> predicate)
    {
        try
        {
            return Directory.EnumerateFiles(PathHelper.ToExtendedPath(directory), "*", SearchOption.TopDirectoryOnly)
                .Select(Unprefix)
                .Where(path => !IsPreviewName(path))
                .Where(predicate)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Media lookup failed for '{directory}': {ex.Message}");
            return null;
        }
    }

    public static bool IsPreviewName(string path)
        => Path.GetFileName(path).StartsWith("preview.", StringComparison.OrdinalIgnoreCase);

    public static bool IsWallpaperEngineWorkshopPath(string path)
        => path.Contains($@"workshop\content\{MediaFileTypes.WallpaperEngineAppId}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($@"workshop/content/{MediaFileTypes.WallpaperEngineAppId}", StringComparison.OrdinalIgnoreCase);

    private static string Unprefix(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
