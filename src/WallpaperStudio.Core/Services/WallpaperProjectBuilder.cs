using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SkiaSharp;

namespace WallpaperStudio.Services;

public sealed record ProjectBuildResult(
    string ProjectFolder,
    string? PreviewFile,
    IReadOnlyList<string> Warnings,
    long ContentBytes,
    bool IsExperimental);

/// <summary>
/// Builds normal, unencrypted Wallpaper Engine project folders from an image or
/// video so SteamCMD has something to upload. The directory layout and JSON keys
/// match real projects; the generated .tex texture is experimental and must be
/// verified in Wallpaper Engine before it is shared.
/// </summary>
public sealed class WallpaperProjectBuilder
{
    private const int MaxTextureEdge = 4096;
    private const int PreviewEdge = 1920;
    private const string MaterialName = "background";

    // JsonNode.ToJsonString marks the options read-only before writing, which stops
    // the default resolver from attaching lazily; it must be supplied up front.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly FfmpegService _ffmpeg;

    public WallpaperProjectBuilder(FfmpegService ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public string BuildStagingFolder(PublishItem item)
    {
        var basis = !string.IsNullOrWhiteSpace(item.Title)
            ? item.Title
            : Path.GetFileNameWithoutExtension(item.SourcePath);
        var slug = PathHelper.SanitizeFileName(basis, 60);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(PathHelper.Normalize(item.SourcePath))))[..8]
            .ToLowerInvariant();
        return Path.Combine(AppPaths.PublishDirectory, $"{slug}-{hash}");
    }

    public async Task<ProjectBuildResult> BuildAsync(
        PublishItem item,
        int jpegQuality,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        if (item.Kind == PublishKind.ExistingProject)
        {
            throw new InvalidOperationException("Existing projects are uploaded as-is and do not need building.");
        }

        var warnings = new List<string>();
        var root = BuildStagingFolder(item);
        if (Directory.Exists(PathHelper.ToExtendedPath(root)))
        {
            Directory.Delete(PathHelper.ToExtendedPath(root), recursive: true);
        }

        Directory.CreateDirectory(PathHelper.ToExtendedPath(root));

        var experimental = item.Kind == PublishKind.NewProjectFromImage;
        string? preview = item.Kind == PublishKind.NewProjectFromVideo
            ? await BuildVideoAsync(item, root, jpegQuality, warnings, cancellationToken, progress).ConfigureAwait(false)
            : BuildImageAsync(item, root, jpegQuality, warnings, progress);

        if (!File.Exists(PathHelper.ToExtendedPath(Path.Combine(root, "project.json"))))
        {
            throw new InvalidOperationException("The project folder was built without a project.json. Nothing was published.");
        }

        var bytes = MeasureFolder(root);
        progress?.Report($"Project built ({PathHelper.HumanSize(bytes)}).");

        return new ProjectBuildResult(root, preview, warnings, bytes, experimental);
    }

    private async Task<string?> BuildVideoAsync(
        PublishItem item,
        string root,
        int jpegQuality,
        List<string> warnings,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var source = item.SourcePath;
        if (!MediaFileTypes.IsVideo(source) || !File.Exists(PathHelper.ToExtendedPath(source)))
        {
            throw new InvalidOperationException("That video file is not readable.");
        }

        var fileName = Path.GetFileName(source);
        var target = Path.Combine(root, fileName);
        progress?.Report($"Copying {fileName}…");
        File.Copy(PathHelper.ToExtendedPath(source), PathHelper.ToExtendedPath(target), overwrite: true);

        progress?.Report("Extracting a preview frame…");
        string? preview = null;
        try
        {
            var info = await _ffmpeg.ProbeAsync(source, cancellationToken).ConfigureAwait(false);
            var time = info.DurationSeconds > 4 ? Math.Min(2.0, info.DurationSeconds * 0.1) : 0;
            var raw = Path.Combine(root, "preview_raw.jpg");
            await _ffmpeg.ExtractFrameAsync(source, time, raw, cancellationToken).ConfigureAwait(false);
            using var frame = SkiaImage.DecodeOriented(raw);
            TryDelete(raw);
            if (frame is not null)
            {
                preview = Path.Combine(root, "preview.jpg");
                SavePreview(frame, preview, jpegQuality);
            }
        }
        catch (Exception ex)
        {
            warnings.Add("A preview frame could not be extracted from this video. Steam may show a default preview image; you can also add a preview.jpg by hand before publishing.");
            Logger.Warn($"Video preview extraction failed for '{source}'.", ex);
        }

        var project = new JsonObject
        {
            ["contentrating"] = "Everyone",
            ["type"] = "video",
            ["version"] = 1,
            ["visibility"] = "public",
            ["file"] = fileName,
            ["title"] = EffectiveTitle(item),
            ["tags"] = new JsonArray(),
            ["general"] = new JsonObject { ["properties"] = SchemeColor() }
        };

        if (preview is not null)
        {
            project["preview"] = "preview.jpg";
        }

        WriteJson(Path.Combine(root, "project.json"), project);
        warnings.Add("Video wallpapers are played by Wallpaper Engine directly from the copied video file. Preview it in Wallpaper Engine before marking it public.");
        return preview is not null ? Path.Combine(root, "preview.jpg") : null;
    }

    private string? BuildImageAsync(
        PublishItem item,
        string root,
        int jpegQuality,
        List<string> warnings,
        IProgress<string>? progress)
    {
        if (!MediaFileTypes.IsExportImage(item.SourcePath))
        {
            throw new InvalidOperationException("That image file is not a supported format.");
        }

        progress?.Report("Decoding the image…");
        using var decoded = SkiaImage.DecodeOriented(item.SourcePath)
            ?? throw new InvalidOperationException("The image could not be decoded.");

        using var texture = SkiaImage.ResizeToFit(decoded, MaxTextureEdge);
        var width = texture.Width;
        var height = texture.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The image has no usable dimensions.");
        }

        var usePng = UsePng(item.SourcePath);
        progress?.Report("Writing the texture…");
        var materialFolder = Path.Combine(root, "materials");
        Directory.CreateDirectory(PathHelper.ToExtendedPath(materialFolder));
        TexWriter.Write(texture, Path.Combine(materialFolder, $"{MaterialName}.tex"), usePng);

        // model + material JSON
        var modelsFolder = Path.Combine(root, "models");
        Directory.CreateDirectory(PathHelper.ToExtendedPath(modelsFolder));
        WriteFile(Path.Combine(modelsFolder, $"{MaterialName}.json"), Json(new JsonObject
        {
            ["autosize"] = true,
            ["material"] = $"materials/{MaterialName}.json"
        }));

        WriteFile(Path.Combine(materialFolder, $"{MaterialName}.json"), Json(new JsonObject
        {
            ["passes"] = new JsonArray
            {
                new JsonObject
                {
                    ["blending"] = "translucent",
                    ["combos"] = new JsonObject { ["version"] = 2 },
                    ["cullmode"] = "nocull",
                    ["depthtest"] = "disabled",
                    ["depthwrite"] = "disabled",
                    ["shader"] = "genericimage2",
                    ["textures"] = new JsonArray { MaterialName }
                }
            }
        }));

        // scene.json
        WriteFile(Path.Combine(root, "scene.json"), Json(BuildScene(width, height)));

        // preview.jpg
        string? preview = null;
        try
        {
            preview = Path.Combine(root, "preview.jpg");
            SavePreview(texture, preview, jpegQuality);
        }
        catch (Exception ex)
        {
            preview = null;
            warnings.Add("A preview image could not be generated; Steam may show a default preview.");
            Logger.Warn("Scene preview generation failed.", ex);
        }

        var project = new JsonObject
        {
            ["contentrating"] = "Everyone",
            ["type"] = "scene",
            ["version"] = 1,
            ["visibility"] = "public",
            ["file"] = "scene.json",
            ["title"] = EffectiveTitle(item),
            ["tags"] = new JsonArray(),
            ["general"] = new JsonObject { ["properties"] = SchemeColor() }
        };

        if (preview is not null)
        {
            project["preview"] = "preview.jpg";
        }

        WriteJson(Path.Combine(root, "project.json"), project);

        warnings.Add("The generated scene uses an experimental texture container. Open the built folder in Wallpaper Engine's editor to confirm it renders correctly before making it public.");
        return preview;
    }

    private static JsonObject BuildScene(int width, int height)
    {
        var w = Format(width);
        var h = Format(height);
        var cx = Format(width / 2.0);
        var cy = Format(height / 2.0);

        return new JsonObject
        {
            ["camera"] = new JsonObject
            {
                ["center"] = "0.00000 0.00000 -1.00000",
                ["eye"] = "0.00000 0.00000 0.00000",
                ["up"] = "0.00000 1.00000 0.00000"
            },
            ["general"] = new JsonObject
            {
                ["ambientcolor"] = "0.30000 0.30000 0.30000",
                ["bloom"] = true,
                ["camerafade"] = true,
                ["cameraparallax"] = false,
                ["camerapreview"] = true,
                ["clearenabled"] = true,
                ["clearcolor"] = "0.00000 0.00000 0.00000",
                ["farz"] = 10000.0,
                ["fov"] = 50.0,
                ["nearz"] = 0.01,
                ["orthogonalprojection"] = new JsonObject { ["width"] = width, ["height"] = height },
                ["skylightcolor"] = "0.30000 0.30000 0.30000",
                ["zoom"] = 1.0
            },
            ["objects"] = new JsonArray
            {
                new JsonObject
                {
                    ["angles"] = "0.00000 0.00000 0.00000",
                    ["id"] = 1,
                    ["image"] = $"models/{MaterialName}.json",
                    ["ledsource"] = false,
                    ["locktransforms"] = false,
                    ["name"] = "Background",
                    ["origin"] = $"{cx} {cy} 0.00000",
                    ["parallaxDepth"] = "1.00000 1.00000",
                    ["perspective"] = false,
                    ["scale"] = "1.00000 1.00000 1.00000",
                    ["size"] = $"{w} {h}",
                    ["solid"] = true,
                    ["visible"] = true
                }
            },
            ["version"] = 1
        };
    }

    private static JsonObject SchemeColor()
        => new()
        {
            ["schemecolor"] = new JsonObject
            {
                ["text"] = "ui_browse_properties_scheme_color",
                ["type"] = "color",
                ["value"] = "0.090 0.110 0.170",
                ["order"] = 0
            }
        };

    private static void SavePreview(SKBitmap source, string path, int jpegQuality)
    {
        var resized = SkiaImage.ResizeToFit(source, PreviewEdge);
        try
        {
            SkiaImage.SaveJpeg(resized, path, Math.Clamp(jpegQuality, 60, 100));
        }
        finally
        {
            if (!ReferenceEquals(resized, source))
            {
                resized.Dispose();
            }
        }
    }

    private static bool UsePng(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static string EffectiveTitle(PublishItem item)
        => string.IsNullOrWhiteSpace(item.Title)
            ? Path.GetFileNameWithoutExtension(item.SourcePath)
            : item.Title.Trim();

    private static string Format(double value) => value.ToString("0.00000", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(JsonOptions);

    private static void WriteJson(string path, JsonNode node) => WriteFile(path, Json(node));

    private static void WriteFile(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(PathHelper.ToExtendedPath(directory));
        }

        File.WriteAllText(PathHelper.ToExtendedPath(path), contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static long MeasureFolder(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(PathHelper.ToExtendedPath(folder), "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(PathHelper.ToExtendedPath(path)))
            {
                File.Delete(PathHelper.ToExtendedPath(path));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temp file '{path}': {ex.Message}");
        }
    }
}
