using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>
/// Detects frames that are effectively blank (almost entirely black). Such
/// frames are never auto-approved, because they usually mean a scene could not
/// be composed correctly (a missing texture, an effects-only layer, or an
/// unsupported shader). Thresholds are deliberately conservative so a genuinely
/// dark wallpaper is not misclassified.
/// </summary>
public static class FrameAnalyzer
{
    private const int SampleGrid = 48;
    private const double NearBlackLuminance = 20.0;
    private const double NearBlackRatio = 0.995;
    private const double AbsoluteMaxLuminance = 22.0;

    public static bool IsAllBlack(SKBitmap? bitmap) => IsAllBlack(bitmap, out _);

    public static bool IsAllBlack(SKBitmap? bitmap, out double maxLuminance)
    {
        maxLuminance = 0;
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return true;
        }

        var nearBlack = 0;
        var total = 0;
        for (var iy = 0; iy < SampleGrid; iy++)
        {
            var y = (int)((iy + 0.5) * bitmap.Height / SampleGrid);
            if (y >= bitmap.Height)
            {
                y = bitmap.Height - 1;
            }

            for (var ix = 0; ix < SampleGrid; ix++)
            {
                var x = (int)((ix + 0.5) * bitmap.Width / SampleGrid);
                if (x >= bitmap.Width)
                {
                    x = bitmap.Width - 1;
                }

                var color = bitmap.GetPixel(x, y);
                var luminance = ((0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue)) * (color.Alpha / 255.0);
                if (luminance > maxLuminance)
                {
                    maxLuminance = luminance;
                }

                if (luminance < NearBlackLuminance)
                {
                    nearBlack++;
                }

                total++;
            }
        }

        if (total == 0)
        {
            return true;
        }

        var darkFraction = (double)nearBlack / total;
        return maxLuminance < AbsoluteMaxLuminance || darkFraction >= NearBlackRatio;
    }

    /// <summary>Convenience overload that analyses an image file on disk.</summary>
    public static bool IsAllBlackFile(string path, out double maxLuminance)
    {
        maxLuminance = 0;
        try
        {
            using var bitmap = SkiaImage.DecodeOriented(path);
            return IsAllBlack(bitmap, out maxLuminance);
        }
        catch
        {
            return true;
        }
    }
}
