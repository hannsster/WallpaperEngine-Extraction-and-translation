using System.Windows;
using System.Windows.Threading;
using WallpaperStudio.Services;
using WallpaperStudio.ViewModels;

namespace WallpaperStudio;

public partial class App : Application
{
    private StateStore? _state;
    private SettingsService? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Initialize();
        Logger.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("An unobserved background task failed.", args.Exception);
            args.SetObserved();
        };

        try
        {
            Compose();
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failed.", ex);
            MessageBox.Show(
                "Wallpaper Studio could not start.\n\n" + ex.Message +
                (Logger.LogFilePath is null ? string.Empty : "\n\nTechnical details:\n" + Logger.LogFilePath),
                "Wallpaper Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Compose()
    {
        _settings = new SettingsService();
        _settings.Load();

        _state = new StateStore();
        _state.Load();

        var images = new ImageProbeService();
        var ffmpeg = new FfmpegService();
        ffmpeg.Locate(_settings.Current);

        var thumbnails = new ThumbnailService();
        var pkgPreviews = new PkgPreviewExtractor();
        var scanner = new SourceScanner(images, ffmpeg);
        var hashing = new HashingService();
        var exports = new ExportService(_state);
        var wallpaperEngine = new WallpaperEngineLocator();
        var imports = new ImportService(images);
        var steamCmd = new SteamCmdService();
        steamCmd.Locate(_settings.Current, out _);
        var projectBuilder = new WallpaperProjectBuilder(ffmpeg);
        var dialogs = new DialogService();
        var shell = new ShellState();

        var library = new LibraryViewModel(
            _settings, _state, scanner, hashing, thumbnails, exports, ffmpeg, pkgPreviews, dialogs, shell);

        var approval = new ApprovalViewModel(
            ffmpeg, thumbnails, pkgPreviews, _state, _settings, dialogs, shell);

        var import = new ImportViewModel(
            imports, _settings, wallpaperEngine, thumbnails, dialogs, shell);

        var publish = new PublishViewModel(
            _settings, _state, steamCmd, projectBuilder, thumbnails, dialogs, shell);

        var settings = new SettingsViewModel(
            _settings, ffmpeg, wallpaperEngine, steamCmd, _state, dialogs, shell);

        var main = new MainViewModel(
            library, approval, import, publish, settings, shell, _settings, _state, ffmpeg, dialogs);

        var window = new MainWindow { DataContext = main };
        dialogs.Owner = window;
        MainWindow = window;
        window.Show();

        Logger.Info("Main window shown.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _state?.Save();
            _settings?.Save();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not save settings/state during exit.", ex);
        }

        Logger.Info("Application exiting.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "Something went wrong, but the app is still running.\n\n" + e.Exception.Message +
            (Logger.LogFilePath is null ? string.Empty : "\n\nDetails were written to:\n" + Logger.LogFilePath),
            "Wallpaper Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
