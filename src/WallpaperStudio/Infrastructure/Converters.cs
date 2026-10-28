using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperStudio.Infrastructure;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Maps the small set of status tone names used by models (success, warning,
/// danger, accent, neutral, muted) to themed brushes. Parameter selects the
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

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
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

        var surface = parameter as string;
        return surface switch
        {
            "foreground" => new SolidColorBrush(color),
            "border" => new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
            _ => new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string text && string.IsNullOrWhiteSpace(text));
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return isNull ^ invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int number ? number : 0;
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (count > 0) ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch
        {
            return path;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class HumanSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is long bytes ? PathHelper.HumanSize(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class TubeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var seconds = value is double d ? d : 0;
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Decodes the JPEG bytes a view model exposes into a frozen bitmap the UI can
/// bind to. Thumbnails are already cached as small JPEGs by the core service.
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
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
