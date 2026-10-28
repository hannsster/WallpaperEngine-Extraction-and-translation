using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WallpaperStudio.Infrastructure;
using WallpaperStudio.Services;
using WallpaperStudio.Services.ReverseSearch;
using WallpaperStudio.ViewModels;
using WallpaperStudio.Views;
using WallpaperStudio.Views.Dialogs;

namespace WallpaperStudio;

public partial class App : Application
{
    private StateStore? _state;
    private SettingsService? _settings;
    private Window? _window;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.Initialize();
        Logger.Initialize();

        UiThread.Initialize(PostToUi, () => Dispatcher.UIThread.CheckAccess());

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception.", args.Exception);
            args.Handled = true;
            ShowStartupProblem(
                "Something went wrong, but the app is still running.\n\n" + args.Exception.Message +
                DetailSuffix());
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("An unobserved background task failed.", args.Exception);
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                Compose(desktop);
            }
            catch (Exception ex)
            {
                Logger.Error("Startup failed.", ex);
                ShowStartupProblem(
                    "Wallpaper Studio could not start.\n\n" + ex.Message + DetailSuffix());
                desktop.Shutdown(1);
            }

            desktop.ShutdownRequested += (_, _) => SaveAll();
            desktop.Exit += (_, _) => SaveAll();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Compose(IClassicDesktopStyleApplicationLifetime desktop)
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
        var sceneFrames = new SceneFrameExtractor();
        var reverse = new ReverseSearchService(_settings, thumbnails);
        var scanner = new SourceScanner(images, ffmpeg, sceneFrames);
        var hashing = new HashingService();
        var exports = new ExportService(_state);
        var wallpaperEngine = new WallpaperEngineLocator();
        var imports = new ImportService(images);
        var steamCmd = new SteamCmdService();
        steamCmd.Locate(_settings.Current, out _);
        var projectBuilder = new WallpaperProjectBuilder(ffmpeg);
        var dialogs = new AvaloniaDialogService(() => _window);
        var shell = new ShellState();

        var library = new LibraryViewModel(
            _settings, _state, scanner, hashing, thumbnails, exports, ffmpeg, pkgPreviews, sceneFrames, reverse, dialogs, shell);

        var approval = new ApprovalViewModel(
            ffmpeg, thumbnails, pkgPreviews, sceneFrames, reverse, _state, _settings, dialogs, shell);

        var import = new ImportViewModel(
            imports, _settings, wallpaperEngine, thumbnails, dialogs, shell);

        var publish = new PublishViewModel(
            _settings, _state, steamCmd, projectBuilder, thumbnails, dialogs, shell);

        var settings = new SettingsViewModel(
            _settings, ffmpeg, wallpaperEngine, steamCmd, _state, reverse, dialogs, shell);

        var main = new MainViewModel(
            library, approval, import, publish, settings, shell, _settings, _state, ffmpeg, dialogs);

        _window = new MainWindow { DataContext = main };
        desktop.MainWindow = _window;
        _window.Show();

        Logger.Info("Main window shown.");
    }

    private void SaveAll()
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
    }

    private static Task PostToUi(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    private static string DetailSuffix()
        => Logger.LogFilePath is null
            ? string.Empty
            : "\n\nTechnical details were written to:\n" + Logger.LogFilePath;

    private void ShowStartupProblem(string message)
    {
        try
        {
            var dialog = new MessageDialog("Wallpaper Studio", message, MessageDialogKind.Warning);
            if (_window is not null)
            {
                _ = dialog.ShowDialog(_window);
            }
            else
            {
                dialog.Show();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not show the startup message.", ex);
        }
    }
}
