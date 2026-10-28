using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace WallpaperStudio.Views.Dialogs;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error,
    Question
}

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(false);
            }
        };
    }

    public MessageDialog(
        string title,
        string message,
        MessageDialogKind kind,
        string primaryText = "OK",
        string? secondaryText = null)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;

        KindIcon.Kind = kind switch
        {
            MessageDialogKind.Warning => MaterialIconKind.Alert,
            MessageDialogKind.Error => MaterialIconKind.AlertCircle,
            MessageDialogKind.Question => MaterialIconKind.Information,
            _ => MaterialIconKind.Information
        };

        var tone = kind switch
        {
            MessageDialogKind.Warning => Color.FromRgb(0xF5, 0xC4, 0x6B),
            MessageDialogKind.Error => Color.FromRgb(0xFF, 0x80, 0x95),
            _ => Color.FromRgb(0x6C, 0x9B, 0xFF)
        };

        var brush = new SolidColorBrush(tone);
        KindIcon.Foreground = brush;
        IconPlate.Background = new SolidColorBrush(Color.FromArgb(0x2E, tone.R, tone.G, tone.B));

        PrimaryButton.Content = primaryText;
        PrimaryButton.Click += (_, _) => Finish(true);

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.IsVisible = true;
            SecondaryButton.Click += (_, _) => Finish(false);
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Finish(bool result)
    {
        if (IsVisible)
        {
            Close(result);
        }
    }
}
