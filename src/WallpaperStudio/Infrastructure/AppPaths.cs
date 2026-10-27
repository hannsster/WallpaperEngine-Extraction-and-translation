namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Resolves portable data locations. The app first tries to keep settings, state,
/// logs and cache beside the executable. If that directory is read-only (common
/// when dropped into Program Files), it falls back to %LOCALAPPDATA%.
/// </summary>
public static class AppPaths
{
    private static bool _initialized;

    public static string ExecutableDirectory { get; private set; } = AppContext.BaseDirectory;
    public static string DataDirectory { get; private set; } = string.Empty;
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbs");
    public static string FrameCacheDirectory => Path.Combine(CacheDirectory, "frames");
    public static string ReverseSearchCacheDirectory => Path.Combine(CacheDirectory, "reverse");
    public static string ReverseSearchThumbnailDirectory => Path.Combine(ReverseSearchCacheDirectory, "thumbs");
    public static string ReverseSearchQueryDirectory => Path.Combine(ReverseSearchCacheDirectory, "queries");
    public static string ReverseSearchUsageFile => Path.Combine(ReverseSearchCacheDirectory, "usage.json");
    public static string PublishDirectory => Path.Combine(DataDirectory, "publish");
    public static string DefaultExportDirectory { get; private set; } = string.Empty;
    public static bool PortableMode { get; private set; }

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ExecutableDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var portableData = Path.Combine(ExecutableDirectory, "WallpaperStudioData");

        if (TryPrepareWritableDirectory(portableData))
        {
            DataDirectory = portableData;
            PortableMode = true;
            DefaultExportDirectory = Path.Combine(ExecutableDirectory, "Exports");
        }
        else
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WallpaperStudio");
            Directory.CreateDirectory(DataDirectory);
            PortableMode = false;
            DefaultExportDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Wallpaper Studio Exports");
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
        Directory.CreateDirectory(FrameCacheDirectory);
        Directory.CreateDirectory(ReverseSearchCacheDirectory);
        Directory.CreateDirectory(ReverseSearchThumbnailDirectory);
        Directory.CreateDirectory(ReverseSearchQueryDirectory);
        Directory.CreateDirectory(PublishDirectory);
    }

    private static bool TryPrepareWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
