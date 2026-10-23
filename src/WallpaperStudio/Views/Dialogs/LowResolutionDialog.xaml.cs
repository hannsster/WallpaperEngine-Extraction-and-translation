using System.Windows;
using System.Windows.Input;
using WallpaperStudio.Models;
using WallpaperStudio.Services;

namespace WallpaperStudio.Views.Dialogs;

public partial class LowResolutionDialog : Window
{
    public LowResolutionDialog(ImportItem item)
    {
        InitializeComponent();
        DataContext = item;
        Choice = LowResolutionChoice.Toss;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Choice = LowResolutionChoice.CancelBatch;
                Close();
            }
        };
    }

    public LowResolutionChoice Choice { get; private set; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LowResolutionChoice.ApproveAnyway;
        Close();
    }

    private void TossButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LowResolutionChoice.Toss;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = LowResolutionChoice.CancelBatch;
        Close();
    }
}
