namespace WallpaperStudio.Services;

public sealed class ImportService
{
    private readonly ImageProbeService _images;

    public ImportService(ImageProbeService images)
    {
        _images = images;
    }

    public async Task<List<ImportItem>> LoadImagesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var items = new List<ImportItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = PathHelper.Normalize(path);
            if (!seen.Add(normalized) || !File.Exists(PathHelper.ToExtendedPath(normalized)))
            {
                continue;
            }

            if (!MediaFileTypes.IsExportImage(normalized))
            {
                Logger.Warn($"Import skipped an unsupported image format: '{normalized}'.");
                continue;
            }

            progress?.Report($"Reading {Path.GetFileName(normalized)}…");
            var resolution = await _images.IdentifyAsync(normalized, cancellationToken).ConfigureAwait(false);
            long size = 0;
            try
            {
                size = new FileInfo(PathHelper.ToExtendedPath(normalized)).Length;
            }
            catch
            {
                // Size is decorative; ignore.
            }

            if (!resolution.IsKnown)
            {
                items.Add(new ImportItem
                {
                    FilePath = normalized,
                    FileName = Path.GetFileName(normalized),
                    Resolution = resolution,
                    FileSize = size,
                    PassesRule = false,
                    Decision = ImportDecision.Failed,
                    Message = "The image could not be read. It may be corrupt or use an unsupported codec."
                });
                continue;
            }

            var meetsRule = ResolutionRules.Passes(resolution.Width, resolution.Height);
            var item = new ImportItem
            {
                FilePath = normalized,
                FileName = Path.GetFileName(normalized),
                Resolution = resolution,
                FileSize = size,
                PassesRule = meetsRule,
                Decision = meetsRule ? ImportDecision.Approved : ImportDecision.Pending,
                Message = meetsRule ? null : "Below the 1080p rule; needs your approval."
            };

            items.Add(item);
        }

        Logger.Info($"Import queue loaded {items.Count} image(s).");
        return items;
    }

    public async Task<ImportStageResult> StageAsync(
        IReadOnlyList<ImportItem> items,
        AppSettings settings,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var approved = items.Where(item => item.CanStage).ToList();
        if (approved.Count == 0)
        {
            throw new InvalidOperationException("There are no approved images to stage.");
        }

        var exportRoot = string.IsNullOrWhiteSpace(settings.ExportFolder)
            ? AppPaths.DefaultExportDirectory
            : settings.ExportFolder;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var staging = Path.Combine(exportRoot, "Wallpaper Engine Imports", $"Import_{stamp}");
        Directory.CreateDirectory(PathHelper.ToExtendedPath(staging));

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var item in approved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Copying {item.FileName}…");

            var baseName = PathHelper.SanitizeFileName(Path.GetFileNameWithoutExtension(item.FileName));
            var extension = Path.GetExtension(item.FileName);
            var name = baseName + extension;
            var counter = 2;
            while (!usedNames.Add(name))
            {
                name = $"{baseName} ({counter++}){extension}";
            }

            var destination = Path.Combine(staging, name);
            try
            {
                await Task.Run(() => File.Copy(
                    PathHelper.ToExtendedPath(item.FilePath),
                    PathHelper.ToExtendedPath(destination),
                    overwrite: false), cancellationToken).ConfigureAwait(false);
                item.Decision = ImportDecision.Staged;
                item.Message = destination;
                copied++;
            }
            catch (Exception ex)
            {
                item.Decision = ImportDecision.Failed;
                item.Message = "Copy failed: " + ex.Message;
                Logger.Error($"Could not stage '{item.FilePath}'.", ex);
            }
        }

        var instructions = Path.Combine(staging, "IMPORT-INSTRUCTIONS.txt");
        await File.WriteAllTextAsync(PathHelper.ToExtendedPath(instructions), BuildInstructions(copied), cancellationToken)
            .ConfigureAwait(false);

        Logger.Info($"Staged {copied} approved image(s) in '{staging}'.");
        return new ImportStageResult(staging, instructions, copied);
    }

    public static string BuildInstructions(int count)
    {
        return $"""
               Wallpaper Studio - manual import helper
               ======================================

               This folder contains {count} image file(s) you approved for personal use.

               Wallpaper Engine does not provide a documented command-line import API
               or a supported Workshop publishing API. Wallpaper Studio therefore does
               not modify Wallpaper Engine's files, inject content, or publish anything.
               This is a staging folder only.

               To import an image yourself:
               1. Open Wallpaper Engine.
               2. Use its Create Wallpaper / Editor action (wording depends on version).
               3. Choose the image file from this folder, give it a name, and save.
               4. Use it as your desktop wallpaper as usual.
               5. For a tablet, copy the exported JPEG to the tablet with a cable,
                  cloud drive, or local transfer method you trust.

               Personal use only. Do not upload or publish content you do not have
               the rights to use.
               """;
    }
}

public sealed record ImportStageResult(string StagingFolder, string InstructionsFile, int CopiedCount);
