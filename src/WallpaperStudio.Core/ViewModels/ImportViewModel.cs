using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

/// <summary>
/// Import queue for images the user wants to bring into Wallpaper Engine.
/// Low-resolution images cannot advance without an explicit approve/toss
/// decision. Staging only copies files to a normal folder and writes human
/// instructions; it never touches Wallpaper Engine or Steam data.
/// </summary>
public sealed class ImportViewModel : ObservableObject
{
    private readonly ImportService _imports;
    private readonly SettingsService _settingsService;
    private readonly WallpaperEngineLocator _wallpaperEngine;
    private readonly ThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly ShellState _shell;
    private readonly AsyncRelayCommand _stageCommand;

    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private string? _stagingFolder;
    private string? _instructionsFile;
    private int _stagedCount;
    private string _wallpaperEngineStatus = "Wallpaper Engine status has not been checked yet.";

    public ImportViewModel(
        ImportService imports,
        SettingsService settingsService,
        WallpaperEngineLocator wallpaperEngine,
        ThumbnailService thumbnails,
        IDialogService dialogs,
        ShellState shell)
    {
        _imports = imports;
        _settingsService = settingsService;
        _wallpaperEngine = wallpaperEngine;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _shell = shell;

        Items = [];
        Items.CollectionChanged += (_, _) => RaiseSummary();

        AddImagesCommand = new AsyncRelayCommand(AddImagesAsync, () => !IsBusy);
        ((AsyncRelayCommand)AddImagesCommand).ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Could not add images", ex.Message, ex);

        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, () => !IsBusy);
        ((AsyncRelayCommand)AddFolderCommand).ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Could not add folder", ex.Message, ex);

        _stageCommand = new AsyncRelayCommand(StageAsync, () => !IsBusy && Items.Any(item => item.CanStage));
        _stageCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Import staging failed", ex.Message, ex);
        StageCommand = _stageCommand;

