using System.Windows.Input;

namespace WallpaperStudio.ViewModels;

public sealed class ShellState : ObservableObject
{
    private string _statusMessage = "Ready. Choose a wallpaper source folder to begin.";
    private string _operationTitle = string.Empty;
    private bool _isBusy;
    private bool _isIndeterminate;
    private double _progressValue;
    private CancellationTokenSource? _operationCts;

    public ShellState()
    {
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string OperationTitle
    {
        get => _operationTitle;
        private set => SetProperty(ref _operationTitle, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>True once the current operation has been asked to cancel.</summary>
    public bool IsCancellationRequested => _operationCts?.IsCancellationRequested == true;

    public bool IsProgressIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public RelayCommand CancelCommand { get; }

    public CancellationToken Begin(string title, bool indeterminate = false)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        OperationTitle = title;
        StatusMessage = title;
        IsBusy = true;
        IsProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        return _operationCts.Token;
    }

    public void Report(double fraction, string? message = null)
    {
        ProgressValue = Math.Clamp(fraction * 100, 0, 100);
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message;
        }
    }

    public void ReportText(string message)
    {
        StatusMessage = message;
    }

    public void Complete(string message)
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        ProgressValue = 100;
        StatusMessage = message;
        OperationTitle = string.Empty;
    }

    public void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        StatusMessage = "Cancelling…";
        _operationCts?.Cancel();
    }

    public void ResetOperation()
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        OperationTitle = string.Empty;
    }
}
