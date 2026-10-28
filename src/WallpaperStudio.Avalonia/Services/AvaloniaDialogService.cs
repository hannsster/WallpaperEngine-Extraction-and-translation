using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WallpaperStudio.Views.Dialogs;

namespace WallpaperStudio.Services;

/// <summary>
/// Avalonia implementation of the desktop shell the view models talk to. Pickers
/// and prompts are asynchronous because Avalonia has no blocking modal dialogs.
/// </summary>
public sealed class AvaloniaDialogService(Func<Window?> owner) : IDialogService
{
    private Window? Owner => owner();

    private IStorageProvider? Storage => Owner?.StorageProvider;

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            options.SuggestedStartLocation = await TryGetFolderAsync(storage, initialDirectory);
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickImagesAsync(string title, bool multiselect = true)
        => await PickFilesAsync(
            title,
            multiselect,
            new FilePickerFileType("Images")
            {
                Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.gif"]
            },
            new FilePickerFileType("All files") { Patterns = ["*"] });

    public async Task<IReadOnlyList<string>> PickVideosAsync(string title, bool multiselect = true)
        => await PickFilesAsync(
            title,
            multiselect,
            new FilePickerFileType("Videos")
            {
                Patterns = ["*.mp4", "*.webm", "*.mkv", "*.avi", "*.mov", "*.m4v"]
            },
            new FilePickerFileType("All files") { Patterns = ["*"] });

    public async Task<string?> PickExecutableAsync(string title, string? initialPath = null)
    {
        if (Storage is not { } storage)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Programs")
                {
                    Patterns = PlatformInfo.IsWindows ? ["*.exe"] : ["*"]
                },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            options.SuggestedStartLocation = await TryGetFolderAsync(storage, initialPath);
        }

        var files = await storage.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (Owner is not { } ownerWindow)
        {
            return false;
        }

        var dialog = new MessageDialog(
            title, message, MessageDialogKind.Question, "Yes", "No");
        return await dialog.ShowDialog<bool>(ownerWindow);
    }

    public Task ShowInfoAsync(string title, string message)
        => ShowMessageAsync(title, message, MessageDialogKind.Info);

    public Task ShowWarningAsync(string title, string message)
        => ShowMessageAsync(title, message, MessageDialogKind.Warning);

    public Task ShowErrorAsync(string title, string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            Logger.Error($"{title}: {message}", exception);
        }
        else
        {
            Logger.Warn($"{title}: {message}");
        }

        var suffix = Logger.LogFilePath is null
            ? string.Empty
            : $"\n\nTechnical details were written to:\n{Logger.LogFilePath}";
        return ShowMessageAsync(title, message + suffix, MessageDialogKind.Error);
    }

    public async Task<LowResolutionChoice> AskLowResolutionAsync(ImportItem item)
    {
        if (Owner is not { } ownerWindow)
        {
            return LowResolutionChoice.CancelBatch;
        }

        var dialog = new LowResolutionDialog(item);
        return await dialog.ShowDialog<LowResolutionChoice>(ownerWindow);
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            _ = ShowWarningAsync("Folder not found", path);
            return;
        }

        if (!PlatformShell.TryOpenFolder(path))
        {
            _ = ShowErrorAsync("Could not open folder", path);
        }
    }

    public void ShowFileInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            _ = ShowWarningAsync("File not found", path);
            return;
        }

        if (!PlatformShell.TryRevealFile(path))
        {
            _ = ShowErrorAsync("Could not open file location", path);
        }
    }

    public void CopyText(string text)
    {
        try
        {
            var clipboard = Owner?.Clipboard;
            if (clipboard is not null)
            {
                _ = clipboard.SetTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("Could not copy to clipboard", "The clipboard is unavailable right now.", ex);
        }
    }

    private async Task ShowMessageAsync(string title, string message, MessageDialogKind kind)
    {
        if (Owner is not { } ownerWindow)
        {
            Logger.Warn($"{title}: {message}");
            return;
        }

        var dialog = new MessageDialog(title, message, kind);
        await dialog.ShowDialog(ownerWindow);
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(
        string title,
        bool multiselect,
        params FilePickerFileType[] filters)
    {
        if (Storage is not { } storage)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiselect,
            FileTypeFilter = filters
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
    }

    private static async Task<IStorageFolder?> TryGetFolderAsync(IStorageProvider storage, string path)
    {
        try
        {
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            return await storage.TryGetFolderFromPathAsync(directory);
        }
        catch
        {
            return null;
        }
    }
}
