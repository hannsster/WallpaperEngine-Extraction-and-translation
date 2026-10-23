using System.Text;
using System.Text.RegularExpressions;

namespace WallpaperStudio.Infrastructure;

public static class PathHelper
{
    private static readonly Regex InvalidFileNameChars = new(@"[<>:""/\\|?*\x00-\x1F]", RegexOptions.Compiled);
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    public static bool PathsEqual(string? left, string? right)
        => string.Equals(Normalize(left ?? string.Empty), Normalize(right ?? string.Empty), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameOrChildPath(string candidate, string parent)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedParent = Normalize(parent);
        if (normalizedParent.Length == 0)
        {
            return false;
        }

        if (string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Prefixes long local paths so Win32 file APIs keep working without registry changes.</summary>
    public static string ToExtendedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.Length < 248)
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }

    public static string SanitizeFileName(string name, int maxLength = 120)
    {
        var cleaned = InvalidFileNameChars.Replace(name, "_").Trim().TrimEnd('.');
        if (cleaned.Length == 0)
        {
            cleaned = "wallpaper";
        }

        var reserved = ReservedNames.Any(r => string.Equals(r, cleaned, StringComparison.OrdinalIgnoreCase));
        if (reserved)
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd(' ', '.');
        }

        return cleaned;
    }

    public static string MakeUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }

    public static string Ellipsize(string value, int max = 90)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        var keep = Math.Max(10, max - 1);
        var tail = Math.Min(40, keep / 2);
        var head = keep - tail;
        var builder = new StringBuilder(head + tail + 1);
        builder.Append(value.AsSpan(0, head));
        builder.Append('…');
        builder.Append(value.AsSpan(value.Length - tail));
        return builder.ToString();
    }

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
