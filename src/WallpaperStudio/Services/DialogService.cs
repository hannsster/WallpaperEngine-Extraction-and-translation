using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WallpaperStudio.Views.Dialogs;

namespace WallpaperStudio.Services;

public enum LowResolutionChoice
{
    ApproveAnyway,
    Toss,
    CancelBatch
}

public sealed class DialogService
{
    public Window? Owner { get; set; }

    public string? PickFolder(string title, string? initialDirectory = null)
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

    public IReadOnlyList<string> PickImages(string title, bool multiselect = true)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = multiselect,
            CheckFileExists = true,
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|JPEG|*.jpg;*.jpeg|PNG|*.png|All files|*.*"
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? PickExecutable(string title, string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            Filter = "Executables|*.exe|All files|*.*"
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

    public bool Confirm(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string title, string message)
        => ShowMessage(title, message, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string title, string message, Exception? exception = null)
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
    }

    private MessageBoxResult ShowMessage(string title, string message, MessageBoxButton buttons, MessageBoxImage image)
        => Owner is null
            ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(Owner, message, title, buttons, image);

    public LowResolutionChoice AskLowResolution(ImportItem item)
    {
        var dialog = new LowResolutionDialog(item) { Owner = Owner };
        dialog.ShowDialog();
        return dialog.Choice;
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                ShowWarning("Folder not found", path);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not open folder", path, ex);
        }
    }

    public void ShowFileInExplorer(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                ShowWarning("File not found", path);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not open file location", path, ex);
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
            ShowError("Could not copy to clipboard", "The clipboard is unavailable right now.", ex);
        }
    }
}