        ApproveItemCommand = new RelayCommand<ImportItem>(ApproveItem);
        TossItemCommand = new RelayCommand<ImportItem>(TossItem);
        RemoveItemCommand = new RelayCommand<ImportItem>(RemoveItem);
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0);
        OpenStagingFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_stagingFolder ?? string.Empty), () => _stagingFolder is not null);
        ShowInstructionsCommand = new RelayCommand(ShowInstructions, () => _instructionsFile is not null && File.Exists(_instructionsFile));
        CopyInstructionsCommand = new RelayCommand(CopyInstructions, () => _instructionsFile is not null && File.Exists(_instructionsFile));
        LaunchWallpaperEngineCommand = new RelayCommand(LaunchWallpaperEngine);
        ManualInstructionsCommand = new RelayCommand(ShowManualInstructions);
        OpenExportFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_settingsService.Current.ExportFolder));
    }

    public ObservableCollection<ImportItem> Items { get; }

    public AppSettings Settings => _settingsService.Current;

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

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public string? StagingFolder
    {
        get => _stagingFolder;
        private set
        {
            if (SetProperty(ref _stagingFolder, value))
            {
                OnPropertyChanged(nameof(HasStagingResult), nameof(StagingFolderDisplay));
                RaiseCommandStates();
            }
        }
    }

    public string StagingFolderDisplay => string.IsNullOrWhiteSpace(StagingFolder)
        ? string.Empty
        : PathHelper.Ellipsize(StagingFolder, 110);

    public string? InstructionsFile
    {
        get => _instructionsFile;
        private set
        {
            if (SetProperty(ref _instructionsFile, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public int StagedCount
    {
        get => _stagedCount;
        private set
        {
            if (SetProperty(ref _stagedCount, value))
            {
                OnPropertyChanged(nameof(HasStagingResult), nameof(StagingSummary));
            }
        }
    }

    public bool HasStagingResult => !string.IsNullOrWhiteSpace(StagingFolder) && StagedCount > 0;

    public string StagingSummary => HasStagingResult
        ? $"{StagedCount:N0} image(s) staged for a manual Wallpaper Engine import."
        : string.Empty;

    public string WallpaperEngineStatus
    {
        get => _wallpaperEngineStatus;
        private set => SetProperty(ref _wallpaperEngineStatus, value);
    }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public int TotalCount => Items.Count;

    public int ApprovedCount => Items.Count(item => item.CanStage);

    public int PendingCount => Items.Count(item => item.Decision == ImportDecision.Pending);

    public int TossedCount => Items.Count(item => item.Decision == ImportDecision.Tossed);

    public int LowResolutionCount => Items.Count(item => item.IsLowResolution);

    public int FailedCount => Items.Count(item => item.Decision == ImportDecision.Failed);

    public bool HasPendingDecisions => PendingCount > 0;

    public bool EnforceMinimumResolution => Settings.EnforceMinimumResolution;

    public string QueueSummary => Items.Count == 0
        ? "No images queued yet."
        : $"{TotalCount:N0} queued · {ApprovedCount:N0} approved · {PendingCount:N0} awaiting decision · {TossedCount:N0} tossed";

    public string RuleSummary => EnforceMinimumResolution
        ? "1080p rule is ON: long edge ≥ 1920 and short edge ≥ 1080. Low-resolution images need an explicit approve or toss."
        : "1080p rule is OFF. Every readable image is accepted without a prompt.";

    public ICommand AddImagesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand StageCommand { get; }
    public ICommand ApproveItemCommand { get; }
    public ICommand TossItemCommand { get; }
    public ICommand RemoveItemCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenStagingFolderCommand { get; }
    public ICommand ShowInstructionsCommand { get; }
    public ICommand CopyInstructionsCommand { get; }
    public ICommand LaunchWallpaperEngineCommand { get; }
    public ICommand ManualInstructionsCommand { get; }
    public ICommand OpenExportFolderCommand { get; }

    public void RefreshRuleState()
    {
        OnPropertyChanged(nameof(EnforceMinimumResolution), nameof(RuleSummary));
        if (!EnforceMinimumResolution)
        {
            foreach (var item in Items.Where(item => item.Decision == ImportDecision.Pending))
            {
                item.Decision = ImportDecision.Approved;
                item.Message = "Accepted because the 1080p rule is off.";
            }

            RaiseSummary();
        }
    }

    private async Task AddImagesAsync()
    {
        var files = await _dialogs.PickImagesAsync("Choose images to queue for Wallpaper Engine", multiselect: true);
        if (files.Count == 0)
        {
            return;
        }

        await LoadPathsAsync(files);
    }

    private async Task AddFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a folder of images to queue");
        if (folder is null)
        {
            return;
        }

        IsBusy = true;
        BusyMessage = "Looking for images…";
        try
        {
            var files = await Task.Run(() => EnumerateImages(folder));
            if (files.Count == 0)
            {
                await _dialogs.ShowInfoAsync("No images found", "That folder does not contain any supported image files.");
                return;
            }

            if (files.Count > 2_000 && !await _dialogs.ConfirmAsync("Large folder",
                    $"{files.Count:N0} images were found. Loading all of them may take a while. Continue?"))
            {
                return;
            }

            await LoadPathsAsync(files);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task LoadPathsAsync(IReadOnlyList<string> files)
    {
        var token = _shell.Begin($"Reading {files.Count:N0} image(s)…");
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message =>
            {
                BusyMessage = message;
                _shell.ReportText(message);
            });

            var loaded = await _imports.LoadImagesAsync(files, token, progress);
            foreach (var item in loaded)
            {
                if (!EnforceMinimumResolution && item.Decision == ImportDecision.Pending)
                {
                    item.Decision = ImportDecision.Approved;
                    item.Message = "Accepted because the 1080p rule is off.";
                }

                if (!Items.Any(existing => PathHelper.PathsEqual(existing.FilePath, item.FilePath)))
                {
                    Items.Add(item);
                }
            }

            RaiseSummary();
            _shell.Complete($"{loaded.Count:N0} image(s) added to the import queue.");
            foreach (var item in loaded)
            {
                _ = LoadThumbnailAsync(item);
            }
        }
        catch (OperationCanceledException)
        {
            _shell.Complete("Reading the import queue was cancelled.");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task StageAsync()
    {
        if (Items.Count == 0)
        {
            return;
        }

        if (!EnforceMinimumResolution)
        {
            foreach (var pending in Items.Where(item => item.Decision == ImportDecision.Pending))
            {
                pending.Decision = ImportDecision.Approved;
            }
        }
        else if (HasPendingDecisions)
        {
            var resolved = await ResolvePendingLowResolutionAsync();
            if (!resolved)
            {
                _shell.ReportText("Import staging cancelled. No files were copied.");
                return;
            }
        }

        var approved = Items.Where(item => item.CanStage).ToList();
        if (approved.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Nothing to stage",
                "There are no approved images. Approve the queue (or approve low-resolution images individually) first.");
            return;
        }

        var token = _shell.Begin($"Staging {approved.Count:N0} image(s)…");
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message =>
            {
                BusyMessage = message;
                _shell.ReportText(message);
            });

            var result = await _imports.StageAsync(Items, Settings, token, progress);
            StagingFolder = result.StagingFolder;
            InstructionsFile = result.InstructionsFile;
            StagedCount = result.CopiedCount;
            RaiseSummary();
            _shell.Complete($"Staged {result.CopiedCount:N0} image(s). Follow the instructions to import them into Wallpaper Engine.");
        }
        catch (OperationCanceledException)
        {
            _shell.Complete("Import staging was cancelled.");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseSummary();
        }
    }

    private async Task<bool> ResolvePendingLowResolutionAsync()
    {
        var pending = Items.Where(item => item.Decision == ImportDecision.Pending && item.IsLowResolution).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        _shell.ReportText($"{pending.Count:N0} image(s) need a resolution decision.");

        foreach (var item in pending)
        {
            var choice = await _dialogs.AskLowResolutionAsync(item);
            switch (choice)
            {
                case LowResolutionChoice.ApproveAnyway:
                    item.Decision = ImportDecision.ApprovedLowResolution;
                    item.Message = "Approved below the 1080p rule.";
                    break;
                case LowResolutionChoice.Toss:
                    item.Decision = ImportDecision.Tossed;
                    item.Message = "Tossed because it is below the 1080p rule.";
                    break;
                case LowResolutionChoice.CancelBatch:
                    return false;
            }

            // Keep the UI responsive while walking a long queue.
            await Task.Yield();
        }

        RaiseSummary();
        return true;
    }

    private void ApproveItem(ImportItem? item)
    {
        if (item is null || item.IsStaged)
        {
            return;
        }

        item.Decision = item.IsLowResolution ? ImportDecision.ApprovedLowResolution : ImportDecision.Approved;
        item.Message = item.IsLowResolution ? "Approved below the 1080p rule." : null;
        RaiseSummary();
    }

    private void TossItem(ImportItem? item)
    {
        if (item is null || item.IsStaged)
        {
            return;
        }

        item.Decision = ImportDecision.Tossed;
        item.Message = "Tossed by you.";
        RaiseSummary();
    }

    private void RemoveItem(ImportItem? item)
    {
        if (item is null)
        {
            return;
        }

        Items.Remove(item);
        RaiseSummary();
    }

    private void Clear()
    {
        Items.Clear();
        StagingFolder = null;
        InstructionsFile = null;
        StagedCount = 0;
        RaiseSummary();
        _shell.ReportText("Import queue cleared.");
    }

    private void ShowInstructions()
    {
        if (_instructionsFile is null || !File.Exists(_instructionsFile))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _instructionsFile,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogs.ShowErrorAsync("Could not open instructions", _instructionsFile, ex);
        }
    }

    private void CopyInstructions()
    {
        if (_instructionsFile is null || !File.Exists(_instructionsFile))
        {
            return;
        }

        try
        {
            _dialogs.CopyText(File.ReadAllText(_instructionsFile));
            _shell.ReportText("Import instructions copied to the clipboard.");
        }
        catch (Exception ex)
        {
            _dialogs.ShowErrorAsync("Could not copy instructions", "The instructions file could not be read.", ex);
        }
    }

    private void LaunchWallpaperEngine()
    {
        var executable = _wallpaperEngine.Locate(Settings, out var status);
        if (executable is null)
        {
            WallpaperEngineStatus = status;
            _dialogs.ShowInfoAsync("Wallpaper Engine not found",
                "Wallpaper Studio could not find wallpaper64.exe or wallpaper32.exe.\n\n" +
                "Set the Wallpaper Engine folder in Settings, then use Launch Wallpaper Engine again. " +
                "You can always import the staged images manually.");
            return;
        }

        if (_wallpaperEngine.TryLaunch(executable, out var message))
        {
            WallpaperEngineStatus = $"Launched: {executable}";
            _shell.ReportText("Wallpaper Engine launched. Use its Create Wallpaper / Editor action to import a staged image.");
        }
        else
        {
            WallpaperEngineStatus = message;
            _dialogs.ShowWarningAsync("Could not launch Wallpaper Engine", message);
        }
    }

    private void ShowManualInstructions()
    {
        _dialogs.ShowInfoAsync("Manual import into Wallpaper Engine",
            "Wallpaper Engine has no documented command-line import or Workshop publishing API, so Wallpaper Studio does not automate this.\n\n" +
            "1. Stage your approved images.\n" +
            "2. Open Wallpaper Engine.\n" +
            "3. Create a wallpaper and choose a staged image.\n" +
            "4. Use it as your desktop wallpaper, or send the exported JPEG to your tablet.\n\n" +
            "Personal use only. Do not publish content you do not have rights to use.");
    }

    private async Task LoadThumbnailAsync(ImportItem item)
    {
        try
        {
            item.IsThumbnailLoading = true;
            var image = await _thumbnails.GetAsync(item.FilePath, 240, "import");
            item.Thumbnail = image;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Import thumbnail failed for '{item.FileName}': {ex.Message}");
        }
        finally
        {
            item.IsThumbnailLoading = false;
        }
    }

    private static List<string> EnumerateImages(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        try
        {
            return Directory.EnumerateFiles(PathHelper.ToExtendedPath(folder), "*", options)
                .Select(path => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path)
                .Where(MediaFileTypes.IsExportImage)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not enumerate images in '{folder}'.", ex);
            return [];
        }
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(
            nameof(HasItems), nameof(IsEmpty), nameof(TotalCount), nameof(ApprovedCount), nameof(PendingCount),
            nameof(TossedCount), nameof(LowResolutionCount), nameof(FailedCount), nameof(HasPendingDecisions),
            nameof(QueueSummary));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        _stageCommand.RaiseCanExecuteChanged();
        if (ClearCommand is RelayCommand clear)
        {
            clear.RaiseCanExecuteChanged();
        }

        if (OpenStagingFolderCommand is RelayCommand open)
        {
            open.RaiseCanExecuteChanged();
        }

        if (ShowInstructionsCommand is RelayCommand show)
        {
            show.RaiseCanExecuteChanged();
        }

        if (CopyInstructionsCommand is RelayCommand copy)
        {
            copy.RaiseCanExecuteChanged();
        }
    }
}
