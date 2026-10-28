using System.Collections.ObjectModel;
using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

/// <summary>
/// Workshop publishing queue. Publishing is opt-in, private by default, and only
/// reports success when SteamCMD actually creates or updates a Workshop item.
/// Steam credentials are never entered, stored or transmitted here.
/// </summary>
public sealed class PublishViewModel : ObservableObject
{
    private const int MaxLogLines = 400;

    private readonly SettingsService _settingsService;
    private readonly StateStore _state;
    private readonly SteamCmdService _steamCmd;
    private readonly WallpaperProjectBuilder _builder;
    private readonly ThumbnailService _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly ShellState _shell;
    private readonly AsyncRelayCommand _publishAllCommand;

    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private string _steamCmdStatus = "SteamCMD has not been checked yet.";
    private string _steamCmdTone = "muted";

    public PublishViewModel(
        SettingsService settingsService,
        StateStore state,
        SteamCmdService steamCmd,
        WallpaperProjectBuilder builder,
        ThumbnailService thumbnails,
        IDialogService dialogs,
        ShellState shell)
    {
        _settingsService = settingsService;
        _state = state;
        _steamCmd = steamCmd;
        _builder = builder;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _shell = shell;

        Items = [];
        Items.CollectionChanged += (_, _) => RaiseSummary();
        Log = [];

        AddProjectFolderCommand = new AsyncRelayCommand(AddProjectFolderAsync, () => !IsBusy);
        ((AsyncRelayCommand)AddProjectFolderCommand).ExecutionFailed += (_, ex) =>
            _dialogs.ShowErrorAsync("Could not add project", ex.Message, ex);

        AddVideosCommand = new AsyncRelayCommand(AddVideosAsync, () => !IsBusy);
        ((AsyncRelayCommand)AddVideosCommand).ExecutionFailed += (_, ex) =>
            _dialogs.ShowErrorAsync("Could not add video", ex.Message, ex);

        AddImagesCommand = new AsyncRelayCommand(AddImagesAsync, () => !IsBusy);
        ((AsyncRelayCommand)AddImagesCommand).ExecutionFailed += (_, ex) =>
            _dialogs.ShowErrorAsync("Could not add image", ex.Message, ex);

        BuildItemCommand = new RelayCommand<PublishItem>(
            item => _ = BuildItemAsync(item),
            item => !IsBusy && item is { RequiresBuild: true, IsBusyState: false });

        PublishItemCommand = new RelayCommand<PublishItem>(
            item => _ = PublishSingleAsync(item),
            item => !IsBusy && item is { CanPublish: true });

        RemoveItemCommand = new RelayCommand<PublishItem>(RemoveItem, _ => !IsBusy);
        OpenItemFolderCommand = new RelayCommand<PublishItem>(OpenItemFolder);
        OpenWorkshopPageCommand = new RelayCommand<PublishItem>(OpenWorkshopPage);
        ClearCommand = new RelayCommand(Clear, () => Items.Count > 0 && !IsBusy);
        OpenPublishFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(AppPaths.PublishDirectory));
        SignInCommand = new RelayCommand(SignIn, () => !IsBusy);
        DetectSteamCmdCommand = new RelayCommand(DetectSteamCmd, () => !IsBusy);
        BrowseSteamCmdCommand = new AsyncRelayCommand(BrowseSteamCmdAsync, () => !IsBusy);
        PublishHelpCommand = new RelayCommand(ShowHelp);

        _publishAllCommand = new AsyncRelayCommand(PublishAllAsync, () => !IsBusy && Items.Any(item => item.CanPublish));
        _publishAllCommand.ExecutionFailed += (_, ex) => _dialogs.ShowErrorAsync("Publish failed", ex.Message, ex);
        PublishAllCommand = _publishAllCommand;

