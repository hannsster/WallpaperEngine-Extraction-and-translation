using WallpaperStudio.Services;

namespace WallpaperStudio.Services;

public sealed record ScanProgress(string Phase, string Message, double Fraction);

public sealed class ScanResult
{
    public List<WallpaperSource> Items { get; } = [];
    public List<string> Warnings { get; } = [];
    public int TotalFiles { get; set; }
    public int ProjectCount { get; set; }
    public int UnsupportedCount { get; set; }
}

/// <summary>
/// Recursively detects Wallpaper Engine projects, scene packages, loose videos
/// and loose images. Detection is conservative: a project.json is parsed only
/// for its documented top-level hints, and a package whose assets cannot be read
/// is marked unsupported rather than guessed at.
/// </summary>
public sealed class SourceScanner
{
    private readonly ImageProbeService _images;
    private readonly FfmpegService _ffmpeg;
    private readonly SceneFrameExtractor _scenes;

    public SourceScanner(ImageProbeService images, FfmpegService ffmpeg, SceneFrameExtractor scenes)
    {
        _images = images;
        _ffmpeg = ffmpeg;
        _scenes = scenes;
    }

    public async Task<ScanResult> ScanAsync(
        string root,
        AppSettings settings,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(PathHelper.ToExtendedPath(root)))
        {
            throw new DirectoryNotFoundException($"The source folder was not found: {root}");
        }

        root = PathHelper.Normalize(root);
        var result = new ScanResult();
        var excluded = BuildExclusions(root, settings);

