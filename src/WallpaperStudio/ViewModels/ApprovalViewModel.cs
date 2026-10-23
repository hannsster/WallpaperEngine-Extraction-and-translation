using System.Windows.Media;
using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

/// <summary>
/// Drives the review/approval screen. A video frame is extracted on demand and
/// never exported until the user explicitly approves it. Scene projects use the
/// project preview as a clearly-labelled fallback because no safe offline scene
/// renderer exists in this app.
/// </summary>
public sealed class ApprovalViewModel : ObservableObject
{
    private readonly FfmpegService _ffmpeg;
    private readonly ThumbnailService _thumbnails;
    private readonly PkgPreviewExtractor _pkgPreviews;
    private readonly StateStore _state;
    private readonly SettingsService _settingsService;
    private readonly DialogService _dialogs;
    private readonly ShellState _shell;
    private readonly AsyncRelayCommand _approveCommand;
    private readonly AsyncRelayCommand _regenerateCommand;
    private readonly AsyncRelayCommand _scanEmbeddedCommand;

    private WallpaperSource? _item;
    private ImageSource? _previewImage;
    private string? _previewPath;
    private bool _isBusy;
    private bool _isExtracting;
    private string _busyMessage = string.Empty;
    private double _frameTimestamp;
    private string? _notice;
    private CancellationTokenSource? _loadCts;

    public ApprovalViewModel(
        FfmpegService ffmpeg,
        ThumbnailService thumbnails,
        PkgPreviewExtractor pkgPreviews,
        StateStore state,
        SettingsService settingsService,
        DialogService dialogs,
        ShellState shell)
    {
        _ffmpeg = ffmpeg;
        _thumbnails = thumbnails;
        _pkgPreviews = pkgPreviews;
        _state = state;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _shell = shell;

        _approveCommand = new AsyncRelayCommand(ApproveAsync, () => Item is not null && HasPreview && !IsBusy);
        _approveCommand.ExecutionFailed += (_, ex) => _dialogs.ShowError("Could not approve", ex.Message, ex);

        _regenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => Item?.Kind == WallpaperKind.VideoWallpaper && HasFfmpeg && !IsBusy);
        _regenerateCommand.ExecutionFailed += (_, ex) => _dialogs.ShowError("Frame extraction failed", ex.Message, ex);

        _scanEmbeddedCommand = new AsyncRelayCommand(ScanEmbeddedAsync, () => Item is not null && Item.Kind == WallpaperKind.SceneProject && !HasPreview && !IsBusy);
        _scanEmbeddedCommand.ExecutionFailed += (_, ex) => _dialogs.ShowError("Preview scan failed", ex.Message, ex);