        RefreshSteamCmdState();
    }

    public ObservableCollection<PublishItem> Items { get; }

    public ObservableCollection<string> Log { get; }

    public AppSettings Settings => _settingsService.Current;

    public IReadOnlyList<string> VisibilityChoices => PublishItem.VisibilityChoices;

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

    public string SteamCmdStatus
    {
        get => _steamCmdStatus;
        private set => SetProperty(ref _steamCmdStatus, value);
    }

    public string SteamCmdTone
    {
        get => _steamCmdTone;
        private set => SetProperty(ref _steamCmdTone, value);
    }

    public string SteamCmdPath
    {
        get => Settings.SteamCmdPath;
        set
        {
            if (!string.Equals(Settings.SteamCmdPath, value, StringComparison.Ordinal))
            {
                Settings.SteamCmdPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string SteamUsername
    {
        get => Settings.SteamUsername;
        set
        {
            if (!string.Equals(Settings.SteamUsername, value, StringComparison.Ordinal))
            {
                Settings.SteamUsername = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public bool HasLog => Log.Count > 0;

    public int TotalCount => Items.Count;

    public int ReadyCount => Items.Count(item => item.CanPublish);

    public int PublishedCount => Items.Count(item => item.IsPublished);

    public int FailedCount => Items.Count(item => item.State == PublishState.Failed);

    public int KnownPublishedCount => _state.PublishedCount;

    public string QueueSummary => Items.Count == 0
        ? "Nothing queued for publishing."
        : $"{TotalCount:N0} queued · {ReadyCount:N0} ready · {PublishedCount:N0} published this session · {FailedCount:N0} failed";

    public string SteamCmdStateText => string.IsNullOrWhiteSpace(SteamUsername)
        ? "SteamCMD reuses the session you created with Sign in. Set your Steam account name so uploads know which session to use."
        : $"Uploads will use the SteamCMD session for '{SteamUsername}'. Passwords and Steam Guard codes are entered only in Valve's own window.";

    public ICommand AddProjectFolderCommand { get; }
    public ICommand AddVideosCommand { get; }
    public ICommand AddImagesCommand { get; }
    public ICommand BuildItemCommand { get; }
    public ICommand PublishItemCommand { get; }
    public ICommand RemoveItemCommand { get; }
    public ICommand OpenItemFolderCommand { get; }
    public ICommand OpenWorkshopPageCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenPublishFolderCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand DetectSteamCmdCommand { get; }
    public ICommand BrowseSteamCmdCommand { get; }
    public ICommand PublishAllCommand { get; }
    public ICommand PublishHelpCommand { get; }

    public void RefreshSettings()
    {
        OnPropertyChanged(nameof(Settings), nameof(SteamCmdPath), nameof(SteamUsername), nameof(SteamCmdStateText));
        RefreshSteamCmdState();
    }

    public void RefreshSteamCmdState()
    {
        var executable = _steamCmd.Locate(Settings, out var status);
        SteamCmdStatus = executable is not null ? "SteamCMD ready · " + executable : status;
        SteamCmdTone = executable is not null ? "success" : "warning";
    }

    private async Task AddProjectFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a Wallpaper Engine project folder (contains project.json)");
        if (folder is null)
        {
            return;
        }

        folder = PathHelper.Normalize(folder);
        var projectJson = Path.Combine(folder, MediaFileTypes.ProjectFileName);
        if (!File.Exists(PathHelper.ToExtendedPath(projectJson)))
        {
            await _dialogs.ShowWarningAsync("Not a project folder",
                "That folder does not contain a project.json file. Choose the folder of a single wallpaper project; a whole 'workshop/content/431960' folder is not supported here.");
            return;
        }

        if (Items.Any(item => PathHelper.PathsEqual(item.SourcePath, folder)))
        {
            _shell.ReportText("That project is already in the publish list.");
            return;
        }

        ProjectManifestReader.TryRead(projectJson, out var manifest, out _);
        ProjectManifestReader.TryReadWorkshopId(projectJson, out var workshopId);
        var preview = PreviewResolver.FindPreviewImage(folder);
        var title = !string.IsNullOrWhiteSpace(manifest.Title)
            ? manifest.Title!
            : new DirectoryInfo(folder).Name;

        var item = new PublishItem
        {
            SourcePath = folder,
            Kind = PublishKind.ExistingProject,
            ExistingWorkshopId = workshopId
        };
        item.ContentFolder = folder;
        item.PreviewFile = preview;
        item.Title = title;
        item.StatusDetail = workshopId is null
            ? "Ready to publish as a new Workshop item."
            : $"Will update existing Workshop item {workshopId}.";
        if (preview is null)
        {
            item.WarningText = "No preview image was found in this project folder. Steam may show a default preview; adding a preview.jpg is recommended.";
        }

        Items.Add(item);
        _shell.ReportText($"Added \"{title}\" to the publish list.");
        await LoadThumbnailAsync(item);
    }

    private async Task AddVideosAsync()
    {
        var files = await _dialogs.PickVideosAsync("Choose video files to turn into video wallpapers", multiselect: true);
        if (files.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var file in files)
        {
            if (!MediaFileTypes.IsVideo(file))
            {
                continue;
            }

            if (Items.Any(item => PathHelper.PathsEqual(item.SourcePath, file)))
            {
                continue;
            }

            var item = new PublishItem { SourcePath = PathHelper.Normalize(file), Kind = PublishKind.NewProjectFromVideo };
            item.Title = Path.GetFileNameWithoutExtension(file);
            item.StatusDetail = "Click Build project to create a video wallpaper project folder.";
            Items.Add(item);
            added++;
        }

        _shell.ReportText($"{added:N0} video(s) added. Build them before publishing.");
        return;
    }

    private async Task AddImagesAsync()
    {
        var files = await _dialogs.PickImagesAsync("Choose images to turn into scene wallpapers", multiselect: true);
        if (files.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var file in files)
        {
            if (!MediaFileTypes.IsExportImage(file))
            {
                continue;
            }

            if (Items.Any(item => PathHelper.PathsEqual(item.SourcePath, file)))
            {
                continue;
            }

            var item = new PublishItem { SourcePath = PathHelper.Normalize(file), Kind = PublishKind.NewProjectFromImage };
            item.Title = Path.GetFileNameWithoutExtension(file);
            item.IsExperimental = true;
            item.StatusDetail = "Click Build project to generate an experimental scene folder.";
            Items.Add(item);
            added++;
        }

        _shell.ReportText($"{added:N0} image(s) added. Build them before publishing.");
        foreach (var item in Items.Where(item => item.Kind == PublishKind.NewProjectFromImage && item.Thumbnail is null).ToList())
        {
            await LoadThumbnailAsync(item);
        }
    }

    private async Task BuildItemAsync(PublishItem? item)
    {
        if (item is null || !item.RequiresBuild || IsBusy)
        {
            return;
        }

        var token = _shell.Begin($"Building \"{item.DisplayTitle}\"…", indeterminate: true);
        IsBusy = true;
        item.State = PublishState.Building;
        item.ErrorMessage = null;
        try
        {
            var progress = new Progress<string>(message =>
            {
                BusyMessage = message;
                _shell.ReportText(message);
            });

            var result = await _builder.BuildAsync(item, Settings.JpegQuality, token, progress);
            item.ContentFolder = result.ProjectFolder;
            item.PreviewFile = result.PreviewFile;
            item.ContentBytes = result.ContentBytes;
            item.IsExperimental = result.IsExperimental;
            item.WarningText = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;
            item.State = PublishState.Ready;
            item.StatusDetail = "Project built. Review the folder, then publish.";
            item.MarkAvailableForPublish();
            AppendLog($"Built project for \"{item.DisplayTitle}\" ({PathHelper.HumanSize(result.ContentBytes)}).");
            _shell.Complete($"Project built: {PathHelper.HumanSize(result.ContentBytes)}.");
            await LoadThumbnailAsync(item);
        }
        catch (OperationCanceledException)
        {
            item.State = PublishState.Draft;
            item.StatusDetail = "Build cancelled.";
            _shell.Complete("Build cancelled.");
        }
        catch (Exception ex)
        {
            item.State = PublishState.Failed;
            item.ErrorMessage = ex.Message;
            item.StatusDetail = "Build failed.";
            AppendLog("Build failed: " + ex.Message);
            await _dialogs.ShowErrorAsync("Could not build project", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseSummary();
        }
    }

    private async Task PublishSingleAsync(PublishItem? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (!EnsureSteamCmd())
        {
            return;
        }

        if (item.RequiresBuild && !item.IsBuilt)
        {
            await BuildItemAsync(item);
            if (!item.IsBuilt)
            {
                return;
            }
        }

        if (!await ConfirmPublishAsync(item))
        {
            return;
        }

        await RunPublishAsync(item);
    }

    private async Task PublishAllAsync()
    {
        var candidates = Items.Where(item => item.CanPublish).ToList();
        if (candidates.Count == 0)
        {
            await _dialogs.ShowInfoAsync("Nothing to publish", "Build the queued items first, then publish.");
            return;
        }

        if (!EnsureSteamCmd())
        {
            return;
        }

        var privateCount = candidates.Count(item => item.Visibility == PublishVisibility.Private);
        if (!await _dialogs.ConfirmAsync("Publish all",
                $"Publish {candidates.Count:N0} item(s) to the Steam Workshop now?\n\n" +
                $"{privateCount:N0} will be private and visible only to your Steam account.\n\n" +
                "You can change visibility later in Wallpaper Engine or on the Workshop page. Wallpaper Studio never stores your Steam password."))
        {
            return;
        }

        var succeeded = 0;
        var failed = 0;
        foreach (var item in candidates)
        {
            if (_shell.IsCancellationRequested)
            {
                AppendLog("Publish all cancelled.");
                break;
            }

            if (item.RequiresBuild && !item.IsBuilt)
            {
                await BuildItemAsync(item);
                if (!item.IsBuilt)
                {
                    failed++;
                    continue;
                }
            }

            await RunPublishAsync(item);
            if (item.State == PublishState.Published)
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        RaiseSummary();
        _shell.ReportText($"Publish finished: {succeeded:N0} succeeded, {failed:N0} failed.");
    }

    private async Task RunPublishAsync(PublishItem item)
    {
        var token = _shell.Begin($"Publishing \"{item.DisplayTitle}\"…", indeterminate: true);
        IsBusy = true;
        item.State = PublishState.Uploading;
        item.ErrorMessage = null;
        item.StatusDetail = "Running SteamCMD…";
        try
        {
            var slug = PathHelper.SanitizeFileName(item.DisplayTitle, 48);
            var vdfName = $"{slug}-{item.Id:N}".Substring(0, Math.Min(60, $"{slug}-{item.Id:N}".Length));
            var vdfPath = Path.Combine(AppPaths.PublishDirectory, vdfName + ".vdf");

            WorkshopVdfWriter.Write(vdfPath, new WorkshopVdfWriter.Fields(
                item.ContentFolder,
                item.HasPreview ? item.PreviewFile : null,
                item.Visibility,
                item.Title.Trim(),
                item.Description,
                item.ChangeNote,
                item.PublishedFileId ?? item.ExistingWorkshopId));

            // Persist the account name so the next launch remembers it.
            _settingsService.Save();

            AppendLog($"Publishing \"{item.Title}\" · {item.VisibilityLabel} · {PathHelper.HumanSize(item.ContentBytes)}");
            var progress = new Progress<string>(AppendLog);
            var result = await _steamCmd.UploadAsync(vdfPath, Settings, token, progress);

            if (result.Success)
            {
                item.PublishedFileId = result.PublishedFileId;
                item.WorkshopUrl = result.WorkshopUrl;
                item.State = PublishState.Published;
                item.StatusDetail = item.Visibility == PublishVisibility.Private
                    ? "Published privately. Only your account can see it."
                    : $"Published ({item.VisibilityLabel}).";
                item.ErrorMessage = null;
                _state.RecordPublish(item);
                AppendLog($"Success. Workshop id {result.PublishedFileId}.");
                _shell.Complete($"Published \"{item.Title}\" to the Steam Workshop.");
            }
            else
            {
                item.State = PublishState.Failed;
                item.ErrorMessage = result.Error;
                item.StatusDetail = result.NeedsSignIn ? "Sign in to SteamCMD first." : "SteamCMD reported a failure.";
                _state.RecordPublishFailure(item, result.Error);
                AppendLog("Failed: " + result.Error);
                _shell.Complete("Publish failed. Review the SteamCMD output above.");
                await _dialogs.ShowErrorAsync("Publish failed", result.Error ?? "SteamCMD did not report a new Workshop item.");
            }
        }
        catch (OperationCanceledException)
        {
            item.State = PublishState.Ready;
            item.StatusDetail = "Publish cancelled.";
            AppendLog("Publish cancelled.");
            _shell.Complete("Publish cancelled.");
        }
        catch (Exception ex)
        {
            item.State = PublishState.Failed;
            item.ErrorMessage = ex.Message;
            item.StatusDetail = "Publish failed.";
            AppendLog("Publish error: " + ex.Message);
            await _dialogs.ShowErrorAsync("Publish failed", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
            RaiseSummary();
        }
    }

    private bool EnsureSteamCmd()
    {
        if (_steamCmd.SteamCmdPath is not null && File.Exists(PathHelper.ToExtendedPath(_steamCmd.SteamCmdPath)))
        {
            return true;
        }

        RefreshSteamCmdState();
        if (_steamCmd.SteamCmdPath is not null)
        {
            return true;
        }

        _dialogs.ShowInfoAsync("SteamCMD not found",
            "Publishing needs Valve's SteamCMD (steamcmd.exe).\n\n" +
            "1. Download SteamCMD from Valve.\n" +
            "2. Unzip it and choose steamcmd.exe here (or place it in a 'steamcmd' subfolder next to the app).\n" +
            "3. Use \"Sign in to SteamCMD\" once.\n\n" +
            "Wallpaper Studio does not bundle or download SteamCMD.");
        return false;
    }

    private async Task<bool> ConfirmPublishAsync(PublishItem item)
    {
        var preview = item.HasPreview ? "yes" : "no (Steam may show a default preview)";
        var target = string.IsNullOrWhiteSpace(item.PublishedFileId ?? item.ExistingWorkshopId)
            ? "a new Workshop item"
            : $"existing Workshop item {item.PublishedFileId ?? item.ExistingWorkshopId}";

        return await _dialogs.ConfirmAsync("Publish to Steam Workshop",
            $"Publish \"{item.DisplayTitle}\" as {target}?\n\n" +
            $"Visibility: {item.VisibilityLabel}\n" +
            $"Preview image: {preview}\n" +
            $"Content folder:\n{item.ContentFolder}\n\n" +
            "Private items are visible only to your Steam account. Wallpaper Studio never stores your password.");
    }

    private void RemoveItem(PublishItem? item)
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
        AppendLog("Publish queue cleared. Built folders and Workshop items are not deleted.");
        RaiseSummary();
    }

    private void OpenItemFolder(PublishItem? item)
    {
        if (item is null)
        {
            return;
        }

        var folder = !string.IsNullOrWhiteSpace(item.ContentFolder) ? item.ContentFolder : item.SourcePath;
        if (Directory.Exists(PathHelper.ToExtendedPath(folder)))
        {
            _dialogs.OpenFolder(folder);
        }
        else if (File.Exists(PathHelper.ToExtendedPath(folder)))
        {
            _dialogs.ShowFileInExplorer(folder);
        }
        else
        {
            _dialogs.ShowWarningAsync("Nothing to open", "This item has not been built yet.");
        }
    }

    private void OpenWorkshopPage(PublishItem? item)
    {
        if (item is null || !item.HasWorkshopLink)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.WorkshopUrl!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogs.ShowErrorAsync("Could not open the Workshop page", item.WorkshopUrl ?? string.Empty, ex);
        }
    }

    private void SignIn()
    {
        if (_steamCmd.SteamCmdPath is null)
        {
            _steamCmd.Locate(Settings, out _);
        }

        _settingsService.Save();
        if (_steamCmd.TryOpenSignIn(Settings, out var message))
        {
            AppendLog(message);
            _shell.ReportText("SteamCMD sign-in window opened.");
            RefreshSteamCmdState();
        }
        else
        {
            _dialogs.ShowInfoAsync("SteamCMD not available", message);
        }
    }

    private void DetectSteamCmd()
    {
        var executable = _steamCmd.Locate(Settings, out var status);
        if (executable is not null)
        {
            SteamCmdPath = executable;
            SteamCmdStatus = "SteamCMD ready · " + executable;
            SteamCmdTone = "success";
            AppendLog("Detected SteamCMD at " + executable);
        }
        else
        {
            SteamCmdStatus = status;
            SteamCmdTone = "warning";
            _dialogs.ShowInfoAsync("SteamCMD not found",
                "Wallpaper Studio looks beside the app, in a 'steamcmd' subfolder, on PATH, and at C:\\steamcmd.\n\n" +
                "SteamCMD is Valve's tool and is distributed by Valve; Wallpaper Studio does not bundle or download it.");
        }
    }

    private async Task BrowseSteamCmdAsync()
    {
        var chosen = await _dialogs.PickExecutableAsync("Choose steamcmd.exe", SteamCmdPath);
        if (chosen is not null)
        {
            SteamCmdPath = chosen;
            _settingsService.Save();
            RefreshSteamCmdState();
        }
    }

    private void ShowHelp()
    {
        _dialogs.ShowInfoAsync("How Workshop publishing works",
            "Wallpaper Studio uploads through Valve's SteamCMD, not through the Wallpaper Engine client.\n\n" +
            "1. Add an existing project folder, a video, or an image.\n" +
            "2. New items are built into a normal project folder under the app's data folder.\n" +
            "3. Click \"Sign in to SteamCMD\" once and sign in inside Valve's window. Your password is never seen by this app.\n" +
            "4. Publish. Items are private by default and only your account can see them.\n\n" +
            "Wallpaper Studio only reports success when SteamCMD writes a real Workshop id back into the item script. " +
            "It never invents a success result. Import the finished project in Wallpaper Engine to double-check it before making anything public.");
    }

    private async Task LoadThumbnailAsync(PublishItem item)
    {
        var path = item.HasPreview
            ? item.PreviewFile
            : item.Kind == PublishKind.NewProjectFromImage ? item.SourcePath : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            item.IsThumbnailLoading = true;
            item.Thumbnail = await _thumbnails.GetAsync(path!, 260, "publish");
        }
        catch (Exception ex)
        {
            Logger.Debug($"Publish thumbnail failed for '{path}': {ex.Message}");
        }
        finally
        {
            item.IsThumbnailLoading = false;
        }
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Log.Add($"{DateTime.Now:HH:mm:ss}  {message.Trim()}");
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasLog));
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(
            nameof(HasItems), nameof(IsEmpty), nameof(TotalCount), nameof(ReadyCount),
            nameof(PublishedCount), nameof(FailedCount), nameof(KnownPublishedCount), nameof(QueueSummary));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        ((AsyncRelayCommand)AddProjectFolderCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)AddVideosCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)AddImagesCommand).RaiseCanExecuteChanged();
        _publishAllCommand.RaiseCanExecuteChanged();

        if (BuildItemCommand is RelayCommand<PublishItem> build)
        {
            build.RaiseCanExecuteChanged();
        }

        if (PublishItemCommand is RelayCommand<PublishItem> publish)
        {
            publish.RaiseCanExecuteChanged();
        }

        if (ClearCommand is RelayCommand clear)
        {
            clear.RaiseCanExecuteChanged();
        }

        if (SignInCommand is RelayCommand signIn)
        {
            signIn.RaiseCanExecuteChanged();
        }

        if (DetectSteamCmdCommand is RelayCommand detect)
        {
            detect.RaiseCanExecuteChanged();
        }

        if (BrowseSteamCmdCommand is AsyncRelayCommand browse)
        {
            browse.RaiseCanExecuteChanged();
        }
    }
}
