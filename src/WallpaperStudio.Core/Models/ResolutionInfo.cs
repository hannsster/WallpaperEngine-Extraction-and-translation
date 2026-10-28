namespace WallpaperStudio.Models;

public readonly record struct ResolutionInfo(int Width, int Height)
{
    public static ResolutionInfo Unknown => new(0, 0);

    public bool IsKnown => Width > 0 && Height > 0;

    public bool PassesMinimum1080p => ResolutionRules.Passes(Width, Height);

    public string Display => IsKnown ? $"{Width} × {Height}" : "Unknown";

    public double AspectRatio => IsKnown ? (double)Width / Height : 16d / 9d;

    public string Orientation => !IsKnown ? string.Empty : Width > Height ? "Landscape" : Width < Height ? "Portrait" : "Square";
}

public static class ResolutionRules
{
    public const int LongEdgeMinimum = 1920;
    public const int ShortEdgeMinimum = 1080;

    /// <summary>
    /// "At least 1080p" means the long edge is >= 1920 AND the short edge is >= 1080.
    /// This intentionally accepts 1080x1920 portrait and rejects 1080x1080 square.
    /// </summary>
    public static bool Passes(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var longEdge = Math.Max(width, height);
        var shortEdge = Math.Min(width, height);
        return longEdge >= LongEdgeMinimum && shortEdge >= ShortEdgeMinimum;
    }

    public static string Explain(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "Resolution could not be determined.";
        }

        var longEdge = Math.Max(width, height);
        var shortEdge = Math.Min(width, height);
        if (Passes(width, height))
        {
            return $"{width} × {height} passes the 1080p rule (long edge {longEdge} ≥ 1920, short edge {shortEdge} ≥ 1080).";
        }

        var reason = longEdge < LongEdgeMinimum
            ? $"long edge {longEdge} is below 1920"
            : $"short edge {shortEdge} is below 1080";
        return $"{width} × {height} does not meet the 1080p rule because the {reason}.";
    }
}
