using System.Text.Json;

namespace WallpaperStudio.Services;

/// <summary>
/// Tolerant reader for the small, documented part of a Wallpaper Engine
/// project.json we actually need. Unknown properties are ignored rather than
/// guessed at. No package/scene internals are interpreted here.
/// </summary>
public sealed record ProjectManifest(string? Type, string? File, string? Preview, string? Title);

public static class ProjectManifestReader
{
    public static bool TryRead(string projectJsonPath, out ProjectManifest manifest, out string? warning)
    {
        manifest = new ProjectManifest(null, null, null, null);
        warning = null;

        try
        {
            var json = File.ReadAllText(PathHelper.ToExtendedPath(projectJsonPath));
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warning = "project.json is not a JSON object.";
                return false;
            }

            var type = GetString(document.RootElement, "type");
            var file = GetString(document.RootElement, "file");
            var preview = GetString(document.RootElement, "preview");
            var title = GetString(document.RootElement, "title");
            manifest = new ProjectManifest(type, file, preview, title);
            return true;
        }
        catch (JsonException ex)
        {
            warning = $"project.json is not valid JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            warning = $"project.json could not be read: {ex.Message}";
            return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads the optional workshop id from an existing project.json. The value is
    /// documented as a string but sometimes appears as a number, so both are accepted.
    /// </summary>
    public static bool TryReadWorkshopId(string projectJsonPath, out string? workshopId)
    {
        workshopId = null;
        try
        {
            var json = File.ReadAllText(PathHelper.ToExtendedPath(projectJsonPath));
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "workshopid", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(value) && value != "0")
                {
                    workshopId = value.Trim();
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read workshopid from '{projectJsonPath}': {ex.Message}");
            return false;
        }
    }
}