        progress?.Report(new ScanProgress("Enumerating", "Walking the source folder…", 0));
        var files = await Task.Run(
            () => EnumerateFiles(root, excluded, result, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        result.TotalFiles = files.Count;
        progress?.Report(new ScanProgress("Detecting", $"{files.Count:N0} files found. Reading Wallpaper Engine projects…", 8));

        var projectRoots = files
            .Where(path => string.Equals(Path.GetFileName(path), MediaFileTypes.ProjectFileName, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathHelper.Normalize(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectDirectories = new HashSet<string>(projectRoots, StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(4, 4);
        var projectIndex = 0;

        foreach (var projectDirectory in projectRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectIndex++;
            var fraction = 8 + (projectRoots.Count == 0 ? 0 : 40d * projectIndex / projectRoots.Count);
            progress?.Report(new ScanProgress("Detecting", $"Project {projectIndex:N0} of {projectRoots.Count:N0}", fraction));

            var source = await BuildProjectSourceAsync(projectDirectory, settings, gate, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                continue;
            }

            result.ProjectCount++;
            AddSource(source, result, addedPaths);
            consumed.Add(source.PrimaryFilePath);
            if (source.ProjectFilePath is not null)
            {
                consumed.Add(source.ProjectFilePath);
            }

            if (source.PreviewPath is not null)
            {
                consumed.Add(source.PreviewPath);
            }
        }

        progress?.Report(new ScanProgress("Detecting", "Looking for loose videos, images and .pkg scenes…", 50));

        var looseCandidates = files
            .Where(path => !consumed.Contains(path))
            .Where(path => !IsInsideProjectFolder(path, projectDirectories))
            .Where(path => MediaFileTypes.IsVideo(path) || MediaFileTypes.IsPkg(path) || MediaFileTypes.IsImage(path))
            .OrderBy(path => MediaFileTypes.IsPkg(path) ? 0 : MediaFileTypes.IsVideo(path) ? 1 : 2)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var looseIndex = 0;
        foreach (var path in looseCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            looseIndex++;
            if (looseIndex % 10 == 0 || looseIndex == looseCandidates.Count)
            {
                var fraction = 50 + (looseCandidates.Count == 0 ? 45 : 45d * looseIndex / looseCandidates.Count);
                progress?.Report(new ScanProgress("Detecting", $"Loose source {looseIndex:N0} of {looseCandidates.Count:N0}", fraction));
            }

            if (MediaFileTypes.IsImage(path) && PreviewResolver.IsPreviewName(path))
            {
                // A preview belongs to a package in the same folder; it is not a wallpaper by itself.
                var siblingPackage = Directory.EnumerateFiles(
                        PathHelper.ToExtendedPath(Path.GetDirectoryName(path)!),
                        "*" + MediaFileTypes.PkgExtension,
                        SearchOption.TopDirectoryOnly)
                    .Any();
                if (siblingPackage)
                {
                    consumed.Add(path);
                    continue;
                }
            }

            var source = MediaFileTypes.IsPkg(path)
                ? await BuildPkgSourceAsync(path, settings, cancellationToken).ConfigureAwait(false)
                : MediaFileTypes.IsVideo(path)
                    ? await BuildLooseVideoAsync(path, gate, cancellationToken).ConfigureAwait(false)
                    : await BuildLooseImageAsync(path, cancellationToken).ConfigureAwait(false);

            if (source is null)
            {
                continue;
            }

            AddSource(source, result, addedPaths);
            consumed.Add(source.PrimaryFilePath);
            if (source.PreviewPath is not null)
            {
                consumed.Add(source.PreviewPath);
            }
        }

        if (result.Warnings.Count > 100)
        {
            result.Warnings.RemoveRange(100, result.Warnings.Count - 100);
            result.Warnings.Add("Additional warnings were logged to the local log file.");
        }

        result.UnsupportedCount = result.Items.Count(item => item.Kind == WallpaperKind.Unknown && item.CandidateFramePath is null);
        progress?.Report(new ScanProgress("Complete", $"{result.Items.Count:N0} wallpaper sources detected.", 100));
        Logger.Info($"Scan complete: {result.Items.Count} sources, {result.TotalFiles} files, {result.ProjectCount} project.json folders.");
        return result;
    }

    private async Task<WallpaperSource?> BuildProjectSourceAsync(string directory, AppSettings settings, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, MediaFileTypes.ProjectFileName);
        ProjectManifestReader.TryRead(manifestPath, out var manifest, out var warning);
        if (warning is not null)
        {
            Logger.Warn($"Manifest warning for '{manifestPath}': {warning}");
        }

        var topLevel = SafeEnumerateTopLevel(directory);
        var preview = ResolveManifestFile(directory, manifest.Preview)
                      ?? PreviewResolver.FindPreviewImage(directory);

        var manifestMedia = ResolveManifestFile(directory, manifest.File);
        var video = manifestMedia is not null && MediaFileTypes.IsVideo(manifestMedia)
            ? manifestMedia
            : topLevel.FirstOrDefault(MediaFileTypes.IsVideo);
        var pkg = manifestMedia is not null && MediaFileTypes.IsPkg(manifestMedia)
            ? manifestMedia
            : topLevel.FirstOrDefault(MediaFileTypes.IsPkg);
        var image = manifestMedia is not null && MediaFileTypes.IsExportImage(manifestMedia)
            ? manifestMedia
            : PreviewResolver.FindFirstMedia(directory, MediaFileTypes.IsExportImage);

        var declaredType = manifest.Type?.Trim().ToLowerInvariant();
        WallpaperKind kind;
        string? primary;

        switch (declaredType)
        {
            case "video":
                primary = video;
                kind = video is not null ? WallpaperKind.VideoWallpaper : WallpaperKind.Unknown;
                break;
            case "scene":
                primary = pkg ?? video ?? image ?? preview ?? manifestPath;
                kind = WallpaperKind.SceneProject;
                break;
            case "web":
            case "application":
                primary = image ?? preview ?? manifestPath;
                kind = WallpaperKind.Unknown;
                break;
            default:
                if (video is not null)
                {
                    primary = video;
                    kind = WallpaperKind.VideoWallpaper;
                }
                else if (pkg is not null)
                {
                    primary = pkg;
                    kind = WallpaperKind.SceneProject;
                }
                else if (image is not null)
                {
                    primary = image;
                    kind = WallpaperKind.ImageWallpaper;
                }
                else if (preview is not null)
                {
                    primary = preview;
                    kind = WallpaperKind.Unknown;
                }
                else
                {
                    primary = manifestPath;
                    kind = WallpaperKind.Unknown;
                }

                break;
        }

        if (primary is null)
        {
            primary = preview ?? manifestPath;
        }

        var title = string.IsNullOrWhiteSpace(manifest.Title)
            ? new DirectoryInfo(PathHelper.ToExtendedPath(directory)).Name
            : manifest.Title!;

        var source = new WallpaperSource
        {
            SourcePath = directory,
            PrimaryFilePath = primary,
            ProjectFilePath = manifestPath,
            PreviewPath = preview,
            Title = title,
            Kind = kind,
            IsWorkshop = PreviewResolver.IsWallpaperEngineWorkshopPath(directory),
            FileSize = SafeLength(primary),
            LastModified = MaxModified(manifestPath, primary, preview)
        };

        await PopulateMediaAsync(source, gate, cancellationToken).ConfigureAwait(false);

        if (kind == WallpaperKind.SceneProject)
        {
            await PopulateSceneAsync(source, preview, settings, cancellationToken).ConfigureAwait(false);
        }
        else if (kind == WallpaperKind.VideoWallpaper)
        {
            source.ApprovalStatus = ApprovalStatus.Pending;
            source.Status = SourceStatus.NeedsReview;
        }
        else if (kind == WallpaperKind.Unknown)
        {
            source.ApprovalStatus = ApprovalStatus.Pending;
            if (preview is not null)
            {
                source.CandidateFramePath = preview;
                source.UsesPreviewFallback = true;
                source.Status = SourceStatus.NeedsReview;
                source.ErrorMessage = "This project is not an image, video or scene. Only its preview/fallback image is offered.";
            }
            else
            {
                source.Status = SourceStatus.Unsupported;
                source.ErrorMessage = "This project type cannot be safely turned into a static image, and no preview asset was found.";
            }
        }
        else
        {
            source.ApprovalStatus = ApprovalStatus.NotRequired;
            source.Status = SourceStatus.Ready;
            source.CandidateFramePath = primary;
        }

        return source;
    }

    private async Task<WallpaperSource?> BuildLooseVideoAsync(string path, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        var title = Path.GetFileNameWithoutExtension(path);
        var source = new WallpaperSource
        {
            SourcePath = path,
            PrimaryFilePath = path,
            Title = title,
            Kind = WallpaperKind.VideoWallpaper,
            IsWorkshop = PreviewResolver.IsWallpaperEngineWorkshopPath(path),
            FileSize = SafeLength(path),
            LastModified = MaxModified(path),
            ApprovalStatus = ApprovalStatus.Pending,
            Status = SourceStatus.NeedsReview
        };

        await PopulateMediaAsync(source, gate, cancellationToken).ConfigureAwait(false);
        return source;
    }

    private async Task<WallpaperSource?> BuildPkgSourceAsync(string path, AppSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var preview = PreviewResolver.FindPreviewImage(directory);
        var source = new WallpaperSource
        {
            SourcePath = path,
            PrimaryFilePath = path,
            PreviewPath = preview,
            Title = Path.GetFileNameWithoutExtension(path),
            Kind = WallpaperKind.SceneProject,
            IsWorkshop = PreviewResolver.IsWallpaperEngineWorkshopPath(path),
            FileSize = SafeLength(path),
            LastModified = MaxModified(path, preview),
            ApprovalStatus = ApprovalStatus.Pending,
            Status = SourceStatus.NeedsReview,
            UsesPreviewFallback = false
        };

        await PopulateSceneAsync(source, preview, settings, cancellationToken).ConfigureAwait(false);

        if (!source.Resolution.IsKnown)
        {
            source.ErrorMessage = "No resolution could be read from the package. The app can attempt a conservative embedded-preview scan when you review this source.";
        }

        return source;
    }

    /// <summary>
    /// Sets the real (full-resolution) size of a scene source with a cheap header
    /// pass and applies the auto-approval preference. The image itself is only
    /// decoded later, on review or export, so scanning 900+ scenes stays fast.
    /// </summary>
    private async Task PopulateSceneAsync(WallpaperSource source, string? preview, AppSettings settings, CancellationToken cancellationToken)
    {
        source.UsesPreviewFallback = false;

        var described = await _scenes.DescribeAsync(source, cancellationToken).ConfigureAwait(false);
        if (described.IsKnown)
        {
            source.Resolution = described;
        }
        else if (preview is not null)
        {
            source.Resolution = await _images.IdentifyAsync(preview, cancellationToken).ConfigureAwait(false);
        }

        // Auto-approve only when the real scene image resolves to at least 1080p.
        // A scene whose textures cannot be read falls back to its small preview and
        // is left for an explicit review instead of silently exporting a thumbnail.
        if (settings.AutoApproveScenes && source.Resolution.PassesMinimum1080p)
        {
            source.ApprovalStatus = ApprovalStatus.Approved;
            source.Status = SourceStatus.Approved;
        }
        else
        {
            source.ApprovalStatus = ApprovalStatus.Pending;
            source.Status = SourceStatus.NeedsReview;
        }
    }

    private async Task<WallpaperSource?> BuildLooseImageAsync(string path, CancellationToken cancellationToken)
    {
        var source = new WallpaperSource
        {
            SourcePath = path,
            PrimaryFilePath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            Kind = WallpaperKind.ImageWallpaper,
            IsWorkshop = PreviewResolver.IsWallpaperEngineWorkshopPath(path),
            FileSize = SafeLength(path),
            LastModified = MaxModified(path),
            ApprovalStatus = ApprovalStatus.NotRequired,
            Status = SourceStatus.Ready,
            CandidateFramePath = path
        };

        source.Resolution = await _images.IdentifyAsync(path, cancellationToken).ConfigureAwait(false);
        return source;
    }

    private async Task PopulateMediaAsync(WallpaperSource source, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        if (source.Kind == WallpaperKind.VideoWallpaper)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await _ffmpeg.ProbeAsync(source.PrimaryFilePath, cancellationToken).ConfigureAwait(false);
                if (info.HasResolution)
                {
                    source.SetResolution(info.Width, info.Height);
                }

                source.DurationSeconds = info.DurationSeconds;
                if (info.Error is not null)
                {
                    source.ErrorMessage = info.Error;
                }
            }
            finally
            {
                gate.Release();
            }

            return;
        }

        var probePath = source.PreviewPath ?? source.PrimaryFilePath;
        if (MediaFileTypes.IsExportImage(probePath))
        {
            source.Resolution = await _images.IdentifyAsync(probePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddSource(WallpaperSource source, ScanResult result, HashSet<string> addedPaths)
    {
        if (!addedPaths.Add(PathHelper.Normalize(source.SourcePath)))
        {
            return;
        }

        result.Items.Add(source);
    }

    private static List<string> EnumerateFiles(
        string root,
        IReadOnlyList<string> excludedRoots,
        ScanResult result,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };

        foreach (var file in Directory.EnumerateFiles(PathHelper.ToExtendedPath(root), "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeExtended(file);
            if (excludedRoots.Any(excluded => PathHelper.IsSameOrChildPath(normalized, excluded)))
            {
                continue;
            }

            files.Add(normalized);
            if (files.Count % 2_000 == 0)
            {
                progress?.Report(new ScanProgress("Enumerating", $"{files.Count:N0} files found…", 2));
            }
        }

        return files;
    }

    private static List<string> BuildExclusions(string root, AppSettings settings)
    {
        var exclusions = new List<string>();
        foreach (var candidate in new[] { AppPaths.DataDirectory, settings.ExportFolder })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = PathHelper.Normalize(candidate);
            if (!PathHelper.PathsEqual(normalized, root) && PathHelper.IsSameOrChildPath(normalized, root))
            {
                exclusions.Add(normalized);
            }
        }

        return exclusions;
    }

    private static bool IsInsideProjectFolder(string path, HashSet<string> projectDirectories)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (projectDirectories.Contains(directory))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = parent;
        }

        return false;
    }

    private static string? ResolveManifestFile(string directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        try
        {
            var combined = Path.GetFullPath(Path.Combine(directory, relative));
            if (!PathHelper.IsSameOrChildPath(combined, directory))
            {
                return null;
            }

            return File.Exists(PathHelper.ToExtendedPath(combined)) ? combined : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> SafeEnumerateTopLevel(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(PathHelper.ToExtendedPath(directory), "*", SearchOption.TopDirectoryOnly)
                .Select(NormalizeExtended)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not enumerate project folder '{directory}'.", ex);
            return [];
        }
    }

    private static DateTimeOffset MaxModified(params string?[] paths)
    {
        var latest = DateTimeOffset.MinValue;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var value = File.GetLastWriteTimeUtc(PathHelper.ToExtendedPath(path));
                if (value > latest)
                {
                    latest = value;
                }
            }
            catch
            {
                // Ignore missing/unreadable files here; they surface later with a clear error.
            }
        }

        return latest == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : latest;
    }

    private static long SafeLength(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        try
        {
            return new FileInfo(PathHelper.ToExtendedPath(path)).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizeExtended(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
}
