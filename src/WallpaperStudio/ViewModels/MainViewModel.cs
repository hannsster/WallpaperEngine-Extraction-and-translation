using System.Reflection;
using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

/// <summary>
/// Shell view model: owns navigation between the four screens and surfaces
/// startup warnings in one place. Child view models are composed by the app.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly StateStore _state;
    private readonly FfmpegService _ffmpeg;
    private readonly DialogService _dialogs;
    private object _currentPage;
    private string _currentPageKey = "library";
    private string? _startupNotice;

    public MainViewModel(
        LibraryViewModel library,
        ApprovalViewModel approval,
        ImportViewModel import,
        SettingsViewModel settings,
        ShellState shell,
        SettingsService settingsService,
        StateStore state,
        FfmpegService ffmpeg,
        DialogService dialogs)
    {
        Library = library;
        Approval = approval;
        Import = import;
        Settings = settings;
        Shell = shell;
        _settingsService = settingsService;
        _state = state;
        _ffmpeg = ffmpeg;
        _dialogs = dialogs;
        _currentPage = library;

        ShowLibraryCommand = new RelayCommand(() => Navigate("library", Library));
        ShowApprovalCommand = new RelayCommand(() => Navigate("approval", Approval));
        ShowImportCommand = new RelayCommand(() => Navigate("import", Import));
        ShowSettingsCommand = new RelayCommand(() => Navigate("settings", Settings));
        DismissStartupNoticeCommand = new RelayCommand(() => StartupNotice = null);
        OpenLogFolderCommand = new RelayCommand(Logger.OpenLogFolder);

        Library.ReviewRequested += OnReviewRequested;
        Approval.BackRequested += (_, _) => Navigate("library", Library);
        Approval.ReviewCompleted += OnReviewCompleted;
        Settings.SettingsSaved += OnSettingsSaved;

        BuildStartupNotice();
    }

    public LibraryViewModel Library { get; }
    public ApprovalViewModel Approval { get; }
    public ImportViewModel Import { get; }
    public SettingsViewModel Settings { get; }
    public ShellState Shell { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageKey
    {
        get => _currentPageKey;
        private set
        {
            if (SetProperty(ref _currentPageKey, value))
            {
                OnPropertyChanged(
                    nameof(IsLibraryPage), nameof(IsApprovalPage), nameof(IsImportPage), nameof(IsSettingsPage));
            }
        }
    }

    public bool IsLibraryPage => CurrentPageKey == "library";
    public bool IsApprovalPage => CurrentPageKey == "approval";
    public bool IsImportPage => CurrentPageKey == "import";
    public bool IsSettingsPage => CurrentPageKey == "settings";

    public string? StartupNotice
    {
        get => _startupNotice;
        private set
        {
            if (SetProperty(ref _startupNotice, value))
            {
                OnPropertyChanged(nameof(HasStartupNotice));
            }
        }
    }

    public bool HasStartupNotice => !string.IsNullOrWhiteSpace(StartupNotice);

    public string VersionText => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public string StorageBadge => AppPaths.PortableMode ? "Portable" : "Local app data";

    public string StorageTone => AppPaths.PortableMode ? "success" : "muted";

    public string DataLocation => AppPaths.DataDirectory;

    public int KnownRecords => _state.RecordCount;

    public ICommand ShowLibraryCommand { get; }
    public ICommand ShowApprovalCommand { get; }
    public ICommand ShowImportCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand DismissStartupNoticeCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public void NavigateTo(object page, string key)
    {
        Navigate(key, page);
    }

    private void Navigate(string key, object page)
    {
        CurrentPage = page;
        CurrentPageKey = key;
    }

    private void OnReviewRequested(object? sender, WallpaperSource item)
    {
        Navigate("approval", Approval);
        _ = Approval.LoadAsync(item);
    }

    private void OnReviewCompleted(object? sender, ApprovalOutcome outcome)
    {
        Library.RefreshAfterReview();
        // Return to the library so the next item is one click away.
        Navigate("library", Library);
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        Library.RefreshSettingsBindings();
        Import.RefreshRuleState();
        Approval.RefreshToolchainState();
        _ffmpeg.Locate(_settingsService.Current);
        OnPropertyChanged(nameof(StorageBadge), nameof(StorageTone), nameof(DataLocation));
        Shell.ReportText("Settings saved. Scan again if the source folder changed.");
    }

    private void BuildStartupNotice()
    {
        var notices = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settingsService.LoadWarning))
        {
            notices.Add(_settingsService.LoadWarning!);
        }

        if (!string.IsNullOrWhiteSpace(_state.LoadWarning))
        {
            notices.Add(_state.LoadWarning!);
        }

        if (!_ffmpeg.Toolchain.HasFfmpeg)
        {
            notices.Add("FFmpeg was not found. Video wallpapers cannot be reviewed or exported until it is configured in Settings. Images and scene previews still work.");
        }

        if (!AppPaths.PortableMode)
        {
            notices.Add($"The app folder is read-only, so settings and library memory are stored in {AppPaths.DataDirectory}.");
        }

        StartupNotice = notices.Count == 0
            ? null
            : string.Join("\n\n", notices);
    }
}
