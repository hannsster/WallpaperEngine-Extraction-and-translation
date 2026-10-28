using System.Windows;
using Microsoft.Win32;
using WallpaperStudio.Services;
using WallpaperStudio.Views.Dialogs;

namespace WallpaperStudio.Services;

/// <summary>
/// WPF implementation of the desktop shell the view models talk to. WPF dialogs
/// are modal and blocking, so every asynchronous member completes immediately
/// after the native dialog closes.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public Window? Owner { get; set; }

    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
        => Task.FromResult(PickFolder(title, initialDirectory));

    public Task<IReadOnlyList<string>> PickImagesAsync(string title, bool multiselect = true)
        => Task.FromResult(PickFiles(title, multiselect, "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|JPEG|*.jpg;*.jpeg|PNG|*.png|All files|*.*"));

    public Task<IReadOnlyList<string>> PickVideosAsync(string title, bool multiselect = true)
        => Task.FromResult(PickFiles(title, multiselect, "Videos|*.mp4;*.webm;*.mkv;*.avi;*.mov;*.m4v|MP4|*.mp4|All files|*.*"));

    public Task<string?> PickExecutableAsync(string title, string? initialPath = null)
        => Task.FromResult(PickExecutable(title, initialPath));

    public Task<bool> ConfirmAsync(string title, string message)
        => Task.FromResult(ShowMessage(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task ShowInfoAsync(string title, string message)
    {
        ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }

    public Task ShowWarningAsync(string title, string message)
    {
        ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Warning);
        return Task.CompletedTask;
    }

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
        ShowMessage(title, message + suffix, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<LowResolutionChoice> AskLowResolutionAsync(ImportItem item)
    {
        var dialog = new LowResolutionDialog(item) { Owner = Owner };
        dialog.ShowDialog();
        return Task.FromResult(dialog.Choice);
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
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("Could not copy to clipboard", "The clipboard is unavailable right now.", ex);
        }
    }

    private string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    private string? PickExecutable(string title, string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = "Programs|*.exe;*.sh|All files|*.*"
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                var directory = Directory.Exists(initialPath) ? initialPath : Path.GetDirectoryName(initialPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    dialog.InitialDirectory = directory;
                }
            }
            catch
            {
                // Ignore an invalid starting path.
            }
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private IReadOnlyList<string> PickFiles(string title, bool multiselect, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = multiselect,
            CheckFileExists = true,
            Filter = filter
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    private MessageBoxResult ShowMessage(string title, string message, MessageBoxButton buttons, MessageBoxImage image)
        => Owner is null
            ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(Owner, message, title, buttons, image);
}
