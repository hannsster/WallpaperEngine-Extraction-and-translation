namespace WallpaperStudio.Services;

public sealed class ExportException : Exception
{
    public ExportException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed record ExportResult(string Path, ResolutionInfo Resolution, string? Warning);

public sealed class ExportService
{
    private readonly StateStore _state;

    public ExportService(StateStore state)
    {
        _state = state;
    }

    public async Task<ExportResult> ExportAsync(
        WallpaperSource item,
        AppSettings settings,
        CancellationToken cancellationToken,
        bool saveState = true)
    {
        if (item.RequiresApproval && item.ApprovalStatus != ApprovalStatus.Approved && item.Status != SourceStatus.Exported)
        {
            throw new ExportException("This source needs an approved frame before it can be exported.");
        }

        var sourcePath = !string.IsNullOrWhiteSpace(item.CandidateFramePath) && File.Exists(PathHelper.ToExtendedPath(item.CandidateFramePath))
            ? item.CandidateFramePath!
            : item.Kind == WallpaperKind.ImageWallpaper
                ? item.PrimaryFilePath
                : null;

        if (sourcePath is null)
        {
            throw new ExportException(item.Kind == WallpaperKind.VideoWallpaper
                ? "Generate a video frame and approve it before exporting."
                : "No approved frame image is available for this source.");
        }

        var exportFolder = string.IsNullOrWhiteSpace(settings.ExportFolder)
            ? AppPaths.DefaultExportDirectory
            : settings.ExportFolder;

        var result = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(PathHelper.ToExtendedPath(exportFolder));
                var title = PathHelper.SanitizeFileName(string.IsNullOrWhiteSpace(item.Title)
                    ? Path.GetFileNameWithoutExtension(item.PrimaryFilePath)
                    : item.Title);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var desired = Path.Combine(exportFolder, $"{title}_{stamp}.jpg");
                var finalPath = PathHelper.MakeUniquePath(desired);
                var tempPath = finalPath + ".tmp";

                try
                {
                    using var image = SkiaImage.DecodeOriented(sourcePath);
                    if (image is null)
                    {
                        throw new ExportException("The selected frame is not a supported image format.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var resolution = new ResolutionInfo(image.Width, image.Height);
                    SkiaImage.SaveJpeg(image, tempPath, settings.JpegQuality);

                    File.Move(PathHelper.ToExtendedPath(tempPath), PathHelper.ToExtendedPath(finalPath), overwrite: false);
                    return new ExportResult(finalPath, resolution, null);
                }
                finally
                {
                    TryDelete(tempPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ExportException("The export folder is not writable. Choose another folder in Settings.", ex);
            }
            catch (PathTooLongException ex)
            {
                throw new ExportException("The output path is too long. Choose a shorter export folder or title.", ex);
            }
            catch (IOException ex)
            {
                throw new ExportException("The image could not be written. It may be locked, the disk may be full, or the file may be corrupt.", ex);
            }
            catch (Exception ex) when (ex is not ExportException)
            {
                throw new ExportException("The JPEG export failed unexpectedly. Technical details were written to the log.", ex);
            }
        }, cancellationToken).ConfigureAwait(false);

        item.ExportPath = result.Path;
        item.ExportedDate = DateTimeOffset.Now;
        item.ExportStatus = ExportStatus.Exported;
        item.Status = SourceStatus.Exported;
        item.Resolution = result.Resolution;
        item.ErrorMessage = null;

        string? warning = null;
        try
        {
            _state.RecordExport(item, save: saveState);
        }
        catch (Exception ex)
        {
            Logger.Error("Export succeeded but state could not be saved.", ex);
            warning = "The JPEG was exported, but the library memory could not be updated. The next scan may offer it again.";
        }

        Logger.Info($"Exported '{item.SourcePath}' -> '{result.Path}' ({result.Resolution.Display}, quality {settings.JpegQuality}).");
        return result with { Warning = warning };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup of a temp file.
        }
    }
}
