namespace WallpaperStudio.Services;

public enum LowResolutionChoice
{
    ApproveAnyway,
    Toss,
    CancelBatch
}

/// <summary>
/// Everything the view models need from the desktop shell: pickers, message
/// boxes, the file manager and the clipboard. Each UI head implements it, so the
/// view models never reference WPF or Avalonia types.
///
/// Pickers and prompts are asynchronous because Avalonia has no blocking modal
/// dialogs. The WPF implementation still shows its native dialogs synchronously
/// and simply returns a completed task.
/// </summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    Task<IReadOnlyList<string>> PickImagesAsync(string title, bool multiselect = true);

    Task<IReadOnlyList<string>> PickVideosAsync(string title, bool multiselect = true);

    Task<string?> PickExecutableAsync(string title, string? initialPath = null);

    Task<bool> ConfirmAsync(string title, string message);

    Task ShowInfoAsync(string title, string message);

    Task ShowWarningAsync(string title, string message);

    Task ShowErrorAsync(string title, string message, Exception? exception = null);

    Task<LowResolutionChoice> AskLowResolutionAsync(ImportItem item);

    void OpenFolder(string path);

    void ShowFileInExplorer(string path);

    void CopyText(string text);
}
