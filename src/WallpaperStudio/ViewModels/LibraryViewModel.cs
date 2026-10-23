using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

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
    private readonly DialogService _dialogs;
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
        DialogService dialogs,
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
        _dialogs = dialogs;
        _shell = shell;

        Wallpapers = [];
        WallpaperView = CollectionViewSource.GetDefaultView(Wallpapers);
        WallpaperView.Filter = FilterItem;
        WallpaperView.SortDescriptions.Add(new SortDescription(nameof(WallpaperSource.Title), ListSortDirection.Ascending));

        _scanCommand = new AsyncRelayCommand(ScanAsync, () => !string.IsNullOrWhiteSpace(Settings.SourceFolder));
        _scanCommand.ExecutionFailed += (_, ex) => _dialogs.ShowError("Scan failed", ex.Message, ex);

        BrowseSourceCommand = new RelayCommand(BrowseSource);
        ScanCommand = _scanCommand;
        ExportAllCommand = new AsyncRelayCommand(ExportAllAsync, HasExportCandidates);
        ((AsyncRelayCommand)ExportAllCommand).ExecutionFailed += (_, ex) => _dialogs.ShowError("Export failed", ex.Message, ex);
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
    }

    public ObservableCollection<WallpaperSource> Wallpapers { get; }

    public ICollectionView WallpaperView { get; }

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
                WallpaperView.Refresh();
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
                WallpaperView.Refresh();
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
        WallpaperView.Refresh();
    }

    private void BrowseSource()
    {
        var chosen = _dialogs.PickFolder("Choose the folder that contains your wallpapers", Settings.SourceFolder);
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
            _dialogs.ShowError("Could not save settings", "The source folder was selected but could not be saved.", ex);
        }

        RefreshSettingsBindings();
        _shell.ReportText($"Source folder set to {chosen}. Scan when ready.");
    }

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SourceFolder))
        {
            _dialogs.ShowInfo("Choose a folder", "Choose a source folder before scanning.");
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
            WallpaperView.Refresh();
            LastScanSummary = $"{result.Items.Count:N0} sources detected · {DuplicatesSkipped:N0} duplicates ignored · {result.TotalFiles:N0} files scanned.";
            _shell.Complete(LastScanSummary);

            foreach (var item in Wallpapers)
            {
                _ = LoadThumbnailAsync(item);
            }
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
            _dialogs.ShowError("Scan failed", ex.Message, ex);
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

        var matches = record.Matches(source.FileHash, source.LastModified);
        if (!matches)
        {
            if (record.ExportStatus == ExportStatus.Exported)
            {
                source.ErrorMessage = "The source changed since it was last exported. Review/export it again.";
            }

            if (source.RequiresApproval)
            {
                source.ApprovalStatus = ApprovalStatus.Pending;
                source.CandidateFrameTime = Settings.DefaultFrameTimestampSeconds;
            }

            return;
        }

        source.CandidateFrameTime = record.FrameTimestampSeconds > 0
            ? record.FrameTimestampSeconds
            : Settings.DefaultFrameTimestampSeconds;
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
            _dialogs.ShowInfo("Nothing to export", "There are no approved wallpapers ready to export. Review video/scene items first, or use the per-item button.");
            return;
        }

        var token = _shell.Begin($"Exporting {candidates.Count:N0} wallpaper(s)…");
        var exported = 0;
        var failed = 0;
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
                _dialogs.ShowError("Export memory not saved", "JPEGs were written, but state.json could not be updated.", ex);
            }

            UpdateCounts();
            WallpaperView.Refresh();
            _shell.Complete(failed == 0
                ? $"Exported {exported:N0} wallpaper(s)."
                : $"Exported {exported:N0}; {failed:N0} failed. See the log for details.");

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
            _dialogs.ShowError("Export failed", ex.Message, ex);
        }
        finally
        {
            if (cancelled)
            {
                UpdateCounts();
                WallpaperView.Refresh();
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
            _dialogs.ShowInfo("Unsupported source", item.ErrorMessage ?? "This source cannot be converted safely. Choose an image manually in the review screen.");
            return;
        }

        var token = _shell.Begin($"Exporting {item.Title}…");
        try
        {
            await EnsureCandidateAsync(item, token);
            var result = await _exports.ExportAsync(item, Settings, token);
            UpdateCounts();
            WallpaperView.Refresh();
            _shell.Complete($"Exported {Path.GetFileName(result.Path)}.");
        }
        catch (OperationCanceledException)
        {
            _shell.Complete("Export cancelled.");
        }
        catch (Exception ex)
        {
            item.Status = SourceStatus.Error;
            item.ErrorMessage = ex.Message;
            _shell.ResetOperation();
            _dialogs.ShowError("Export failed", ex.Message, ex);
        }
    }

    private async Task EnsureCandidateAsync(WallpaperSource item, CancellationToken token)
    {
        if (item.Kind == WallpaperKind.ImageWallpaper)
        {
            item.CandidateFramePath = item.PrimaryFilePath;
            return;
        }

        if (item.Kind == WallpaperKind.VideoWallpaper)
        {
            if (item.HasCandidate)
            {
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
            return;
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

    private async Task LoadThumbnailAsync(WallpaperSource item)
    {
        try
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
        WallpaperView.Refresh();
        _shell.ReportText("Library cleared. Scan again when ready.");
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