        ApproveCommand = _approveCommand;
        RegenerateCommand = _regenerateCommand;
        ScanEmbeddedCommand = _scanEmbeddedCommand;
        TossCommand = new RelayCommand(Toss, () => Item is not null);
        ChooseImageCommand = new AsyncRelayCommand(ChooseImageAsync, () => Item is not null && !IsBusy);
        OpenFileLocationCommand = new RelayCommand(OpenFileLocation, () => Item is not null);
        OpenExportFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(EffectiveExportFolder));
        BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? BackRequested;

    public event EventHandler<ApprovalOutcome>? ReviewCompleted;

    public WallpaperSource? Item
    {
        get => _item;
        private set
        {
            if (SetProperty(ref _item, value))
            {
                OnPropertyChanged(
                    nameof(HasItem), nameof(Title), nameof(SourcePath), nameof(KindGlyph), nameof(SourceTypeLabel),
                    nameof(ResolutionDetail), nameof(MetadataSummary), nameof(IsVideo), nameof(IsScene), nameof(HasFfmpeg),
                    nameof(DurationSeconds), nameof(MaxTimestamp), nameof(CanScrub), nameof(FallbackNotice), nameof(HasFallbackNotice),
                    nameof(CandidateSourceLabel), nameof(ApprovalStatusLabel), nameof(IsAlreadyApproved));
                RaiseCommandStates();
            }
        }
    }

    public bool HasItem => Item is not null;

    public string Title => Item?.Title ?? "Nothing selected";

    public string SourcePath => Item?.SourcePath ?? string.Empty;

    public string KindGlyph => Item?.KindGlyph ?? "\uE8B9";

    public string SourceTypeLabel => Item?.SourceTypeLabel ?? string.Empty;

    public string ResolutionDetail => Item?.ResolutionDetail ?? string.Empty;

    public string MetadataSummary => Item?.MetadataSummary ?? string.Empty;

    public bool IsVideo => Item?.Kind == WallpaperKind.VideoWallpaper;

    public bool IsScene => Item?.Kind == WallpaperKind.SceneProject;

    public bool HasFfmpeg => _ffmpeg.Toolchain.HasFfmpeg;

    public double DurationSeconds => Item?.DurationSeconds ?? 0;

    public bool CanScrub => IsVideo && DurationSeconds > 0.5;

    public string FallbackNotice => Item?.PreviewFallbackNotice ?? string.Empty;

    public bool HasFallbackNotice => Item?.UsesPreviewFallback == true;

    public string CandidateSourceLabel => Item?.CandidateSourceLabel ?? string.Empty;

    public bool IsAlreadyApproved => Item?.ApprovalStatus == ApprovalStatus.Approved;

    public string ApprovalStatusLabel => Item?.ApprovalStatus switch
    {
        ApprovalStatus.Approved => "Approved",
        ApprovalStatus.Rejected => "Tossed",
        ApprovalStatus.Pending => "Awaiting your decision",
        ApprovalStatus.NotRequired => "Approval not required",
        _ => string.Empty
    };

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                RaiseCommandStates();
            }
        }
    }

    public string? PreviewPath
    {
        get => _previewPath;
        private set
        {
            if (SetProperty(ref _previewPath, value))
            {
                OnPropertyChanged(nameof(HasPreview), nameof(PreviewPathDisplay));
            }
        }
    }

    public string PreviewPathDisplay => string.IsNullOrWhiteSpace(PreviewPath) ? string.Empty : PathHelper.Ellipsize(PreviewPath, 110);

    public bool HasPreview => PreviewImage is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool IsExtracting
    {
        get => _isExtracting;
        private set => SetProperty(ref _isExtracting, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public double FrameTimestamp
    {
        get => _frameTimestamp;
        set
        {
            var clamped = Math.Max(0, value);
            if (SetProperty(ref _frameTimestamp, clamped))
            {
                OnPropertyChanged(nameof(FrameTimestampText));
            }
        }
    }

    public string FrameTimestampText => FormatTime(FrameTimestamp);

    public double MaxTimestamp => DurationSeconds > 1 ? DurationSeconds - 0.1 : 0;

    public string EffectiveExportFolder => string.IsNullOrWhiteSpace(_settingsService.Current.ExportFolder)
        ? AppPaths.DefaultExportDirectory
        : _settingsService.Current.ExportFolder;

    /// <summary>Called after settings change so FFmpeg availability is reflected immediately.</summary>
    public void RefreshToolchainState()
    {
        OnPropertyChanged(nameof(HasFfmpeg), nameof(CanScrub), nameof(EffectiveExportFolder));
        RaiseCommandStates();
    }

    public ICommand ApproveCommand { get; }
    public ICommand TossCommand { get; }
    public ICommand RegenerateCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand ScanEmbeddedCommand { get; }
    public ICommand OpenFileLocationCommand { get; }
    public ICommand OpenExportFolderCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Loads a source for review, reusing an existing candidate when possible.</summary>
    public Task LoadAsync(WallpaperSource item)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        Item = item;
        Notice = null;
        FrameTimestamp = item.CandidateFrameTime > 0
            ? item.CandidateFrameTime
            : _settingsService.Current.DefaultFrameTimestampSeconds;

        if (item.DurationSeconds > 0)
        {
            FrameTimestamp = Math.Clamp(FrameTimestamp, 0, Math.Max(0, item.DurationSeconds - 0.1));
        }

        return LoadPreviewAsync(item, token, autoExtract: true);
    }

    private async Task LoadPreviewAsync(WallpaperSource item, CancellationToken token, bool autoExtract)
    {
        var existing = item.HasCandidate ? item.CandidateFramePath : null;
        if (existing is not null)
        {
            await ShowPreviewAsync(existing, token);
            return;
        }

        // Scene/project sources may only have a preview asset.
        if (!string.IsNullOrWhiteSpace(item.PreviewPath) && File.Exists(PathHelper.ToExtendedPath(item.PreviewPath)))
        {
            item.CandidateFramePath = item.PreviewPath;
            item.UsesPreviewFallback = true;
            await ShowPreviewAsync(item.PreviewPath, token);
            return;
        }

        if (item.Kind == WallpaperKind.VideoWallpaper && autoExtract)
        {
            await ExtractFrameAsync(item, FrameTimestamp, token, showErrors: false);
            return;
        }

        PreviewImage = null;
        PreviewPath = null;
        Notice = item.Kind == WallpaperKind.SceneProject
            ? "No preview asset was found. Use \"Find embedded preview\" or choose an image manually."
            : "No preview is available yet. Choose an image to use as the exported frame.";
        RaiseCommandStates();
    }

    private async Task RegenerateAsync()
    {
        if (Item is null)
        {
            return;
        }

        if (!HasFfmpeg)
        {
            _dialogs.ShowWarning("FFmpeg not found",
                "FFmpeg is needed to extract video frames. Set the FFmpeg folder in Settings, or place ffmpeg.exe in an 'ffmpeg' subfolder next to the app.");
            return;
        }

        IsBusy = true;
        try
        {
            await ExtractFrameAsync(Item, FrameTimestamp, _loadCts?.Token ?? CancellationToken.None, showErrors: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExtractFrameAsync(WallpaperSource item, double time, CancellationToken token, bool showErrors)
    {
        IsExtracting = true;
        BusyMessage = $"Extracting a frame at {FormatTime(time)}…";
        IsBusy = true;
        _shell.ReportText($"Extracting video frame at {FormatTime(time)}…");
        try
        {
            var output = Path.Combine(
                AppPaths.FrameCacheDirectory,
                $"review-{(string.IsNullOrWhiteSpace(item.FileHash) ? "video" : item.FileHash[..Math.Min(16, item.FileHash.Length)])}-{time:0.###}.png");

            var result = await _ffmpeg.ExtractFrameAsync(item.PrimaryFilePath, time, output, token);
            item.CandidateFramePath = result.Path;
            item.CandidateFrameTime = result.TimeSeconds;
            item.UsesPreviewFallback = false;
            FrameTimestamp = result.TimeSeconds;

            if (result.Warning is not null)
            {
                Notice = result.Warning;
            }

            if (item.Resolution.IsKnown == false)
            {
                item.Resolution = await _thumbnails.Probe.IdentifyAsync(result.Path, token);
            }

            await ShowPreviewAsync(result.Path, token);
            _shell.ReportText($"Frame ready at {FormatTime(result.TimeSeconds)}. Review it before approving.");
        }
        catch (OperationCanceledException)
        {
            // Navigation cancels the previous load; nothing to report.
        }
        catch (Exception ex)
        {
            Notice = "The frame could not be extracted. The codec may be unsupported, or FFmpeg may be missing.";
            Logger.Error($"Frame extraction failed for '{item.SourcePath}'.", ex);
            if (showErrors)
            {
                _dialogs.ShowError("Frame extraction failed", Notice, ex);
            }
        }
        finally
        {
            IsExtracting = false;
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task ShowPreviewAsync(string path, CancellationToken token)
    {
        if (!File.Exists(PathHelper.ToExtendedPath(path)))
        {
            PreviewImage = null;
            PreviewPath = null;
            return;
        }

        IsBusy = true;
        BusyMessage = "Loading preview…";
        try
        {
            var image = await _thumbnails.GetAsync(path, 1600, "review", token);
            token.ThrowIfCancellationRequested();
            PreviewImage = image;
            PreviewPath = path;
        }
        catch (OperationCanceledException)
        {
            // Ignore: a newer review request superseded this one.
        }
        finally
        {
            BusyMessage = string.Empty;
            IsBusy = false;
        }
    }

    private Task ApproveAsync()
    {
        if (Item is null)
        {
            return Task.CompletedTask;
        }

        if (!HasPreview)
        {
            _dialogs.ShowInfo("No frame selected",
                "Generate a video frame, find a preview, or choose an image before approving this source.");
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(PreviewPath))
        {
            Item.CandidateFramePath = PreviewPath;
        }

        var changedAfterExport = Item.Status == SourceStatus.Exported;
        Item.ApprovalStatus = ApprovalStatus.Approved;
        Item.Status = SourceStatus.Approved;

        try
        {
            _state.RecordApproval(Item, clearExportStatus: changedAfterExport);
            if (changedAfterExport)
            {
                Item.ExportStatus = ExportStatus.NotExported;
                Item.ExportPath = null;
                Item.ExportedDate = null;
            }
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Approval not saved",
                "The frame is approved in this session, but state.json could not be updated.", ex);
        }

        _shell.ReportText($"{Item.Title} approved. It is ready to export.");
        ReviewCompleted?.Invoke(this, new ApprovalOutcome(Item, Approved: true));
        return Task.CompletedTask;
    }

    private void Toss()
    {
        if (Item is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Toss this wallpaper?",
                "The source stays on disk untouched. Wallpaper Studio will remember it as tossed and skip it unless you approve it later."))
        {
            return;
        }

        Item.ApprovalStatus = ApprovalStatus.Rejected;
        Item.Status = SourceStatus.Rejected;
        try
        {
            _state.RecordApproval(Item, clearExportStatus: false);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not save the tossed decision.", ex);
        }

        _shell.ReportText($"{Item.Title} was tossed.");
        ReviewCompleted?.Invoke(this, new ApprovalOutcome(Item, Approved: false));
    }

    private async Task ChooseImageAsync()
    {
        if (Item is null)
        {
            return;
        }

        var files = _dialogs.PickImages("Choose an image to use as this wallpaper's static frame", multiselect: false);
        var chosen = files.FirstOrDefault();
        if (chosen is null)
        {
            return;
        }

        Item.CandidateFramePath = chosen;
        Item.UsesPreviewFallback = Item.Kind is not WallpaperKind.ImageWallpaper;
        Item.Resolution = await _thumbnails.Probe.IdentifyAsync(chosen, _loadCts?.Token ?? CancellationToken.None);
        await ShowPreviewAsync(chosen, _loadCts?.Token ?? CancellationToken.None);
        Notice = Item.UsesPreviewFallback
            ? "A manually chosen image is being used. It will be exported exactly as approved."
            : null;
        RaiseCommandStates();
    }

    private async Task ScanEmbeddedAsync()
    {
        if (Item is null)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "Scanning the package for a safe embedded preview…";
        try
        {
            var extracted = await _pkgPreviews.TryExtractAsync(Item.PrimaryFilePath, _loadCts?.Token ?? CancellationToken.None);
            if (extracted is null)
            {
                Notice = "No safe embedded preview was found. The package may not contain a plain image, or its format is protected. Choose an image manually or mark it unsupported.";
                _dialogs.ShowInfo("No embedded preview found",
                    "Wallpaper Studio only accepts plain JPEG/PNG streams that decode cleanly. It does not unpack or reverse-engineer .pkg scene data.");
                return;
            }

            Item.PreviewPath = extracted;
            Item.CandidateFramePath = extracted;
            Item.UsesPreviewFallback = true;
            var resolution = await _thumbnails.Probe.IdentifyAsync(extracted, _loadCts?.Token ?? CancellationToken.None);
            if (resolution.IsKnown)
            {
                Item.Resolution = resolution;
            }

            await ShowPreviewAsync(extracted, _loadCts?.Token ?? CancellationToken.None);
            Notice = "Found a validated embedded preview. It is labelled as a fallback; approve it only if the image looks right.";
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            Notice = "The embedded preview scan failed. The package may be unreadable or locked.";
            _dialogs.ShowError("Preview scan failed", Notice, ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseCommandStates();
        }
    }

    private void OpenFileLocation()
    {
        if (Item is null)
        {
            return;
        }

        if (Item.Kind == WallpaperKind.ImageWallpaper || File.Exists(Item.SourcePath))
        {
            _dialogs.ShowFileInExplorer(Item.SourcePath);
        }
        else
        {
            _dialogs.OpenFolder(Item.SourcePath);
        }
    }

    private void RaiseCommandStates()
    {
        _approveCommand.RaiseCanExecuteChanged();
        _regenerateCommand.RaiseCanExecuteChanged();
        _scanEmbeddedCommand.RaiseCanExecuteChanged();
        if (TossCommand is RelayCommand toss)
        {
            toss.RaiseCanExecuteChanged();
        }

        if (ChooseImageCommand is AsyncRelayCommand choose)
        {
            choose.RaiseCanExecuteChanged();
        }

        if (OpenFileLocationCommand is RelayCommand open)
        {
            open.RaiseCanExecuteChanged();
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }
}

public sealed record ApprovalOutcome(WallpaperSource Item, bool Approved);
