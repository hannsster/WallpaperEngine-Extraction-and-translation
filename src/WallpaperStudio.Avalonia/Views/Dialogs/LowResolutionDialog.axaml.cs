using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WallpaperStudio.Models;
using WallpaperStudio.Services;

namespace WallpaperStudio.Views.Dialogs;

public partial class LowResolutionDialog : Window
{
    public LowResolutionDialog()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(LowResolutionChoice.CancelBatch);
            }
        };
    }

    public LowResolutionDialog(ImportItem item)
        : this()
    {
        DataContext = item;
    }

    public LowResolutionChoice Choice { get; private set; } = LowResolutionChoice.CancelBatch;

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Finish(LowResolutionChoice.CancelBatch);

    private void TossButton_Click(object? sender, RoutedEventArgs e)
        => Finish(LowResolutionChoice.Toss);

    private void ApproveButton_Click(object? sender, RoutedEventArgs e)
        => Finish(LowResolutionChoice.ApproveAnyway);

    private void Finish(LowResolutionChoice choice)
    {
        Choice = choice;
        if (IsVisible)
        {
            Close(choice);
        }
    }
}
