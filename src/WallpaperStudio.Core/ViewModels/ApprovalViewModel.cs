using System.Collections.ObjectModel;
using System.Windows.Input;
using WallpaperStudio.Services.ReverseSearch;

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
    private readonly SceneFrameExtractor _sceneFrames;
    private readonly ReverseSearchService _reverse;
    private readonly StateStore _state;
    private readonly SettingsService _settingsService;
    private readonly IDialogService _dialogs;
    private readonly ShellState _shell;
    private readonly AsyncRelayCommand _approveCommand;
    private readonly AsyncRelayCommand _regenerateCommand;
    private readonly AsyncRelayCommand _scanEmbeddedCommand;
    private readonly AsyncRelayCommand _searchOriginalCommand;
    private readonly AsyncRelayCommand _scanFramesCommand;

    private WallpaperSource? _item;
    private byte[]? _previewImage;
    private string? _previewPath;
    private bool _isBusy;
    private bool _isExtracting;
    private bool _isSampling;
    private string _busyMessage = string.Empty;
    private double _frameTimestamp;
    private string? _notice;
    private CancellationTokenSource? _loadCts;
    private SceneCompositionOption _selectedSceneComposition;
    private int _compositionVersion;
    private bool _compositionInvalidatesExport;

    public ApprovalViewModel(
        FfmpegService ffmpeg,
        ThumbnailService thumbnails,
        PkgPreviewExtractor pkgPreviews,
        SceneFrameExtractor sceneFrames,
        ReverseSearchService reverse,
        StateStore state,
        SettingsService settingsService,
        IDialogService dialogs,
        ShellState shell)
    {
        _ffmpeg = ffmpeg;
        _thumbnails = thumbnails;
        _pkgPreviews = pkgPreviews;
        _sceneFrames = sceneFrames;
        _reverse = reverse;
        _state = state;
        _settingsService = settingsService;
        _dialogs = dialogs;
        _shell = shell;

        SceneCompositionOptions =
        [
            new SceneCompositionOption(
                SceneComposition.Balanced,
                "Balanced",
                "Background plus objects at their normal size. Small props and light effects are kept."),
            new SceneCompositionOption(
                SceneComposition.Full,
                "Everything",
                "Adds every drawable layer, including stretched gradients and effect layers."),
            new SceneCompositionOption(
                SceneComposition.Background,
                "Background only",
                "Only layers that fill most of the frame. Useful when foreground props sit in the wrong place.")
        ];
        _selectedSceneComposition = SceneCompositionOptions[0];

        FrameSamples.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFrameSamples), nameof(FrameSamplesSummary));

        _approveCommand = new AsyncRelayCommand(ApproveAsync, () => Item is not null && HasPreview && !IsBusy);
        _approveCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Could not approve", ex.Message, ex);

        _regenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => Item?.Kind == WallpaperKind.VideoWallpaper && HasFfmpeg && !IsBusy);
        _regenerateCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Frame extraction failed", ex.Message, ex);

        _scanEmbeddedCommand = new AsyncRelayCommand(ScanEmbeddedAsync, () => Item is not null && Item.Kind == WallpaperKind.SceneProject && !IsBusy);
        _scanEmbeddedCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Preview scan failed", ex.Message, ex);

        _searchOriginalCommand = new AsyncRelayCommand(
            () => RunSearchAsync(automatic: false, ignoreCache: true),
            () => Item is not null && HasPreview && !IsBusy);
        _searchOriginalCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Reverse image search failed", ex.Message, ex);

        _scanFramesCommand = new AsyncRelayCommand(
            () => ScanFramesAsync(autoSelect: false),
            () => IsVideo && HasFfmpeg && DurationSeconds > 2 && !IsBusy);
        _scanFramesCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Frame sampling failed", ex.Message, ex);

        ApproveCommand = _approveCommand;
        RegenerateCommand = _regenerateCommand;
        ScanEmbeddedCommand = _scanEmbeddedCommand;
        SearchOriginalCommand = _searchOriginalCommand;
        ScanFramesCommand = _scanFramesCommand;
        EnableReverseSearchCommand = new AsyncRelayCommand(EnableReverseSearchAsync);
        OpenResultCommand = new RelayCommand<ReverseSearchResult>(OpenResult);
        UseResultCommand = new RelayCommand<ReverseSearchResult>(result => _ = UseResultAsync(result));
        PickSampleCommand = new RelayCommand<FrameSample>(sample => _ = PickSampleAsync(sample));
        TossCommand = new AsyncRelayCommand(TossAsync, () => Item is not null);
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
                var option = SceneCompositionOptions.FirstOrDefault(item => item.Value == (value?.SceneComposition ?? SceneComposition.Balanced))
                             ?? SceneCompositionOptions[0];
                if (!ReferenceEquals(option, _selectedSceneComposition))
                {
                    _selectedSceneComposition = option;
                    OnPropertyChanged(nameof(SelectedSceneComposition), nameof(SceneCompositionDescription));
                }

                _compositionInvalidatesExport = false;

                OnPropertyChanged(
                    nameof(HasItem), nameof(Title), nameof(SourcePath), nameof(KindGlyph), nameof(SourceTypeLabel),
                    nameof(ResolutionDetail), nameof(MetadataSummary), nameof(IsVideo), nameof(IsScene), nameof(HasFfmpeg),
                    nameof(DurationSeconds), nameof(DurationText), nameof(MaxTimestamp), nameof(CanScrub), nameof(FallbackNotice), nameof(HasFallbackNotice),
                    nameof(CandidateSourceLabel), nameof(ApprovalStatusLabel), nameof(IsAlreadyApproved),
                    nameof(NeedsDecision));
                FrameSamples.Clear();
                RaiseCommandStates();
                OnPropertyChanged(
                    nameof(ReverseConsentMissing), nameof(ReverseNotConfigured), nameof(ReverseReady),
                    nameof(ReverseProviderSummary), nameof(HasFrameSamples), nameof(FrameSamplesSummary));
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

    /// <summary>True for videos, scenes and fallbacks that must be approved before export.</summary>
    public bool NeedsDecision => Item?.RequiresApproval == true;

    public string ApprovalStatusLabel => Item?.ApprovalStatus switch
    {
        ApprovalStatus.Approved => "Approved",
        ApprovalStatus.Rejected => "Tossed",
        ApprovalStatus.Pending => "Awaiting your decision",
        ApprovalStatus.NotRequired => "Approval not required",
        _ => string.Empty
    };

    /// <summary>How much of a scene is baked into its static frame.</summary>
    public IReadOnlyList<SceneCompositionOption> SceneCompositionOptions { get; }

    public SceneCompositionOption SelectedSceneComposition
    {
        get => _selectedSceneComposition;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSceneComposition))
            {
                return;
            }

            _selectedSceneComposition = value;
            OnPropertyChanged(nameof(SelectedSceneComposition), nameof(SceneCompositionDescription));
            if (Item is null || Item.Kind != WallpaperKind.SceneProject)
            {
                return;
            }

            Item.SceneComposition = value.Value;
            var exportedEarlier = Item.ExportStatus == ExportStatus.Exported;
            if (exportedEarlier)
            {
                // The old JPEG was flattened with a different layer stack, so it is
                // stale from now on. Mark the source as needing a fresh export.
                Item.ExportStatus = ExportStatus.NotExported;
                Item.ExportPath = null;
                Item.ExportedDate = null;
                _compositionInvalidatesExport = true;
            }

            try
            {
                // Persist immediately, even for auto-approved scenes, so the chosen
                // composition survives restarts and future versions.
                _state.RecordApproval(Item, clearExportStatus: exportedEarlier);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save the scene composition.", ex);
            }

            _ = ApplySceneCompositionAsync();
        }
    }

    public string SceneCompositionDescription => _selectedSceneComposition.Description;

    public byte[]? PreviewImage
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

    public string DurationText => FormatTime(DurationSeconds);

    public double MaxTimestamp => DurationSeconds > 1 ? DurationSeconds - 0.1 : 0;

    public string EffectiveExportFolder => string.IsNullOrWhiteSpace(_settingsService.Current.ExportFolder)
        ? AppPaths.DefaultExportDirectory
        : _settingsService.Current.ExportFolder;

    /// <summary>Called after settings change so FFmpeg availability is reflected immediately.</summary>
    public void RefreshToolchainState()
    {
        OnPropertyChanged(nameof(HasFfmpeg), nameof(CanScrub), nameof(EffectiveExportFolder));
        RefreshReverseState();
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
    public ICommand SearchOriginalCommand { get; }
    public ICommand ScanFramesCommand { get; }
    public ICommand EnableReverseSearchCommand { get; }
    public ICommand OpenResultCommand { get; }
    public ICommand UseResultCommand { get; }
    public ICommand PickSampleCommand { get; }

    public ObservableCollection<FrameSample> FrameSamples { get; } = [];

    public bool HasFrameSamples => FrameSamples.Count > 0;

    public string FrameSamplesSummary => FrameSamples.Count == 0
        ? string.Empty
        : $"{FrameSamples.Count} frames · pick the one where the artwork is clearest";

    public bool IsSampling
    {
        get => _isSampling;
        private set
        {
            if (SetProperty(ref _isSampling, value))
            {
                OnPropertyChanged(nameof(IsNotSampling));
            }
        }
    }

    public bool IsNotSampling => !_isSampling;

    public bool ReverseConsentMissing => !_settingsService.Current.ReverseSearchConsent;

    public bool ReverseReady => _reverse.IsReady;

    public bool ReverseNotConfigured => !ReverseConsentMissing && !_reverse.IsReady;

    public string ReverseProviderSummary => _reverse.ActiveProviders.Count == 0
        ? "No provider is configured yet"
        : string.Join(" + ", _reverse.ActiveProviders);

    public string ReverseConsentText =>
        "Wallpaper Studio can upload a 1280-pixel copy of this frame to " +
        string.Join(", ", _reverse.ProviderLabels) +
        " to look for the original artwork. Only the downscaled copy is sent, and you choose whether to download a match.";

    /// <summary>Called after Settings changes so the prompts and chips update.</summary>
    public void RefreshReverseState()
    {
        OnPropertyChanged(
            nameof(ReverseConsentMissing), nameof(ReverseNotConfigured), nameof(ReverseReady),
            nameof(ReverseProviderSummary), nameof(ReverseConsentText));
        RaiseCommandStates();
    }

    /// <summary>Loads a source for review, reusing an existing candidate when possible.</summary>
    public async Task LoadAsync(WallpaperSource item)
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

        await LoadPreviewAsync(item, token, autoExtract: true);

        // A video often opens on a fade-in, logo or black frame. Detect that and
        // immediately offer a filmstrip with the best frame pre-selected.
        if (HasPreview && IsVideo && item.DurationSeconds >= 4 && !token.IsCancellationRequested)
        {
            var path = item.CandidateFramePath ?? item.PreviewPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var quality = await Task.Run(() => FrameAnalyzer.AnalyzeFile(path!), token);
                if (quality.IsWeak)
                {
                    await ScanFramesAsync(autoSelect: true, token);
                }
            }
        }

        MaybeAutoSearch();
    }

    /// <summary>Runs a search without blocking the preview if the user has opted in.</summary>
    public void MaybeAutoSearch()
    {
        var settings = _settingsService.Current;
        if (!settings.ReverseSearchEnabled || !settings.ReverseSearchConsent || !settings.ReverseSearchAutomatic)
        {
            return;
        }

        if (Item is null || !Item.HasCandidate || Item.UsesPreviewFallback || Item.IsReverseSearching)
        {
            return;
        }

        if (Item.ReverseResults.Count > 0)
        {
            return;
        }

        _ = RunSearchAsync(automatic: true, ignoreCache: false);
    }

    private async Task RunSearchAsync(bool automatic, bool ignoreCache)
    {
        var item = Item;
        if (item is null || !item.HasCandidate || item.IsReverseSearching)
        {
            return;
        }

        item.IsReverseSearching = true;
        try
        {
            var snapshot = await _reverse.SearchAsync(item, automatic, ignoreCache, _loadCts?.Token ?? CancellationToken.None);
            if (snapshot is null)
            {
                return;
            }

            item.ApplyReverseSearch(snapshot);
            if (snapshot.Status == ReverseSearchStatus.Error && snapshot.Note is not null)
            {
                Notice = snapshot.Note;
            }
            else if (snapshot.Results.Count > 0)
            {
                _shell.ReportText(snapshot.HasBetterOriginal
                    ? $"Found a higher-resolution original for {item.Title}."
                    : $"Found {snapshot.Results.Count} source match(es) for {item.Title}.");
            }
        }
        catch (OperationCanceledException)
        {
            // Navigating away cancels the review load; the search result is cached anyway.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Reverse search failed for '{item.Title}'.", ex);
            Notice = "The reverse image search failed. See the log for details.";
        }
        finally
        {
            item.IsReverseSearching = false;
        }
    }

    private async Task EnableReverseSearchAsync()
    {
        var settings = _settingsService.Current;
        if (!await _dialogs.ConfirmAsync("Turn on reverse image search?",
                "To find the original artwork, Wallpaper Studio uploads a 1280-pixel JPEG copy of the extracted frame to the providers you enable:\n\n" +
                "- SauceNAO: finds Pixiv and booru sources (needs a free API key)\n" +
                "- trace.moe: identifies anime scenes (keyless)\n" +
                "- Google Vision: finds pages with the same or similar image (needs a key)\n\n" +
                "No full-size image is ever downloaded unless you choose a result. You can turn this off at any time in Settings."))
        {
            return;
        }

        settings.ReverseSearchEnabled = true;
        settings.ReverseSearchConsent = true;
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync("Could not save the setting", "Reverse search is on for this session, but settings.json could not be updated.", ex);
        }

        RefreshReverseState();
        Notice = "Reverse image search is on. Searching this frame now.";
        _ = RunSearchAsync(automatic: false, ignoreCache: true);
    }

    private async Task ScanFramesAsync(bool autoSelect, CancellationToken? externalToken = null)
    {
        if (Item is null || !IsVideo)
        {
            return;
        }

        if (!HasFfmpeg)
        {
            await _dialogs.ShowWarningAsync("FFmpeg not found",
                "FFmpeg is needed to sample video frames. Set the FFmpeg folder in Settings, or place ffmpeg.exe in an 'ffmpeg' subfolder next to the app.");
            return;
        }

        var token = externalToken ?? _loadCts?.Token ?? CancellationToken.None;
        IsSampling = true;
        IsBusy = true;
        BusyMessage = "Sampling frames across the video…";
        try
        {
            var samples = await _ffmpeg.ExtractFilmstripAsync(
                Item.PrimaryFilePath, Item.DurationSeconds, 12, Item.FileHash, token);
            token.ThrowIfCancellationRequested();

            if (samples.Count == 0)
            {
                Notice = "No frames could be sampled from this video. Try regenerating a frame at a specific time.";
                return;
            }

            FrameSamples.Clear();
            foreach (var sample in samples)
            {
                sample.Thumbnail = await _thumbnails.GetAsync(sample.Path, 240, "filmstrip", token);
                FrameSamples.Add(sample);
            }

            var selected = samples
                .Where(sample => !sample.IsWeak)
                .OrderByDescending(sample => sample.Score)
                .FirstOrDefault();

            if (autoSelect && selected is not null)
            {
                await SelectSampleAsync(selected, token);
                Notice = Item?.CandidateNote ?? $"Picked the clearest of {samples.Count} sampled frames.";
            }
            else
            {
                var isWeak = selected is null;
                Notice = isWeak
                    ? "Every sampled frame looks dark or flat. Scrub the timestamp and try a different moment."
                    : $"Sampled {samples.Count} frames. Click the frame where the artwork is clearest to search from it.";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer review request superseded this one.
        }
        catch (Exception ex)
        {
            Notice = "Frame sampling failed. The video may use an unsupported codec.";
            Logger.Warn($"Frame sampling failed for '{Item?.SourcePath}'.", ex);
        }
        finally
        {
            IsSampling = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseCommandStates();
        }
    }

    private async Task PickSampleAsync(FrameSample? sample)
    {
        if (Item is null || sample is null)
        {
            return;
        }

        await SelectSampleAsync(sample, _loadCts?.Token ?? CancellationToken.None);
    }

    private async Task SelectSampleAsync(FrameSample sample, CancellationToken token)
    {
        if (Item is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            BusyMessage = $"Extracting the frame at {sample.TimeText}…";

            foreach (var item in FrameSamples)
            {
                item.IsSelected = ReferenceEquals(item, sample);
            }

            FrameTimestamp = sample.TimeSeconds;

            var stem = string.IsNullOrWhiteSpace(Item.FileHash)
                ? "video"
                : Item.FileHash[..Math.Min(16, Item.FileHash.Length)];
            var output = Path.Combine(AppPaths.FrameCacheDirectory, $"review-{stem}-{sample.TimeSeconds:0.###}.png");

            var result = await _ffmpeg.ExtractFrameAsync(Item.PrimaryFilePath, sample.TimeSeconds, output, token);
            token.ThrowIfCancellationRequested();

            Item.CandidateFramePath = result.Path;
            Item.CandidateFrameTime = result.TimeSeconds;
            Item.UsesPreviewFallback = false;
            Item.CandidateNote = $"Using the frame at {FormatTime(result.TimeSeconds)} (sharpness score {sample.Score:0}/100).";
            Item.ResetReverseSearch();

            await ShowPreviewAsync(result.Path, token);
            Notice = $"Frame at {FormatTime(result.TimeSeconds)} selected. Searching for its source…";

            if (_settingsService.Current.ReverseSearchEnabled && _settingsService.Current.ReverseSearchConsent)
            {
                _ = RunSearchAsync(automatic: false, ignoreCache: true);
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore: a newer selection superseded this one.
        }
        catch (Exception ex)
        {
            Notice = "The selected frame could not be extracted.";
            Logger.Warn($"Selecting a sampled frame failed for '{Item?.SourcePath}'.", ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseCommandStates();
        }
    }

    private void OpenResult(ReverseSearchResult? result)
    {
        var url = result?.PageUrl ?? result?.ImageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            _dialogs.ShowInfoAsync("No link", "This match does not include a page link.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogs.ShowErrorAsync("Could not open the link", url, ex);
        }
    }

    private async Task UseResultAsync(ReverseSearchResult? result)
    {
        if (Item is null || result is null)
        {
            return;
        }

        if (!result.CanUseImage)
        {
            await _dialogs.ShowInfoAsync("Open the page instead",
                "This match identifies the source but does not expose a direct image file. Open the page to view or download it yourself.");
            OpenResult(result);
            return;
        }

        var size = result.Width > 0 && result.Height > 0 ? result.ResolutionText : "unknown resolution";
        if (!await _dialogs.ConfirmAsync("Use this image as the export candidate?",
                $"{result.Header}\n{result.Subheader}\n\n{size} from {result.SourceName}.\n\n" +
                "Wallpaper Studio will download this one image, check that it decodes, and apply the 1080p rule. " +
                "Nothing is exported until you approve the frame."))
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "Downloading the original…";
        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var downloaded = await _reverse.DownloadOriginalAsync(result, token);

            if (_settingsService.Current.EnforceMinimumResolution &&
                !downloaded.Resolution.PassesMinimum1080p)
            {
                await _dialogs.ShowWarningAsync("Below the 1080p rule",
                    $"{downloaded.Resolution.Display} does not meet the 1080p rule ({ResolutionRules.Explain(downloaded.Resolution.Width, downloaded.Resolution.Height)}).\n\n" +
                    "It was not applied. Choose another match, or turn the rule off in Settings if you really want this size.");
                return;
            }

            Item.CandidateFramePath = downloaded.Path;
            Item.UsesPreviewFallback = false;
            Item.Resolution = downloaded.Resolution;
            Item.CandidateNote = $"Using the {downloaded.Resolution.Display} original from {result.SourceName}.";
            Item.ApprovalStatus = ApprovalStatus.Pending;
            Item.Status = SourceStatus.NeedsReview;

            await ShowPreviewAsync(downloaded.Path, token);
            Notice = downloaded.Quality.Summary.Contains("black", StringComparison.OrdinalIgnoreCase)
                ? "The downloaded image is very dark. Check it carefully before approving."
                : "The downloaded original is now the export candidate. Approve it if it looks right.";
            _shell.ReportText($"Downloaded {downloaded.Resolution.Display} from {result.SourceName}.");
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (ReverseSearchProviderException ex)
        {
            await _dialogs.ShowWarningAsync("Could not download that image", ex.Message);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync("Could not download that image", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseCommandStates();
        }
    }    private async Task LoadPreviewAsync(WallpaperSource item, CancellationToken token, bool autoExtract)
    {
        var existing = item.HasCandidate ? item.CandidateFramePath : null;
        if (existing is not null && (!item.UsesPreviewFallback || item.ApprovalStatus == ApprovalStatus.Approved))
        {
            await ShowPreviewAsync(existing, token);
            return;
        }

        // Scenes are decoded from their package so review shows the real image.
        if (item.Kind == WallpaperKind.SceneProject && autoExtract)
        {
            await ExtractSceneAsync(item, token);
            if (HasPreview)
            {
                return;
            }
        }

        // Scene/project sources may only have a preview asset. Videos are handled
        // below, because a real extracted frame is always better than the preview
        // GIF that Wallpaper Engine ships with many video wallpapers.
        if (item.Kind != WallpaperKind.VideoWallpaper &&
            !string.IsNullOrWhiteSpace(item.PreviewPath) &&
            File.Exists(PathHelper.ToExtendedPath(item.PreviewPath)))
        {
            item.CandidateFramePath = item.PreviewPath;
            item.UsesPreviewFallback = true;
            await ShowPreviewAsync(item.PreviewPath, token);
            return;
        }

        if (item.Kind == WallpaperKind.VideoWallpaper && autoExtract)
        {
            await ExtractFrameAsync(item, FrameTimestamp, token, showErrors: false);
            if (HasPreview)
            {
                return;
            }

            // Extraction failed (unsupported codec or missing FFmpeg). Fall back to
            // the labelled preview so the review screen still shows something.
            if (!string.IsNullOrWhiteSpace(item.PreviewPath) &&
                File.Exists(PathHelper.ToExtendedPath(item.PreviewPath)))
            {
                item.CandidateFramePath = item.PreviewPath;
                item.UsesPreviewFallback = true;
                await ShowPreviewAsync(item.PreviewPath, token);
                return;
            }
        }

        PreviewImage = null;
        PreviewPath = null;
        Notice = item.Kind == WallpaperKind.SceneProject
            ? "No image could be extracted from this scene package. Use \"Find embedded preview\" or choose an image manually."
            : "No preview is available yet. Choose an image to use as the exported frame.";
        RaiseCommandStates();
    }

    private async Task ExtractSceneAsync(WallpaperSource item, CancellationToken token)
    {
        IsExtracting = true;
        IsBusy = true;
        BusyMessage = "Extracting the full-resolution scene image…";
        _shell.ReportText("Extracting the scene image…");
        try
        {
            var result = await _sceneFrames.ExtractAsync(item, token);
            if (result is null)
            {
                Notice = "No image could be extracted from this scene package. Use \"Find embedded preview\" or choose an image manually.";
                return;
            }

            item.CandidateFramePath = result.Path;
            item.UsesPreviewFallback = false;
            if (result.Resolution.IsKnown)
            {
                item.Resolution = result.Resolution;
            }

            if (result.IsAllBlack)
            {
                Notice = "The extracted scene image is almost entirely black. It needs your explicit approval before it can be exported.";
            }
            else if (result.LayerCount > 1)
            {
                Notice = $"Extracted {result.Resolution.Display} from {result.LayerCount} composited layers.";
            }
            else
            {
                Notice = null;
            }

            await ShowPreviewAsync(result.Path, token);
        }
        catch (OperationCanceledException)
        {
            // Navigation cancels the previous load; nothing to report.
        }
        catch (Exception ex)
        {
            Notice = "The scene image could not be extracted. The package may be unreadable or use an unsupported texture format.";
            Logger.Error($"Scene extraction failed for '{item.SourcePath}'.", ex);
        }
        finally
        {
            IsExtracting = false;
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseCommandStates();
        }
    }

    private async Task RegenerateAsync()
    {
        if (Item is null)
        {
            return;
        }

        if (!HasFfmpeg)
        {
            await _dialogs.ShowWarningAsync("FFmpeg not found",
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
                await _dialogs.ShowErrorAsync("Frame extraction failed", Notice, ex);
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
            _dialogs.ShowInfoAsync("No frame selected",
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
            _dialogs.ShowErrorAsync("Approval not saved",
                "The frame is approved in this session, but state.json could not be updated.", ex);
        }

        _shell.ReportText($"{Item.Title} approved. It is ready to export.");
        ReviewCompleted?.Invoke(this, new ApprovalOutcome(Item, Approved: true));
        return Task.CompletedTask;
    }

    private async Task TossAsync()
    {
        if (Item is null)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Toss this wallpaper?",
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

        var files = await _dialogs.PickImagesAsync("Choose an image to use as this wallpaper's static frame", multiselect: false);
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

    private async Task ApplySceneCompositionAsync()
    {
        var item = Item;
        if (item is null || item.Kind != WallpaperKind.SceneProject)
        {
            return;
        }

        var version = ++_compositionVersion;
        var token = _loadCts?.Token ?? CancellationToken.None;
        IsBusy = true;
        BusyMessage = "Re-compositing the scene…";
        try
        {
            var scene = await _sceneFrames.ExtractAsync(item, item.SceneComposition, token);
            if (version != _compositionVersion || !ReferenceEquals(item, Item))
            {
                return;
            }

            if (scene is null)
            {
                Notice = "This scene could not be composited with that choice. Try another composition, or choose an image manually.";
                return;
            }

            item.CandidateFramePath = scene.Path;
            item.UsesPreviewFallback = false;
            if (scene.Resolution.IsKnown)
            {
                item.Resolution = scene.Resolution;
            }

            await ShowPreviewAsync(scene.Path, token);
            Notice = scene.IsAllBlack
                ? "This composition is almost entirely black. Try another one or review the source in Wallpaper Engine."
                : $"Scene composition set to {item.SceneCompositionLabel}.";
            if (_compositionInvalidatesExport)
            {
                _compositionInvalidatesExport = false;
                Notice += " Export it again to save the new composition.";
            }

            _shell.ReportText($"{item.Title}: {Notice}");
        }
        catch (OperationCanceledException)
        {
            // A newer review/composition request superseded this one.
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not re-composite scene '{item.SourcePath}'.", ex);
            Notice = "The scene could not be re-composited. See the log for details.";
        }
        finally
        {
            if (version == _compositionVersion)
            {
                IsBusy = false;
                BusyMessage = string.Empty;
                RaiseCommandStates();
            }
        }
    }

    private async Task ScanEmbeddedAsync()
    {
        if (Item is null)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "Extracting the scene image from the package…";
        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;

            var scene = await _sceneFrames.ExtractAsync(Item, token);
            if (scene is not null)
            {
                Item.CandidateFramePath = scene.Path;
                Item.UsesPreviewFallback = false;
                if (scene.Resolution.IsKnown)
                {
                    Item.Resolution = scene.Resolution;
                }

                await ShowPreviewAsync(scene.Path, token);
                Notice = scene.IsAllBlack
                    ? "The extracted scene image is almost entirely black. Approve it only if it honestly looks right."
                    : $"Extracted the full-resolution scene image ({scene.Resolution.Display}).";
                return;
            }

            BusyMessage = "Scanning the package for a safe embedded preview…";
            var extracted = await _pkgPreviews.TryExtractAsync(Item.PrimaryFilePath, token);
            if (extracted is null)
            {
                Notice = "No image could be extracted from this package, and no safe embedded preview was found. The format may be protected. Choose an image manually or toss this source.";
                await _dialogs.ShowInfoAsync("No image found",
                    "Wallpaper Studio could not decode the scene textures or find a plain JPEG/PNG stream that decodes cleanly. Choose an image manually, or toss the source.");
                return;
            }

            Item.PreviewPath = extracted;
            Item.CandidateFramePath = extracted;
            Item.UsesPreviewFallback = true;
            var resolution = await _thumbnails.Probe.IdentifyAsync(extracted, token);
            if (resolution.IsKnown)
            {
                Item.Resolution = resolution;
            }

            await ShowPreviewAsync(extracted, token);
            Notice = "Found a validated embedded preview. It is labelled as a fallback; approve it only if the image looks right.";
        }
        catch (OperationCanceledException)
        {
            // Ignore.
        }
        catch (Exception ex)
        {
            Notice = "The embedded preview scan failed. The package may be unreadable or locked.";
            await _dialogs.ShowErrorAsync("Preview scan failed", Notice, ex);
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
        _searchOriginalCommand.RaiseCanExecuteChanged();
        _scanFramesCommand.RaiseCanExecuteChanged();
        if (TossCommand is AsyncRelayCommand toss)
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
