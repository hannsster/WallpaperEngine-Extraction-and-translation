using System.Collections.ObjectModel;
using System.Windows.Input;
using WallpaperStudio.Services.ReverseSearch;

namespace WallpaperStudio.ViewModels;

public sealed class LibraryViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly StateStore _state;
    private readonly SourceScanner _scanner;
    private readonly HashingService _hashing;
    private readonly ThumbnailService _thumbnails;
    private readonly ExportService _exports;
    private readonly FfmpegService _ffmpeg;
    private readonly PkgPreviewExtractor _pkgPreviews;
    private readonly SceneFrameExtractor _sceneFrames;
    private readonly ReverseSearchService _reverse;
    private readonly IDialogService _dialogs;
    private readonly ShellState _shell;
    private readonly AsyncRelayCommand _scanCommand;
    private string _filter = "all";
    private string _searchText = string.Empty;
    private bool _forceReExport;
    private int _duplicatesSkipped;
    private string _lastScanSummary = "No scan yet.";
    private WallpaperSource? _selected;

    public LibraryViewModel(
        SettingsService settingsService,
        StateStore state,
        SourceScanner scanner,
        HashingService hashing,
        ThumbnailService thumbnails,
        ExportService exports,
        FfmpegService ffmpeg,
        PkgPreviewExtractor pkgPreviews,
        SceneFrameExtractor sceneFrames,
        ReverseSearchService reverse,
        IDialogService dialogs,
        ShellState shell)
    {
        _settingsService = settingsService;
        _state = state;
        _scanner = scanner;
        _hashing = hashing;
        _thumbnails = thumbnails;
        _exports = exports;
        _ffmpeg = ffmpeg;
        _pkgPreviews = pkgPreviews;
        _sceneFrames = sceneFrames;
        _reverse = reverse;
        _dialogs = dialogs;
        _shell = shell;

        Wallpapers = [];
        WallpaperView = new ReadOnlyObservableCollection<WallpaperSource>(_wallpaperView);

        _scanCommand = new AsyncRelayCommand(ScanAsync, () => !string.IsNullOrWhiteSpace(Settings.SourceFolder));
        _scanCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Scan failed", ex.Message, ex);

        BrowseSourceCommand = new AsyncRelayCommand(BrowseSourceAsync);
        ScanCommand = _scanCommand;
        ExportAllCommand = new AsyncRelayCommand(ExportAllAsync, HasExportCandidates);
        ((AsyncRelayCommand)ExportAllCommand).ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Export failed", ex.Message, ex);
        ExportItemCommand = new RelayCommand<WallpaperSource>(item => _ = ExportItemSafeAsync(item));
        ReviewItemCommand = new RelayCommand<WallpaperSource>(item => RequestReview(item));
        OpenExportFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(Settings.ExportFolder));
        OpenSourceFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(Settings.SourceFolder));
        OpenExportedFileCommand = new RelayCommand<WallpaperSource>(item =>
        {
            if (item is not null && !string.IsNullOrWhiteSpace(item.ExportPath))
            {
                _dialogs.ShowFileInExplorer(item.ExportPath);
            }
        });
        ClearCommand = new RelayCommand(Clear, () => Wallpapers.Count > 0);
        SelectAllVisibleCommand = new RelayCommand(() => { });
        SetFilterCommand = new RelayCommand<string>(SetFilter);
        FindOriginalCommand = new RelayCommand<WallpaperSource>(item =>
        {
            if (item is null)
            {
                return;
            }

            if (!Settings.ReverseSearchEnabled || !Settings.ReverseSearchConsent)
            {
                _shell.ReportText("Review this frame to turn on reverse image search and find the original artwork.");
            }

            RequestReview(item);
        });
    }

    public ObservableCollection<WallpaperSource> Wallpapers { get; }

    private readonly ObservableCollection<WallpaperSource> _wallpaperView = [];

    /// <summary>The filtered, title-sorted list the library grid binds to.</summary>
    public ReadOnlyObservableCollection<WallpaperSource> WallpaperView { get; }

    public AppSettings Settings => _settingsService.Current;

    public ICommand BrowseSourceCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ExportAllCommand { get; }
    public ICommand ExportItemCommand { get; }
    public ICommand ReviewItemCommand { get; }
    public ICommand OpenExportFolderCommand { get; }
    public ICommand OpenSourceFolderCommand { get; }
    public ICommand OpenExportedFileCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SelectAllVisibleCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand FindOriginalCommand { get; }

    public string SourceFolder => Settings.SourceFolder;

    public string SourceFolderDisplay => string.IsNullOrWhiteSpace(SourceFolder) ? "No source folder selected" : SourceFolder;

    public string ExportFolderDisplay => Settings.ExportFolder;

    public string? StateWarning => _state.LoadWarning;

    public event EventHandler<WallpaperSource>? ReviewRequested;

    public string Filter
    {
        get => _filter;
        private set
        {
            if (SetProperty(ref _filter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll), nameof(IsFilterReady), nameof(IsFilterReview), nameof(IsFilterExported));
                RefreshWallpaperView();
            }
        }
    }

    public bool IsFilterAll => Filter == "all";
    public bool IsFilterReady => Filter == "ready";
    public bool IsFilterReview => Filter == "review";
    public bool IsFilterExported => Filter == "exported";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshWallpaperView();
            }
        }
    }

    public bool ForceReExport
    {
        get => _forceReExport;
        set
        {
            if (SetProperty(ref _forceReExport, value) && ExportAllCommand is AsyncRelayCommand exportAll)
            {
                exportAll.RaiseCanExecuteChanged();
            }
        }
    }

    public int DuplicatesSkipped
    {
        get => _duplicatesSkipped;
        private set => SetProperty(ref _duplicatesSkipped, value);
    }

    public string LastScanSummary
    {
        get => _lastScanSummary;
        private set => SetProperty(ref _lastScanSummary, value);
    }

    public WallpaperSource? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public int DetectedCount => Wallpapers.Count;
    public int NeedsReviewCount => Wallpapers.Count(item => item.NeedsReview || item.Status == SourceStatus.Unsupported);
    public int ExportedCount => Wallpapers.Count(item => item.IsExported);
    public int UnsupportedCount => Wallpapers.Count(item => item.Status == SourceStatus.Unsupported);
    public int ReadyCount => Wallpapers.Count(item => item.Status is SourceStatus.Ready or SourceStatus.Approved);
    public string CountsText => $"{DetectedCount:N0} sources · {ExportedCount:N0} exported · {NeedsReviewCount:N0} need attention";

    public void SetFilter(string? filter)
    {
        Filter = string.IsNullOrWhiteSpace(filter) ? "all" : filter;
    }

    public void RefreshSettingsBindings()
    {
        OnPropertyChanged(nameof(Settings), nameof(SourceFolder), nameof(SourceFolderDisplay), nameof(ExportFolderDisplay));
        _scanCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Re-evaluates chips after the approval screen changed an item.</summary>
    public void RefreshAfterReview()
    {
        UpdateCounts();
        RefreshWallpaperView();
    }

    private async Task BrowseSourceAsync()
    {
        var chosen = await _dialogs.PickFolderAsync("Choose the folder that contains your wallpapers", Settings.SourceFolder);
        if (chosen is null)
        {
            return;
        }

        Settings.SourceFolder = chosen;
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not save settings", "The source folder was selected but could not be saved.", ex);
        }

        RefreshSettingsBindings();
        _shell.ReportText($"Source folder set to {chosen}. Scan when ready.");
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SourceFolder))
        {
            await _dialogs.ShowInfoAsync("Choose a folder", "Choose a source folder before scanning.");
            return;
        }

        var token = _shell.Begin("Scanning wallpapers…");
        try
        {
            Wallpapers.Clear();
            DuplicatesSkipped = 0;
            UpdateCounts();
            var progress = new Progress<ScanProgress>(value =>
            {
                _shell.Report(value.Fraction / 100 * 0.55, value.Message);
            });

            var result = await _scanner.ScanAsync(Settings.SourceFolder, Settings, progress, token);
            token.ThrowIfCancellationRequested();

            var records = new List<(WallpaperSource Source, Models.ExportRecord? Record)>();
            for (var index = 0; index < result.Items.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var source = result.Items[index];
                var record = _state.Get(source.SourcePath);
                _shell.Report(0.55 + 0.35 * ((double)index / Math.Max(1, result.Items.Count)),
                    $"Fingerprinting {index + 1:N0} of {result.Items.Count:N0}…");

                try
                {
                    if (record is not null &&
                        !string.IsNullOrWhiteSpace(record.FileHash) &&
                        record.LastModified == source.LastModified)
                    {
                        source.FileHash = record.FileHash;
                    }
                    else
                    {
                        source.FileHash = await _hashing.ComputeSourceHashAsync(source, null, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    source.ErrorMessage = "Hashing failed; the file may be locked or unreadable.";
                    Logger.Error($"Hash failed for '{source.SourcePath}'.", ex);
                }

                records.Add((source, record));
            }

            foreach (var (source, record) in records)
            {
                ApplyState(source, record);
            }

            var deduped = Deduplicate(records.Select(pair => pair.Source));
            DuplicatesSkipped = result.Items.Count - deduped.Count;
            foreach (var source in deduped.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                Wallpapers.Add(source);
            }

            foreach (var warning in result.Warnings)
            {
                Logger.Warn($"Scan warning: {warning}");
            }

            UpdateCounts();
            RefreshWallpaperView();
            LastScanSummary = $"{result.Items.Count:N0} sources detected · {DuplicatesSkipped:N0} duplicates ignored · {result.TotalFiles:N0} files scanned.";
            _shell.Complete(LastScanSummary);

            foreach (var item in Wallpapers)
            {
                _ = LoadThumbnailAsync(item);
            }

            _ = RestoreReverseResultsAsync();
        }
        catch (OperationCanceledException)
        {
            _shell.Complete("Scan cancelled. Partial results were discarded.");
            Logger.Info("Scan cancelled by user.");
        }
        catch (Exception ex)
        {
            _shell.ResetOperation();
            _shell.ReportText("Scan failed. See the error dialog and log.");
            await _dialogs.ShowErrorAsync("Scan failed", ex.Message, ex);
        }
    }

    private static List<WallpaperSource> Deduplicate(IEnumerable<WallpaperSource> sources)
    {
        var kept = new List<WallpaperSource>();
        foreach (var group in sources.GroupBy(source => source.FileHash, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                kept.AddRange(group);
                continue;
            }

            var best = group
                .OrderByDescending(item => item.IsExported)
                .ThenByDescending(item => item.ProjectFilePath is not null)
                .ThenBy(item => item.SourcePath.Length)
                .First();
            kept.Add(best);
        }

        return kept
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ApplyState(WallpaperSource source, Models.ExportRecord? record)
    {
        if (record is null)
        {
            if (source.Kind == WallpaperKind.ImageWallpaper)
            {
                source.ApprovalStatus = ApprovalStatus.NotRequired;
                source.Status = SourceStatus.Ready;
            }

            return;
        }

        source.SceneComposition = record.SceneComposition;

        var matches = record.Matches(source.FileHash, source.LastModified);
        if (matches &&
            record.ExportStatus == ExportStatus.Exported &&
            source.Resolution.IsKnown &&
            !string.IsNullOrWhiteSpace(record.Resolution) &&
            !string.Equals(record.Resolution, source.Resolution.Display, StringComparison.Ordinal))
        {
            // The same source now yields a different resolution (for example a scene
            // that used its small preview before full-resolution extraction existed).
            // Treat it as stale so a re-export captures the real image.
            matches = false;
            source.ErrorMessage = $"This source now yields {source.Resolution.Display} (previously {record.Resolution}). Export it again for full resolution.";
        }

        if (!matches)
        {
            if (record.ExportStatus == ExportStatus.Exported)
            {
                source.ErrorMessage ??= "The source changed since it was last exported. Review/export it again.";
            }

            // Keep an explicit approval (for example an auto-approved scene) so a
            // stale-but-still-valid source is simply re-exported at the new size.
            if (source.RequiresApproval && source.ApprovalStatus != ApprovalStatus.Approved)
            {
                source.ApprovalStatus = ApprovalStatus.Pending;
                source.CandidateFrameTime = Settings.DefaultFrameTimestampSeconds;
            }

            return;
        }

        // Only frame-based sources need the default timestamp; a loose image has no
        // timeline, and a stray default there would change its reverse-search key.
        source.CandidateFrameTime = source.RequiresApproval
            ? (record.FrameTimestampSeconds > 0 ? record.FrameTimestampSeconds : Settings.DefaultFrameTimestampSeconds)
            : record.FrameTimestampSeconds;
        source.UsesPreviewFallback = record.UsesPreviewFallback;

        if (source.RequiresApproval && record.ApprovalStatus is ApprovalStatus.Approved or ApprovalStatus.Rejected)
        {
            source.ApprovalStatus = record.ApprovalStatus;
        }

        if (record.ExportStatus == ExportStatus.Exported && !string.IsNullOrWhiteSpace(record.ExportPath))
        {
            if (File.Exists(PathHelper.ToExtendedPath(record.ExportPath)))
            {
                source.ExportPath = record.ExportPath;
                source.ExportedDate = record.ExportedDate;
                source.ExportStatus = ExportStatus.Exported;
                source.Status = SourceStatus.Exported;
                return;
            }

            source.ErrorMessage = "The previous JPEG is missing. Export it again.";
        }

        source.ExportStatus = ExportStatus.NotExported;
        if (source.RequiresApproval)
        {
            source.Status = source.ApprovalStatus switch
            {
                ApprovalStatus.Approved => SourceStatus.Approved,
                ApprovalStatus.Rejected => SourceStatus.Rejected,
                _ => SourceStatus.NeedsReview
            };
        }
        else
        {
            source.Status = SourceStatus.Ready;
        }
    }

    private bool HasExportCandidates() => Wallpapers.Any(IsExportCandidate);

    private bool IsExportCandidate(WallpaperSource item) =>
        item.CanExportNow && (ForceReExport || !Settings.SkipAlreadyExported || !item.IsExported);

    private async Task ExportAllAsync()
    {
        var candidates = Wallpapers
            .Where(IsExportCandidate)
            .ToList();

        if (candidates.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Nothing to export", "There are no approved wallpapers ready to export. Review video/scene items first, or use the per-item button.");
            return;
        }

        var token = _shell.Begin($"Exporting {candidates.Count:N0} wallpaper(s)…");
        var exported = 0;
        var failed = 0;
        var skipped = 0;
        var cancelled = false;

        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var item = candidates[index];
                _shell.Report((double)index / candidates.Count, $"Exporting {item.Title} ({index + 1}/{candidates.Count})…");

                try
                {
                    await EnsureCandidateAsync(item, token);
                    var result = await _exports.ExportAsync(item, Settings, token, saveState: false);
                    exported++;
                    if (result.Warning is not null)
                    {
                        _shell.ReportText(result.Warning);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ExportException ex)
                {
                    // A source that needs a manual decision (an all-black frame or a
                    // low-resolution fallback) is skipped, not counted as a failure.
                    skipped++;
                    item.ErrorMessage = ex.Message;
                    if (item.Status != SourceStatus.Error)
                    {
                        item.Status = SourceStatus.NeedsReview;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    item.Status = SourceStatus.Error;
                    item.ErrorMessage = ex.Message;
                    Logger.Error($"Export failed for '{item.SourcePath}'.", ex);
                }
            }

            try
            {
                _state.Save();
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync("Export memory not saved", "JPEGs were written, but state.json could not be updated.", ex);
            }

            UpdateCounts();
            RefreshWallpaperView();
            var summary = $"Exported {exported:N0} wallpaper(s).";
            if (skipped > 0)
            {
                summary += $" {skipped:N0} need review.";
            }

            if (failed > 0)
            {
                summary += $" {failed:N0} failed. See the log for details.";
            }

            _shell.Complete(summary);

            if (Settings.OpenExportFolderAfterBatch && exported > 0)
            {
                _dialogs.OpenFolder(Settings.ExportFolder);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _shell.Complete($"Export cancelled after {exported:N0} file(s).");
        }
        catch (Exception ex)
        {
            _shell.ResetOperation();
            await _dialogs.ShowErrorAsync("Export failed", ex.Message, ex);
        }
        finally
        {
            if (cancelled)
            {
                UpdateCounts();
                RefreshWallpaperView();
            }
        }
    }

    private async Task ExportItemSafeAsync(WallpaperSource? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.RequiresApproval && item.ApprovalStatus != ApprovalStatus.Approved && item.Status != SourceStatus.Exported)
        {
            RequestReview(item);
            _shell.ReportText($"{item.Title} needs frame approval before export.");
            return;
        }

        if (item.Status == SourceStatus.Unsupported && item.CandidateFramePath is null)
        {
            await _dialogs.ShowInfoAsync("Unsupported source", item.ErrorMessage ?? "This source cannot be converted safely. Choose an image manually in the review screen.");
            return;
        }

        var token = _shell.Begin($"Exporting {item.Title}…");
        try
        {
            await EnsureCandidateAsync(item, token);
            var result = await _exports.ExportAsync(item, Settings, token);
            UpdateCounts();
            RefreshWallpaperView();
            _shell.Complete($"Exported {Path.GetFileName(result.Path)}.");
        }
        catch (OperationCanceledException)
        {
            _shell.Complete("Export cancelled.");
        }
        catch (ExportException ex)
        {
            item.ErrorMessage = ex.Message;
            _shell.ResetOperation();
            await _dialogs.ShowInfoAsync("This source needs review", ex.Message);
            RequestReview(item);
        }
        catch (Exception ex)
        {
            item.Status = SourceStatus.Error;
            item.ErrorMessage = ex.Message;
            _shell.ResetOperation();
            await _dialogs.ShowErrorAsync("Export failed", ex.Message, ex);
        }
    }

    private async Task EnsureCandidateAsync(WallpaperSource item, CancellationToken token)
    {
        if (item.Kind == WallpaperKind.ImageWallpaper)
        {
            item.CandidateFramePath = item.PrimaryFilePath;
            item.UsesPreviewFallback = false;
            StartAutoSearch(item);
            return;
        }

        if (item.Kind == WallpaperKind.VideoWallpaper)
        {
            if (item.HasCandidate)
            {
                StartAutoSearch(item);
                return;
            }

            var time = item.CandidateFrameTime;
            if (item.DurationSeconds > 0)
            {
                time = Math.Clamp(time, 0, Math.Max(0, item.DurationSeconds - 0.1));
            }

            var output = Path.Combine(AppPaths.FrameCacheDirectory,
                $"{(string.IsNullOrWhiteSpace(item.FileHash) ? "video" : item.FileHash[..Math.Min(16, item.FileHash.Length)])}-{time:0.###}.png");
            var result = await _ffmpeg.ExtractFrameAsync(item.PrimaryFilePath, time, output, token);
            item.CandidateFramePath = result.Path;
            item.CandidateFrameTime = result.TimeSeconds;
            item.UsesPreviewFallback = false;

            await TryImproveBlankFrameAsync(item, token);
            StartAutoSearch(item);
            return;
        }

        if (item.Kind == WallpaperKind.SceneProject)
        {
            // Reuse an existing frame once it is extracted, or once the user has
            // explicitly approved a fallback image.
            if (item.HasCandidate && (!item.UsesPreviewFallback || item.ApprovalStatus == ApprovalStatus.Approved))
            {
                return;
            }

            var extracted = await _sceneFrames.ExtractAsync(item, token);
            if (extracted is not null)
            {
                item.CandidateFramePath = extracted.Path;
                item.UsesPreviewFallback = false;
                if (extracted.Resolution.IsKnown)
                {
                    item.Resolution = extracted.Resolution;
                }

                if (extracted.IsAllBlack)
                {
                    item.ApprovalStatus = ApprovalStatus.Pending;
                    item.Status = SourceStatus.NeedsReview;
                    item.ErrorMessage = "The extracted scene image is almost entirely black, so it needs a manual look before it can be exported.";
                    throw new ExportException(item.ErrorMessage);
                }

                StartAutoSearch(item);
                return;
            }

            // No scene image could be extracted: fall back to the preview, but a
            // low-resolution fallback always needs explicit approval.
            if (!string.IsNullOrWhiteSpace(item.PreviewPath) &&
                File.Exists(PathHelper.ToExtendedPath(item.PreviewPath)))
            {
                item.CandidateFramePath = item.PreviewPath;
                item.UsesPreviewFallback = true;
                if (item.ApprovalStatus != ApprovalStatus.Approved)
                {
                    item.ApprovalStatus = ApprovalStatus.Pending;
                    item.Status = SourceStatus.NeedsReview;
                    throw new ExportException(
                        "Full-resolution extraction was not possible for this scene, so only its low-resolution preview is available. Review and approve it if you still want to export it.");
                }

                return;
            }
        }

        if (item.HasCandidate)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.PreviewPath))
        {
            item.CandidateFramePath = item.PreviewPath;
            item.UsesPreviewFallback = true;
            return;
        }

        if (MediaFileTypes.IsPkg(item.PrimaryFilePath))
        {
            var extracted = await _pkgPreviews.TryExtractAsync(item.PrimaryFilePath, token);
            if (extracted is not null)
            {
                item.PreviewPath = extracted;
                item.CandidateFramePath = extracted;
                item.UsesPreviewFallback = true;
                item.Resolution = await _thumbnails.Probe.IdentifyAsync(extracted, token);
                return;
            }
        }

        throw new ExportException("No preview or approved image is available. Open Review and choose an image.");
    }

    /// <summary>
    /// A blank opening second is common (fade-ins, logos, black scene start).
    /// When the requested frame is weak, sample the video once and take the
    /// clearest frame instead of silently exporting something unusable.
    /// </summary>
    private async Task TryImproveBlankFrameAsync(WallpaperSource item, CancellationToken token)
    {
        if (!_ffmpeg.Toolchain.HasFfmpeg || item.DurationSeconds < 4 || !item.HasCandidate)
        {
            return;
        }

        var originalTime = item.CandidateFrameTime;
        var path = item.CandidateFramePath!;
        var quality = await Task.Run(() => FrameAnalyzer.AnalyzeFile(path), token);
        if (!quality.IsWeak)
        {
            return;
        }

        try
        {
            var samples = await _ffmpeg.ExtractFilmstripAsync(
                item.PrimaryFilePath, item.DurationSeconds, 10, item.FileHash, token);
            token.ThrowIfCancellationRequested();

            var best = samples
                .Where(sample => !sample.IsWeak && !sample.IsAllBlack)
                .OrderByDescending(sample => sample.Score)
                .FirstOrDefault();

            if (best is null || best.Score < quality.Score + 4)
            {
                item.CandidateNote = "This frame has very little visible detail. Open Review to pick a different moment.";
                return;
            }

            var stem = string.IsNullOrWhiteSpace(item.FileHash)
                ? "video"
                : item.FileHash[..Math.Min(16, item.FileHash.Length)];
            var output = Path.Combine(AppPaths.FrameCacheDirectory, $"{stem}-auto-{best.TimeSeconds:0.###}.png");
            var better = await _ffmpeg.ExtractFrameAsync(item.PrimaryFilePath, best.TimeSeconds, output, token);
            item.CandidateFramePath = better.Path;
            item.CandidateFrameTime = better.TimeSeconds;
            item.CandidateNote =
                $"The frame at {FormatTime(originalTime)} was mostly blank, so a clearer frame at {FormatTime(better.TimeSeconds)} was picked automatically.";
            Logger.Info($"Auto-picked frame {FormatTime(better.TimeSeconds)} for '{item.Title}' (blank frame at {FormatTime(originalTime)}).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Filmstrip sampling failed for '{item.Title}': {ex.Message}");
            item.CandidateNote = "This frame has very little visible detail. Open Review to pick a different moment.";
        }
    }

    private void StartAutoSearch(WallpaperSource item)
    {
        if (!Settings.ReverseSearchEnabled ||
            !Settings.ReverseSearchConsent ||
            !Settings.ReverseSearchAutomatic)
        {
            return;
        }

        if (item.UsesPreviewFallback || item.IsReverseSearching || !item.HasCandidate)
        {
            return;
        }

        _ = AutoSearchAsync(item);
    }

    /// <summary>
    /// Applies cached reverse-search results after a scan so a restart does not
    /// lose the chips and cards for sources that were already searched.
    /// </summary>
    private async Task RestoreReverseResultsAsync()
    {
        if (!Settings.ReverseSearchEnabled || !Settings.ReverseSearchConsent)
        {
            return;
        }

        foreach (var item in Wallpapers)
        {
            if (item.Kind != WallpaperKind.ImageWallpaper ||
                item.IsReverseSearching ||
                item.ReverseStatus != ReverseSearchStatus.Idle)
            {
                continue;
            }

            try
            {
                var snapshot = await _reverse.TryGetCachedAsync(item);
                if (snapshot is not null)
                {
                    await UiThread.RunAsync(() => item.ApplyReverseSearch(snapshot));
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Reverse cache restore failed for '{item.Title}': {ex.Message}");
            }
        }
    }

    private async Task AutoSearchAsync(WallpaperSource item)
    {
        item.IsReverseSearching = true;
        try
        {
            var snapshot = await _reverse.SearchAsync(item, automatic: true, ignoreCache: false, CancellationToken.None);
            if (snapshot is not null)
            {
                await UiThread.RunAsync(() => item.ApplyReverseSearch(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing or the scan was replaced; nothing to report.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Automatic reverse search failed for '{item.Title}'.", ex);
        }
        finally
        {
            await UiThread.RunAsync(() => item.IsReverseSearching = false);
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private async Task LoadThumbnailAsync(WallpaperSource item)
    {        try
        {
            var path = item.HasCandidate
                ? item.CandidateFramePath
                : item.PreviewPath
                  ?? (item.Kind == WallpaperKind.ImageWallpaper ? item.PrimaryFilePath : null);
            if (path is null)
            {
                return;
            }

            item.IsThumbnailLoading = true;
            var image = await _thumbnails.GetAsync(path, 480, "library");
            if (image is not null)
            {
                item.Thumbnail = image;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Library thumbnail failed for '{item.Title}': {ex.Message}");
        }
        finally
        {
            item.IsThumbnailLoading = false;
        }
    }

    private void RequestReview(WallpaperSource? item)
    {
        if (item is null)
        {
            return;
        }

        Selected = item;
        ReviewRequested?.Invoke(this, item);
    }

    private void Clear()
    {
        Wallpapers.Clear();
        DuplicatesSkipped = 0;
        LastScanSummary = "Library cleared.";
        UpdateCounts();
        RefreshWallpaperView();
        _shell.ReportText("Library cleared. Scan again when ready.");
    }

    private void RefreshWallpaperView()
    {
        var items = Wallpapers
            .Where(FilterItem)
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _wallpaperView.Clear();
        foreach (var item in items)
        {
            _wallpaperView.Add(item);
        }
    }

    private bool FilterItem(object obj)
    {
        if (obj is not WallpaperSource item)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            if (!item.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) &&
                !item.SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return Filter switch
        {
            "review" => item.NeedsReview || item.Status == SourceStatus.Unsupported,
            "ready" => item.Status is SourceStatus.Ready or SourceStatus.Approved,
            "exported" => item.IsExported,
            "unsupported" => item.Status == SourceStatus.Unsupported,
            _ => true
        };
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(DetectedCount), nameof(NeedsReviewCount), nameof(ExportedCount),
            nameof(UnsupportedCount), nameof(ReadyCount), nameof(CountsText));
        if (ClearCommand is RelayCommand clear)
        {
            clear.RaiseCanExecuteChanged();
        }

        if (ExportAllCommand is AsyncRelayCommand exportAll)
        {
            exportAll.RaiseCanExecuteChanged();
        }
    }
}
