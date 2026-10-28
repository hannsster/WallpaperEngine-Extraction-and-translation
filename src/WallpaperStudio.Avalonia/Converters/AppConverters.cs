using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WallpaperStudio.Infrastructure;

namespace WallpaperStudio.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>
/// True when the bound value is null or a blank string. Pass "invert" to test
/// for the opposite. Used for placeholder text and optional chips.
/// </summary>
public sealed class IsNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string text && string.IsNullOrWhiteSpace(text));
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return isNull ^ invert;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

public sealed class CountToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int number ? number : 0;
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (count > 0) ^ invert;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// Maps the small set of status tone names used by models (success, warning,
/// danger, accent, neutral, muted) to themed brushes. The parameter selects the
/// surface: "background", "foreground" or "border".
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    private static readonly Color Success = Color.FromRgb(0x63, 0xD6, 0xA6);
    private static readonly Color Warning = Color.FromRgb(0xF5, 0xC4, 0x6B);
    private static readonly Color Danger = Color.FromRgb(0xFF, 0x80, 0x95);
    private static readonly Color Accent = Color.FromRgb(0x6C, 0x9B, 0xFF);
    private static readonly Color Neutral = Color.FromRgb(0x9F, 0xB0, 0xC7);
    private static readonly Color Muted = Color.FromRgb(0x6F, 0x7E, 0x96);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value as string ?? "neutral";
        var color = tone switch
        {
            "success" => Success,
            "warning" => Warning,
            "danger" => Danger,
            "accent" => Accent,
            "muted" => Muted,
            _ => Neutral
        };

        return (parameter as string) switch
        {
            "foreground" => new SolidColorBrush(color),
            "border" => new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
            _ => new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>
/// Decodes the cached JPEG bytes exposed by the view models into a bitmap the
/// Avalonia renderer can draw. Returns null for an empty or invalid image.
/// </summary>
public sealed class JpegImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;
}
