namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Resolves the data locations used for settings, state, logs, cache and drafts.
/// Data lives in the per-user folder (%LOCALAPPDATA%\WallpaperStudio on Windows,
/// XDG data home on Linux) so it survives installing a new version in a fresh
/// folder. Dropping a <c>portable.txt</c> file beside the executable opts into
/// keeping everything next to the app instead. Data left in the old
/// <c>WallpaperStudioData</c> folder beside a previous executable is imported
/// once, automatically.
/// </summary>
public static class AppPaths
{
    private const string PortableMarkerName = "portable.txt";
    private const string LegacyDataFolderName = "WallpaperStudioData";
    private const string MigratedMarkerName = "migrated-to-user-data.txt";

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

    /// <summary>The old beside-the-executable folder whose data was imported, if any.</summary>
    public static string? MigratedFrom { get; private set; }

    /// <summary>Names of the files/folders that were copied from <see cref="MigratedFrom"/>.</summary>
    public static IReadOnlyList<string> MigratedItems { get; private set; } = [];

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ExecutableDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var legacyData = Path.Combine(ExecutableDirectory, LegacyDataFolderName);
        var portableMarker = Path.Combine(ExecutableDirectory, PortableMarkerName);

        if (File.Exists(portableMarker) && TryPrepareWritableDirectory(legacyData))
        {
            DataDirectory = legacyData;
            PortableMode = true;
            DefaultExportDirectory = Path.Combine(ExecutableDirectory, "Exports");
        }
        else
        {
            DataDirectory = ResolveUserDataDirectory();
            Directory.CreateDirectory(DataDirectory);
            PortableMode = false;
            DefaultExportDirectory = ResolvePicturesDirectory();
            ImportLegacyData(ExecutableDirectory, DataDirectory);
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

    private static string ResolveUserDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WallpaperStudio");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "WallpaperStudio");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "WallpaperStudio");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "WallpaperStudio");
    }

    private static string ResolvePicturesDirectory()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            pictures = string.IsNullOrWhiteSpace(home) ? AppContext.BaseDirectory : Path.Combine(home, "Pictures");
        }

        return Path.Combine(pictures, "Wallpaper Studio Exports");
    }

    /// <summary>
    /// Imports settings, state, publish drafts and the cache from the old
    /// beside-the-executable folder the first time a user runs a version that
    /// stores data in the user profile. Both the current folder and folders
    /// extracted next to it are considered, because a new release is often
    /// unpacked into a sibling directory. The old folder is left in place and
    /// marked so a later run cannot re-import stale data over newer choices.
    /// </summary>
    private static void ImportLegacyData(string executableDirectory, string target)
    {
        try
        {
            if (File.Exists(Path.Combine(target, "settings.json")) ||
                File.Exists(Path.Combine(target, "state.json")))
            {
                return;
            }

            foreach (var candidate in FindLegacyCandidates(executableDirectory))
            {
                if (File.Exists(Path.Combine(candidate, MigratedMarkerName)))
                {
                    continue;
                }

                if (ImportFrom(candidate, target))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not import previous data.", ex);
        }
    }

    private static IEnumerable<string> FindLegacyCandidates(string executableDirectory)
    {
        var candidates = new List<(string Path, DateTime Stamp)>();

        void Consider(string directory)
        {
            var settings = Path.Combine(directory, "settings.json");
            var state = Path.Combine(directory, "state.json");
            var stamp = DateTime.MinValue;
            var found = false;
            foreach (var file in new[] { settings, state })
            {
                if (File.Exists(file))
                {
                    found = true;
                    var stampUtc = File.GetLastWriteTimeUtc(file);
                    if (stampUtc > stamp)
                    {
                        stamp = stampUtc;
                    }
                }
            }

            if (found)
            {
                candidates.Add((directory, stamp));
            }
        }

        Consider(Path.Combine(executableDirectory, LegacyDataFolderName));

        var parent = Directory.GetParent(executableDirectory)?.FullName;
        if (parent is not null)
        {
            try
            {
                // From the usual download/extract folder, look one level down into
                // sibling installs such as "WallpaperStudio-win-x64 (2)".
                foreach (var sibling in Directory.EnumerateDirectories(parent))
                {
                    if (string.Equals(sibling, executableDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Consider(Path.Combine(sibling, LegacyDataFolderName));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not look for a previous install beside '{parent}': {ex.Message}");
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Stamp)
            .Select(candidate => candidate.Path);
    }

    private static bool ImportFrom(string legacyData, string target)
    {
        try
        {
            var legacySettings = Path.Combine(legacyData, "settings.json");
            var legacyState = Path.Combine(legacyData, "state.json");
            if (!File.Exists(legacySettings) && !File.Exists(legacyState))
            {
                return false;
            }

            var imported = new List<string>();
            if (CopyFileIfExists(legacySettings, Path.Combine(target, "settings.json")))
            {
                imported.Add("settings.json");
            }

            if (CopyFileIfExists(legacyState, Path.Combine(target, "state.json")))
            {
                imported.Add("state.json");
            }

            if (CopyDirectory(Path.Combine(legacyData, "publish"), Path.Combine(target, "publish")))
            {
                imported.Add("publish");
            }

            // Cache folders can be large; moving them on the same volume is instant.
            // If that is not possible they are simply rebuilt on demand.
            if (MoveDirectory(Path.Combine(legacyData, "cache"), Path.Combine(target, "cache")))
            {
                imported.Add("cache");
            }

            if (imported.Count == 0)
            {
                return false;
            }

            MigratedFrom = legacyData;
            MigratedItems = imported;
            File.WriteAllText(
                Path.Combine(legacyData, MigratedMarkerName),
                "Wallpaper Studio now stores settings and state in the user profile:" + Environment.NewLine +
                target + Environment.NewLine +
                "Imported: " + string.Join(", ", imported) + Environment.NewLine +
                "Delete this file to import this folder again the next time the app starts." + Environment.NewLine);
            Logger.Info($"Imported previous data from '{legacyData}' ({string.Join(", ", imported)}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not import previous data from '{legacyData}'.", ex);
            return false;
        }
    }

    private static bool CopyFileIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return true;
    }

    private static bool CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return false;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        return true;
    }

    private static bool MoveDirectory(string source, string destination)
    {
        if (!Directory.Exists(source) || Directory.Exists(destination))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not move the previous cache from '{source}'; it will be rebuilt.", ex);
            return false;
        }
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
