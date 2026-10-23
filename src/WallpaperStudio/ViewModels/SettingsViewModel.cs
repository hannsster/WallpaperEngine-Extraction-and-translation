using System.Diagnostics;
using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

/// <summary>
/// Settings screen. Every field writes into the portable settings.json.
/// Detection is read-only; no Steam credentials are requested or stored.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly FfmpegService _ffmpeg;
    private readonly WallpaperEngineLocator _wallpaperEngine;
    private readonly SteamCmdService _steamCmd;
    private readonly StateStore _state;
    private readonly DialogService _dialogs;
    private readonly ShellState _shell;

    private string _ffmpegStatus = string.Empty;
    private string _ffmpegTone = "muted";
    private string _wallpaperEngineStatus = string.Empty;
    private string _wallpaperEngineTone = "muted";
    private string _steamCmdStatus = string.Empty;
    private string _steamCmdTone = "muted";
    private string _saveMessage = string.Empty;
    private bool _saveMessageIsError;

    public SettingsViewModel(
        SettingsService settingsService,
        FfmpegService ffmpeg,
        WallpaperEngineLocator wallpaperEngine,
        SteamCmdService steamCmd,
        StateStore state,
        DialogService dialogs,
        ShellState shell)
    {
        _settingsService = settingsService;
        _ffmpeg = ffmpeg;
        _wallpaperEngine = wallpaperEngine;
        _steamCmd = steamCmd;
        _state = state;
        _dialogs = dialogs;
        _shell = shell;

        BrowseSourceCommand = new RelayCommand(BrowseSource);
        BrowseExportCommand = new RelayCommand(BrowseExport);
        BrowseFfmpegCommand = new RelayCommand(BrowseFfmpeg);
        BrowseWallpaperEngineCommand = new RelayCommand(BrowseWallpaperEngine);
        BrowseSteamCmdCommand = new RelayCommand(BrowseSteamCmd);
        DetectFfmpegCommand = new RelayCommand(DetectFfmpeg);
        DetectWallpaperEngineCommand = new RelayCommand(DetectWallpaperEngine);
        DetectSteamCmdCommand = new RelayCommand(DetectSteamCmd);
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        OpenDataFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(AppPaths.DataDirectory));
        OpenLogFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(AppPaths.LogDirectory));
        OpenExportFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(Settings.ExportFolder));
        OpenStateFolderCommand = new RelayCommand(() => _dialogs.ShowFileInExplorer(AppPaths.StateFile));

        RefreshToolchain();
    }

    public event EventHandler? SettingsSaved;

    public AppSettings Settings => _settingsService.Current;

    public string SourceFolder
    {
        get => Settings.SourceFolder;
        set
        {
            if (!string.Equals(Settings.SourceFolder, value, StringComparison.Ordinal))
            {
                Settings.SourceFolder = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string ExportFolder
    {
        get => Settings.ExportFolder;
        set
        {
            if (!string.Equals(Settings.ExportFolder, value, StringComparison.Ordinal))
            {
                Settings.ExportFolder = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public int JpegQuality
    {
        get => Settings.JpegQuality;
        set
        {
            var clamped = Math.Clamp(value, 1, 100);
            if (Settings.JpegQuality != clamped)
            {
                Settings.JpegQuality = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(JpegQualityText));
            }
        }
    }

    public string JpegQualityText => JpegQuality switch
    {
        >= 95 => $"{JpegQuality} · near-lossless, largest files",
        >= 85 => $"{JpegQuality} · high quality (recommended)",
        >= 70 => $"{JpegQuality} · balanced",
        _ => $"{JpegQuality} · small files"
    };

    public bool EnforceMinimumResolution
    {
        get => Settings.EnforceMinimumResolution;
        set
        {
            if (Settings.EnforceMinimumResolution != value)
            {
                Settings.EnforceMinimumResolution = value;
                OnPropertyChanged();
            }
        }
    }

    public double DefaultFrameTimestampSeconds
    {
        get => Settings.DefaultFrameTimestampSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 600);
            if (Math.Abs(Settings.DefaultFrameTimestampSeconds - clamped) > 0.001)
            {
                Settings.DefaultFrameTimestampSeconds = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefaultFrameTimestampText));
            }
        }
    }

    public string DefaultFrameTimestampText
    {
        get
        {
            var time = TimeSpan.FromSeconds(Math.Max(0, Settings.DefaultFrameTimestampSeconds));
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        }
    }

    public string FfmpegPath
    {
        get => Settings.FfmpegPath;
        set
        {
            if (!string.Equals(Settings.FfmpegPath, value, StringComparison.Ordinal))
            {
                Settings.FfmpegPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public string WallpaperEnginePath
    {
        get => Settings.WallpaperEnginePath;
        set
        {
            if (!string.Equals(Settings.WallpaperEnginePath, value, StringComparison.Ordinal))
            {
                Settings.WallpaperEnginePath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
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

    public bool SkipAlreadyExported
    {
        get => Settings.SkipAlreadyExported;
        set
        {
            if (Settings.SkipAlreadyExported != value)
            {
                Settings.SkipAlreadyExported = value;
                OnPropertyChanged();
            }
        }
    }

    public bool OpenExportFolderAfterBatch
    {
        get => Settings.OpenExportFolderAfterBatch;
        set
        {
            if (Settings.OpenExportFolderAfterBatch != value)
            {
                Settings.OpenExportFolderAfterBatch = value;
                OnPropertyChanged();
            }
        }
    }

    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        private set => SetProperty(ref _ffmpegStatus, value);
    }

    public string FfmpegTone
    {
        get => _ffmpegTone;
        private set => SetProperty(ref _ffmpegTone, value);
    }

    public string WallpaperEngineStatus
    {
        get => _wallpaperEngineStatus;
        private set => SetProperty(ref _wallpaperEngineStatus, value);
    }

    public string WallpaperEngineTone
    {
        get => _wallpaperEngineTone;
        private set => SetProperty(ref _wallpaperEngineTone, value);
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

    public string SaveMessage
    {
        get => _saveMessage;
        private set
        {
            if (SetProperty(ref _saveMessage, value))
            {
                OnPropertyChanged(nameof(HasSaveMessage));
            }
        }
    }

    public bool HasSaveMessage => !string.IsNullOrWhiteSpace(SaveMessage);

    public bool SaveMessageIsError
    {
        get => _saveMessageIsError;
        private set => SetProperty(ref _saveMessageIsError, value);
    }

    public string DataDirectory => AppPaths.DataDirectory;

    public string SettingsFile => AppPaths.SettingsFile;

    public string StateFile => AppPaths.StateFile;

    public string PortableModeText => AppPaths.PortableMode
        ? "Portable · data is stored next to the executable"
        : "Fallback · the app folder is read-only, so data lives in %LOCALAPPDATA%";

    public string PortableTone => AppPaths.PortableMode ? "success" : "warning";

    public int StateRecordCount => _state.RecordCount;

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseExportCommand { get; }
    public ICommand BrowseFfmpegCommand { get; }
    public ICommand BrowseWallpaperEngineCommand { get; }
    public ICommand BrowseSteamCmdCommand { get; }
    public ICommand DetectFfmpegCommand { get; }
    public ICommand DetectWallpaperEngineCommand { get; }
    public ICommand DetectSteamCmdCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenExportFolderCommand { get; }
    public ICommand OpenStateFolderCommand { get; }

    public void RefreshAll()
    {
        OnPropertyChanged(
            nameof(Settings), nameof(SourceFolder), nameof(ExportFolder), nameof(JpegQuality), nameof(JpegQualityText),
            nameof(EnforceMinimumResolution), nameof(DefaultFrameTimestampSeconds), nameof(DefaultFrameTimestampText),
            nameof(FfmpegPath), nameof(WallpaperEnginePath), nameof(SteamCmdPath), nameof(SteamUsername), nameof(SkipAlreadyExported),
            nameof(OpenExportFolderAfterBatch), nameof(DataDirectory), nameof(SettingsFile), nameof(StateFile),
            nameof(PortableModeText), nameof(PortableTone), nameof(StateRecordCount));
        RefreshToolchain();
    }

    private void RefreshToolchain()
    {
        var toolchain = _ffmpeg.Locate(Settings);
        if (toolchain.HasFfmpeg)
        {
            FfmpegStatus = toolchain.StatusText + " · " + toolchain.FfmpegPath;
            FfmpegTone = "success";
        }
        else
        {
            FfmpegStatus = "FFmpeg not found. Video frame extraction is disabled until a path is set.";
            FfmpegTone = "warning";
        }

        var executable = _wallpaperEngine.Locate(Settings, out var status);
        if (executable is not null)
        {
            WallpaperEngineStatus = "Detected · " + executable;
            WallpaperEngineTone = "success";
        }
        else
        {
            WallpaperEngineStatus = status;
            WallpaperEngineTone = "muted";
        }

        var steamCmd = _steamCmd.Locate(Settings, out var steamStatus);
        if (steamCmd is not null)
        {
            SteamCmdStatus = "Detected · " + steamCmd;
            SteamCmdTone = "success";
        }
        else
        {
            SteamCmdStatus = steamStatus;
            SteamCmdTone = "muted";
        }
    }

    private void BrowseSource()
    {
        var chosen = _dialogs.PickFolder("Choose the wallpaper source folder", SourceFolder);
        if (chosen is not null)
        {
            SourceFolder = chosen;
        }
    }

    private void BrowseExport()
    {
        var chosen = _dialogs.PickFolder("Choose where exported JPEGs should go", ExportFolder);
        if (chosen is not null)
        {
            ExportFolder = chosen;
        }
    }

    private void BrowseFfmpeg()
    {
        var chosen = _dialogs.PickExecutable("Choose ffmpeg.exe", FfmpegPath);
        if (chosen is not null)
        {
            FfmpegPath = chosen;
            RefreshToolchain();
        }
    }

    private void BrowseWallpaperEngine()
    {
        var chosen = _dialogs.PickExecutable("Choose wallpaper64.exe or wallpaper32.exe", WallpaperEnginePath);
        if (chosen is not null)
        {
            WallpaperEnginePath = chosen;
            RefreshToolchain();
        }
    }

    private void BrowseSteamCmd()
    {
        var chosen = _dialogs.PickExecutable("Choose steamcmd.exe", SteamCmdPath);
        if (chosen is not null)
        {
            SteamCmdPath = chosen;
            RefreshToolchain();
        }
    }

    private void DetectFfmpeg()
    {
        var toolchain = _ffmpeg.Locate(Settings);
        if (toolchain.HasFfmpeg)
        {
            FfmpegPath = toolchain.FfmpegPath!;
            FfmpegStatus = toolchain.StatusText + " · " + toolchain.FfmpegPath;
            FfmpegTone = "success";
            _shell.ReportText("FFmpeg detected at " + toolchain.FfmpegPath);
        }
        else
        {
            FfmpegStatus = "FFmpeg was not found beside the app, in an 'ffmpeg' subfolder, on PATH, or at the configured path.";
            FfmpegTone = "warning";
            _dialogs.ShowInfo("FFmpeg not found",
                "Place ffmpeg.exe in an 'ffmpeg' subfolder next to WallpaperStudio.exe, add it to PATH, or choose its folder here.\n\n" +
                "FFmpeg is a separate project under its own license; Wallpaper Studio does not bundle it.");
        }
    }

    private void DetectWallpaperEngine()
    {
        var executable = _wallpaperEngine.Locate(Settings, out var status);
        if (executable is null)
        {
            WallpaperEngineStatus = status;
            WallpaperEngineTone = "muted";
            _dialogs.ShowInfo("Wallpaper Engine not found",
                "Wallpaper Studio looks for the normal installation via Steam and Windows uninstall entries. " +
                "Set wallpaper64.exe manually if your installation is elsewhere.\n\n" +
                "Only launching is supported; importing and Workshop publishing stay manual.");
            return;
        }

        WallpaperEnginePath = executable;
        WallpaperEngineStatus = "Detected · " + executable;
        WallpaperEngineTone = "success";
        _shell.ReportText("Wallpaper Engine detected at " + executable);
    }

    private void DetectSteamCmd()
    {
        var executable = _steamCmd.Locate(Settings, out var status);
        if (executable is null)
        {
            SteamCmdStatus = status;
            SteamCmdTone = "warning";
            _dialogs.ShowInfo("SteamCMD not found",
                "Wallpaper Studio looks beside the app, in a 'steamcmd' subfolder, on PATH, and at C:\\steamcmd.\n\n" +
                "SteamCMD is Valve's tool and is distributed by Valve; Wallpaper Studio does not bundle or download it. " +
                "Publishing to the Workshop needs it.");
            return;
        }

        SteamCmdPath = executable;
        SteamCmdStatus = "Detected · " + executable;
        SteamCmdTone = "success";
        _shell.ReportText("SteamCMD detected at " + executable);
    }

    private void Save()
    {
        SaveMessage = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(SourceFolder) &&
                !Directory.Exists(PathHelper.ToExtendedPath(SourceFolder)))
            {
                throw new InvalidOperationException("The source folder does not exist. Choose an existing folder.");
            }

            if (!string.IsNullOrWhiteSpace(ExportFolder))
            {
                Directory.CreateDirectory(PathHelper.ToExtendedPath(ExportFolder));
            }

            Settings.Normalize();
            _settingsService.Save();
            RefreshToolchain();
            SaveMessageIsError = false;
            SaveMessage = "Settings saved to " + AppPaths.SettingsFile;
            _shell.ReportText("Settings saved.");
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SaveMessageIsError = true;
            SaveMessage = ex.Message;
            _dialogs.ShowError("Could not save settings", ex.Message, ex);
        }
    }

    private void Reset()
    {
        if (!_dialogs.Confirm("Reset settings?",
                "This resets export quality, tool paths and behaviour switches to their defaults. Your source folder is kept. Library memory and exported files are not touched."))
        {
            return;
        }

        var source = Settings.SourceFolder;
        Settings.JpegQuality = 90;
        Settings.EnforceMinimumResolution = true;
        Settings.DefaultFrameTimestampSeconds = 1.0;
        Settings.FfmpegPath = string.Empty;
        Settings.WallpaperEnginePath = string.Empty;
        Settings.SteamCmdPath = string.Empty;
        Settings.ExportFolder = AppPaths.DefaultExportDirectory;
        Settings.SkipAlreadyExported = true;
        Settings.OpenExportFolderAfterBatch = false;
        Settings.SourceFolder = source;

        try
        {
            _settingsService.Save();
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            SaveMessageIsError = false;
            SaveMessage = "Defaults restored.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not reset settings", ex.Message, ex);
        }

        RefreshAll();
    }
}
