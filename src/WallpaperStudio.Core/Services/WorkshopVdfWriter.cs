using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WallpaperStudio.Services;

/// <summary>
/// Writes the small Valve KeyValues (VDF) file that SteamCMD's
/// <c>workshop_build_item</c> command consumes. The key names follow Valve's
/// documented workshop item script. The visibility value is written verbatim;
/// 2 means private/hidden.
/// </summary>
public static partial class WorkshopVdfWriter
{
    public sealed record Fields(
        string ContentFolder,
        string? PreviewFile,
        PublishVisibility Visibility,
        string Title,
        string? Description,
        string? ChangeNote,
        string? PublishedFileId);

    public static string Build(Fields fields)
    {
        var builder = new StringBuilder();
        builder.AppendLine("\"workshopitem\"");
        builder.AppendLine("{");
        Append(builder, "appid", MediaFileTypes.WallpaperEngineAppId);
        Append(builder, "publishedfileid", string.IsNullOrWhiteSpace(fields.PublishedFileId) ? "0" : fields.PublishedFileId);
        Append(builder, "contentfolder", fields.ContentFolder);
        if (!string.IsNullOrWhiteSpace(fields.PreviewFile))
        {
            Append(builder, "previewfile", fields.PreviewFile);
        }

        Append(builder, "visibility", ((int)fields.Visibility).ToString(CultureInfo.InvariantCulture));
        Append(builder, "title", fields.Title);
        if (!string.IsNullOrWhiteSpace(fields.Description))
        {
            Append(builder, "description", fields.Description);
        }

        if (!string.IsNullOrWhiteSpace(fields.ChangeNote))
        {
            Append(builder, "changenote", fields.ChangeNote);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    public static void Write(string path, Fields fields)
    {
        AtomicFile.WriteAllText(path, Build(fields));
        Logger.Info($"Wrote Workshop VDF: '{path}' (visibility={(int)fields.Visibility}).");
    }

    /// <summary>
    /// Reads the published file id back out of a VDF. SteamCMD rewrites the file
    /// with the id it created; a non-zero value is the only reliable success signal.
    /// </summary>
    public static bool TryReadPublishedFileId(string vdfPath, out string? publishedFileId)
    {
        publishedFileId = null;
        try
        {
            if (!File.Exists(PathHelper.ToExtendedPath(vdfPath)))
            {
                return false;
            }

            var text = File.ReadAllText(PathHelper.ToExtendedPath(vdfPath));
            var match = PublishedFileIdPattern().Match(text);
            if (!match.Success)
            {
                return false;
            }

            var value = match.Groups["value"].Value.Trim();
            if (string.IsNullOrWhiteSpace(value) || value == "0")
            {
                return false;
            }

            publishedFileId = value;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read published file id from '{vdfPath}'.", ex);
            return false;
        }
    }

    private static void Append(StringBuilder builder, string key, string value)
        => builder.Append('\t').Append('"').Append(key).Append("\"\t\t\"").Append(Escape(value)).Append('"').AppendLine();

    private static string Escape(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\n")
            .Replace("\t", "\\t");

    [GeneratedRegex("\"publishedfileid\"\\s*\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PublishedFileIdPattern();
}
