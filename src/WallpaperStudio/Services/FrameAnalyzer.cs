using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>
/// Measurements used to decide whether a frame is worth showing, exporting or
/// sending to a reverse-image search. A blank opening second, a fade-in, or a
/// scene that failed to compose all look "weak" here.
/// </summary>
public readonly record struct FrameQuality(
    bool IsAllBlack,
    double Score,
    double MeanLuminance,
    double DarkFraction,
    double EdgeDensity,
    double Colourfulness,
    int DistinctColours)
{
    /// <summary>Frames below this score are treated as "not discernible enough".</summary>
    public const double WeakThreshold = 14.0;

    public bool IsWeak => IsAllBlack || Score < WeakThreshold;

    public string Tone => IsAllBlack ? "danger" : IsWeak ? "warning" : Score >= 45 ? "success" : "neutral";

    public string Summary => IsAllBlack
        ? "almost entirely black"
        : IsWeak
            ? "very little visible detail"
            : Score >= 60
                ? "rich, detailed frame"
                : "usable frame";

    public string Detail =>
        $"brightness {MeanLuminance:0}, detail {EdgeDensity:0.0}, colour {Colourfulness:P0}, {DistinctColours:N0} tones";
}

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

    private const int QualityGrid = 64;

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

    /// <summary>
    /// Measures brightness spread, local contrast, colour and palette richness.
    /// Cheap enough to run on every candidate frame (4096 sampled pixels).
    /// </summary>
    public static FrameQuality Analyze(SKBitmap? bitmap)
    {
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return new FrameQuality(true, 0, 0, 1, 0, 0, 0);
        }

        var size = QualityGrid * QualityGrid;
        var luminance = new double[size];
        var colours = new HashSet<int>(size);

        var sum = 0d;
        var dark = 0;
        var chroma = 0d;
        var min = 255d;
        var max = 0d;

        for (var iy = 0; iy < QualityGrid; iy++)
        {
            var y = (int)((iy + 0.5) * bitmap.Height / QualityGrid);
            if (y >= bitmap.Height)
            {
                y = bitmap.Height - 1;
            }

            for (var ix = 0; ix < QualityGrid; ix++)
            {
                var x = (int)((ix + 0.5) * bitmap.Width / QualityGrid);
                if (x >= bitmap.Width)
                {
                    x = bitmap.Width - 1;
                }

                var color = bitmap.GetPixel(x, y);
                var value = (0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue);
                luminance[(iy * QualityGrid) + ix] = value;
                sum += value;

                if (value < NearBlackLuminance)
                {
                    dark++;
                }

                var high = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
                var low = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
                chroma += (high - low) / 255d;

                min = Math.Min(min, value);
                max = Math.Max(max, value);

                colours.Add(((color.Red >> 4) << 8) | ((color.Green >> 4) << 4) | (color.Blue >> 4));
            }
        }

        var mean = sum / size;
        var variance = 0d;
        for (var i = 0; i < size; i++)
        {
            var delta = luminance[i] - mean;
            variance += delta * delta;
        }

        var stdDev = Math.Sqrt(variance / size);

        var edgeSum = 0d;
        var edgeCount = 0;
        for (var iy = 0; iy < QualityGrid; iy++)
        {
            for (var ix = 0; ix < QualityGrid; ix++)
            {
                var index = (iy * QualityGrid) + ix;
                if (ix + 1 < QualityGrid)
                {
                    edgeSum += Math.Abs(luminance[index] - luminance[index + 1]);
                    edgeCount++;
                }

                if (iy + 1 < QualityGrid)
                {
                    edgeSum += Math.Abs(luminance[index] - luminance[index + QualityGrid]);
                    edgeCount++;
                }
            }
        }

        var edge = edgeCount > 0 ? edgeSum / edgeCount : 0;

        var stdScore = Math.Clamp(stdDev / 56d, 0, 1);
        var edgeScore = Math.Clamp(edge / 16d, 0, 1);
        var colourScore = Math.Clamp((chroma / size) / 0.38d, 0, 1);
        var diversityScore = Math.Clamp((colours.Count - 1) / 220d, 0, 1);

        var score = 100 * ((0.34 * stdScore) + (0.34 * edgeScore) + (0.17 * colourScore) + (0.15 * diversityScore));
        var allBlack = max < AbsoluteMaxLuminance || (double)dark / size >= NearBlackRatio;

        return new FrameQuality(
            allBlack,
            Math.Round(score, 1),
            Math.Round(mean, 1),
            Math.Round((double)dark / size, 4),
            Math.Round(edge, 2),
            Math.Round(chroma / size, 4),
            colours.Count);
    }

    public static FrameQuality AnalyzeFile(string path)
    {
        try
        {
            using var bitmap = SkiaImage.DecodeOriented(path);
            return Analyze(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not analyse frame '{path}'.", ex);
            return new FrameQuality(true, 0, 0, 1, 0, 0, 0);
        }
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
