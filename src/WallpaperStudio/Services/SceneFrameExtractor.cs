using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace WallpaperStudio.Services;

/// <summary>The result of extracting a static frame from a scene package.</summary>
public sealed record SceneFrameResult(
    string Path,
    ResolutionInfo Resolution,
    bool IsAllBlack,
    int LayerCount,
    string? Warning);

/// <summary>
/// Read-only view over the assets a scene references (scene.json, models, materials and textures).
/// Two implementations exist: a PKGV package and a loose project folder.
/// </summary>
public interface ISceneAssets : IDisposable
{
    string Description { get; }

    IEnumerable<string> Names { get; }

    bool Exists(string name);

    bool TryRead(string name, out byte[] data);

    bool TryReadRange(string name, int maxBytes, out byte[] data);
}

/// <summary>Scene assets stored inside a Wallpaper Engine <c>.pkg</c> (PKGV) archive.</summary>
internal sealed class PkgSceneAssets : ISceneAssets
{
    private readonly PkgArchive _archive;

    public PkgSceneAssets(PkgArchive archive) => _archive = archive;

    public string Description => "scene.pkg";

    public IEnumerable<string> Names => _archive.Names;

    public bool Exists(string name) => _archive.Contains(name);

    public bool TryRead(string name, out byte[] data) => _archive.TryRead(name, out data);

    public bool TryReadRange(string name, int maxBytes, out byte[] data) => _archive.TryReadRange(name, maxBytes, out data);

    public void Dispose() => _archive.Dispose();
}

/// <summary>Scene assets stored as loose files inside an extracted project folder.</summary>
internal sealed class FolderSceneAssets : ISceneAssets
{
    private readonly string _root;
    private readonly string? _assetRoot;

    public FolderSceneAssets(string root)
    {
        _root = root;
        var nested = Path.Combine(root, "assets");
        _assetRoot = Directory.Exists(PathHelper.ToExtendedPath(nested)) ? nested : null;
    }

    public string Description => _root;

    public IEnumerable<string> Names
    {
        get
        {
            var results = new List<string>();
            Collect(_root, results);
            if (_assetRoot is not null)
            {
                Collect(_assetRoot, results);
            }

            return results;
        }
    }

    public bool Exists(string name) => TryResolve(name, out _);

    public bool TryRead(string name, out byte[] data)
    {
        data = [];
        if (!TryResolve(name, out var path))
        {
            return false;
        }

        try
        {
            data = File.ReadAllBytes(PathHelper.ToExtendedPath(path));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read scene asset '{path}': {ex.Message}");
            return false;
        }
    }

    public bool TryReadRange(string name, int maxBytes, out byte[] data)
    {
        data = [];
        if (maxBytes <= 0 || !TryResolve(name, out var path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(PathHelper.ToExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(maxBytes, stream.Length);
            var buffer = new byte[length];
            var read = 0;
            while (read < length)
            {
                var chunk = stream.Read(buffer, read, length - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read != length)
            {
                return false;
            }

            data = buffer;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read scene asset '{path}': {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        // Loose files hold no open handles.
    }

    private bool TryResolve(string name, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var relative = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        foreach (var baseDirectory in _assetRoot is null ? [_root] : new[] { _root, _assetRoot })
        {
            string combined;
            try
            {
                combined = Path.GetFullPath(Path.Combine(baseDirectory, relative));
            }
            catch
            {
                continue;
            }

            if (!PathHelper.IsSameOrChildPath(combined, baseDirectory))
            {
                continue;
            }

            if (File.Exists(PathHelper.ToExtendedPath(combined)))
            {
                path = combined;
                return true;
            }
        }

        return false;
    }

    private static void Collect(string root, List<string> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(PathHelper.ToExtendedPath(root), "*.tex", SearchOption.AllDirectories))
            {
                var normalized = file.StartsWith(@"\\?\", StringComparison.Ordinal) ? file[4..] : file;
                var relative = Path.GetRelativePath(root, normalized).Replace(Path.DirectorySeparatorChar, '/');
                results.Add(relative);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not enumerate scene assets in '{root}': {ex.Message}");
        }
    }
}

/// <summary>
/// Extracts a full-resolution static frame from a Wallpaper Engine scene by
/// following the documented scene.json → model → material → texture chain and
/// decoding the referenced <c>.tex</c> data. It never executes scene shaders or
/// effects; full-frame image layers are composited bottom-to-top on black.
/// Extraction is best-effort and every frame is validated (including an
/// all-black check) before it is offered for export.
/// </summary>
public sealed class SceneFrameExtractor
{
    private const int HeaderProbeBytes = 256;
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private const int MinPlausibleEdge = 256;
    private const int CacheJpegQuality = 98;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Cheap header-only pass that reports the real scene resolution without decoding any pixels.</summary>
    public bool TryDescribe(WallpaperSource source, out ResolutionInfo resolution)
    {
        resolution = ResolutionInfo.Unknown;
        if (!TryOpenAssets(source, out var assets) || assets is null)
        {
            return false;
        }

        using (assets)
        {
            var plan = BuildPlan(assets);
            if (plan is null || plan.CanvasWidth <= 0 || plan.CanvasHeight <= 0)
            {
                return false;
            }

            resolution = new ResolutionInfo(plan.CanvasWidth, plan.CanvasHeight);
            return resolution.IsKnown;
        }
    }

    public Task<ResolutionInfo> DescribeAsync(WallpaperSource source, CancellationToken cancellationToken)
        => Task.Run(() => TryDescribe(source, out var resolution) ? resolution : ResolutionInfo.Unknown, cancellationToken);

    public Task<SceneFrameResult?> ExtractAsync(WallpaperSource source, CancellationToken cancellationToken)
        => Task.Run(() => Extract(source, cancellationToken), cancellationToken);

    private SceneFrameResult? Extract(WallpaperSource source, CancellationToken cancellationToken)
    {
        var cachePath = CachePath(source);
        if (File.Exists(PathHelper.ToExtendedPath(cachePath)) && SkiaImage.TryProbe(cachePath, out var cachedWidth, out var cachedHeight))
        {
            var cachedBlack = FrameAnalyzer.IsAllBlackFile(cachePath, out _);
            return new SceneFrameResult(cachePath, new ResolutionInfo(cachedWidth, cachedHeight), cachedBlack, 0, null);
        }

        if (!TryOpenAssets(source, out var assets) || assets is null)
        {
            return null;
        }

        using (assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = BuildPlan(assets);
            if (plan is null || plan.Layers.Count == 0)
            {
                return null;
            }

            using var canvas = Render(assets, plan, cancellationToken);
            if (canvas is null || canvas.Width <= 0 || canvas.Height <= 0)
            {
                return null;
            }

            var isBlack = FrameAnalyzer.IsAllBlack(canvas, out _);
            try
            {
                SkiaImage.SaveJpeg(canvas, cachePath, CacheJpegQuality);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not cache the extracted scene frame for '{source.SourcePath}'.", ex);
                return null;
            }

            var resolution = new ResolutionInfo(canvas.Width, canvas.Height);
            Logger.Info($"Extracted scene frame '{source.SourcePath}' -> {resolution.Display} from {plan.Layers.Count} layer(s).");
            return new SceneFrameResult(cachePath, resolution, isBlack, plan.Layers.Count, null);
        }
    }

    private static SKBitmap? Render(ISceneAssets assets, ScenePlan plan, CancellationToken cancellationToken)
    {
        SKBitmap canvas;
        try
        {
            canvas = new SKBitmap(new SKImageInfo(plan.CanvasWidth, plan.CanvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not allocate a {plan.CanvasWidth}×{plan.CanvasHeight} scene canvas.", ex);
            return null;
        }

        try
        {
            using var skCanvas = new SKCanvas(canvas);
            skCanvas.Clear(SKColors.Black);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            foreach (var layer in plan.Layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!assets.TryRead(layer.PackageName, out var bytes))
                {
                    continue;
                }

                using var decoded = TexDecoder.Decode(bytes);
                if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
                {
                    continue;
                }

                var sourceWidth = layer.PictureWidth > 0 ? Math.Min(layer.PictureWidth, decoded.Width) : decoded.Width;
                var sourceHeight = layer.PictureHeight > 0 ? Math.Min(layer.PictureHeight, decoded.Height) : decoded.Height;
                var sourceRect = new SKRect(0, 0, sourceWidth, sourceHeight);
                var destinationRect = new SKRect(0, 0, plan.CanvasWidth, plan.CanvasHeight);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    BlendMode = layer.BlendMode,
                    Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)Math.Round(layer.Alpha * 255), 0, 255))
                };

                skCanvas.DrawBitmap(decoded, sourceRect, destinationRect, sampling, paint);
            }

            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    private static ScenePlan? BuildPlan(ISceneAssets assets)
    {
        if (!TryReadJson(assets, "scene.json", out var root))
        {
            return BuildFallbackPlan(assets);
        }

        ReadProjection(root, out var projectionWidth, out var projectionHeight);

        var layers = new List<SceneLayer>();
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("objects", out var objects) &&
            objects.ValueKind == JsonValueKind.Array)
        {
            foreach (var obj in objects.EnumerateArray())
            {
                var layer = TryBuildLayer(assets, obj);
                if (layer is not null)
                {
                    layers.Add(layer);
                }
            }
        }

        if (layers.Count == 0)
        {
            return BuildFallbackPlan(assets);
        }

        var largest = layers[0];
        foreach (var layer in layers)
        {
            if ((long)layer.PictureWidth * layer.PictureHeight > (long)largest.PictureWidth * largest.PictureHeight)
            {
                largest = layer;
            }
        }

        var canvasWidth = projectionWidth > 0 ? projectionWidth : largest.PictureWidth;
        var canvasHeight = projectionHeight > 0 ? projectionHeight : largest.PictureHeight;

        var selected = layers.Where(layer => IsFullFrame(layer, canvasWidth, canvasHeight)).ToList();
        if (selected.Count == 0)
        {
            canvasWidth = largest.PictureWidth;
            canvasHeight = largest.PictureHeight;
            selected = layers.Where(layer => IsFullFrame(layer, canvasWidth, canvasHeight)).ToList();
        }

        if (selected.Count == 0)
        {
            canvasWidth = largest.PictureWidth;
            canvasHeight = largest.PictureHeight;
            selected = [largest];
        }

        return new ScenePlan(canvasWidth, canvasHeight, selected);
    }

    private static ScenePlan? BuildFallbackPlan(ISceneAssets assets)
    {
        SceneLayer? best = null;
        long bestArea = 0;

        foreach (var name in assets.Names)
        {
            if (!name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!assets.TryReadRange(name, HeaderProbeBytes, out var header) ||
                !TexDecoder.TryReadInfo(header, out var width, out var height, out var imageType, out var format))
            {
                continue;
            }

            if (width < MinPlausibleEdge || height < MinPlausibleEdge || !IsColourTexture(imageType, format))
            {
                continue;
            }

            var area = (long)width * height;
            if (area <= bestArea)
            {
                continue;
            }

            bestArea = area;
            best = new SceneLayer(name, width, height, width, height, true, 1.0, SKBlendMode.SrcOver, imageType, format);
        }

        return best is null
            ? null
            : new ScenePlan(best.PictureWidth, best.PictureHeight, [best]);
    }

    private static SceneLayer? TryBuildLayer(ISceneAssets assets, JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetBool(obj, "visible", out var sceneVisible) && !sceneVisible)
        {
            return null;
        }

        if (!TryGetString(obj, "image", out var imageName) || !TryReadJson(assets, imageName, out var imageJson))
        {
            return null;
        }

        if (TryGetBool(imageJson, "visible", out var imageVisible) && !imageVisible)
        {
            return null;
        }

        if (!TryGetString(imageJson, "material", out var materialName) || IsUtilityAsset(materialName))
        {
            return null;
        }

        var fullscreen = false;
        TryGetBool(obj, "fullscreen", out fullscreen);
        if (!fullscreen)
        {
            TryGetBool(imageJson, "fullscreen", out fullscreen);
        }

        var alpha = 1.0;
        if (!TryGetDouble(obj, "alpha", out alpha) && !TryGetDouble(imageJson, "alpha", out alpha))
        {
            alpha = 1.0;
        }

        if (alpha <= 0.001)
        {
            return null;
        }

        ReadSize(obj, out var objectWidth, out var objectHeight);
        if (objectWidth <= 0 || objectHeight <= 0)
        {
            ReadSize(imageJson, out objectWidth, out objectHeight);
        }

        if (!TryReadJson(assets, materialName, out var materialJson) ||
            !TryGetFirstTexture(materialJson, out var textureName, out var blending) ||
            IsUtilityAsset(textureName))
        {
            return null;
        }

        var packageName = "materials/" + textureName + ".tex";
        if (!assets.TryReadRange(packageName, HeaderProbeBytes, out var header) ||
            !TexDecoder.TryReadInfo(header, out var width, out var height, out var imageType, out var format))
        {
            return null;
        }

        if (width <= 0 || height <= 0 || !IsColourTexture(imageType, format))
        {
            return null;
        }

        return new SceneLayer(
            packageName,
            width,
            height,
            objectWidth,
            objectHeight,
            fullscreen,
            alpha,
            MapBlend(blending),
            imageType,
            format);
    }

    private static bool TryGetFirstTexture(JsonElement materialJson, out string name, out string? blending)
    {
        name = string.Empty;
        blending = null;
        if (materialJson.ValueKind != JsonValueKind.Object ||
            !materialJson.TryGetProperty("passes", out var passes) ||
            passes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var pass in passes.EnumerateArray())
        {
            if (pass.ValueKind != JsonValueKind.Object ||
                !pass.TryGetProperty("textures", out var textures) ||
                textures.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var texture in textures.EnumerateArray())
            {
                if (texture.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = texture.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                name = value;
                TryGetString(pass, "blending", out var passBlending);
                blending = passBlending;
                return true;
            }
        }

        return false;
    }

    private static bool IsFullFrame(SceneLayer layer, int canvasWidth, int canvasHeight)
    {
        if (layer.Fullscreen)
        {
            return true;
        }

        if (Approximately(layer.ObjectWidth, canvasWidth) && Approximately(layer.ObjectHeight, canvasHeight))
        {
            return true;
        }

        return Approximately(layer.PictureWidth, canvasWidth) && Approximately(layer.PictureHeight, canvasHeight);
    }

    private static bool Approximately(double value, double reference)
        => value > 0 && reference > 0 && Math.Abs(value - reference) / Math.Max(value, reference) <= 0.08;

    private static bool IsColourTexture(int imageType, int format)
    {
        if (TexDecoder.IsImageContainer(imageType))
        {
            return true;
        }

        // Single-channel masks are never a colour layer.
        return BlockTextureDecoder.IsSupported(format) &&
               format is not BlockTextureDecoder.FormatRg8 and not BlockTextureDecoder.FormatR8;
    }

    private static bool IsUtilityAsset(string name)
    {
        var normalized = name.Replace('\\', '/');
        return normalized.StartsWith("util/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/util/", StringComparison.OrdinalIgnoreCase);
    }

    private static SKBlendMode MapBlend(string? blending) => blending?.Trim().ToLowerInvariant() switch
    {
        "additive" => SKBlendMode.Plus,
        "multiply" => SKBlendMode.Multiply,
        "screen" => SKBlendMode.Screen,
        _ => SKBlendMode.SrcOver
    };

    private static bool TryOpenAssets(WallpaperSource source, out ISceneAssets? assets)
    {
        assets = null;

        if (TryOpenPkg(source.PrimaryFilePath, out assets))
        {
            return true;
        }

        var directory = Directory.Exists(PathHelper.ToExtendedPath(source.SourcePath))
            ? source.SourcePath
            : Path.GetDirectoryName(source.PrimaryFilePath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(PathHelper.ToExtendedPath(directory)))
        {
            return false;
        }

        if (TryOpenPkg(Path.Combine(directory, "scene" + MediaFileTypes.PkgExtension), out assets))
        {
            return true;
        }

        // Some workshop folders keep several packages; use the first, then any top-level package.
        var packages = SafeEnumeratePackages(directory);
        foreach (var package in packages)
        {
            if (TryOpenPkg(package, out assets))
            {
                return true;
            }
        }

        if (File.Exists(PathHelper.ToExtendedPath(Path.Combine(directory, "scene.json"))))
        {
            assets = new FolderSceneAssets(directory);
            return true;
        }

        return false;
    }

    private static bool TryOpenPkg(string? path, out ISceneAssets? assets)
    {
        assets = null;
        if (string.IsNullOrWhiteSpace(path) ||
            !MediaFileTypes.IsPkg(path) ||
            !File.Exists(PathHelper.ToExtendedPath(path)))
        {
            return false;
        }

        if (PkgArchive.TryOpen(path, out var archive, out var error) && archive is not null)
        {
            assets = new PkgSceneAssets(archive);
            return true;
        }

        if (error is not null)
        {
            Logger.Debug($"Scene package '{path}' could not be opened: {error}");
        }

        return false;
    }

    private static List<string> SafeEnumeratePackages(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(PathHelper.ToExtendedPath(directory), "*" + MediaFileTypes.PkgExtension, SearchOption.TopDirectoryOnly)
                .Select(path => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not list packages in '{directory}': {ex.Message}");
            return [];
        }
    }

    private static bool TryReadJson(ISceneAssets assets, string name, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(name) || !assets.TryReadRange(name, MaxJsonBytes, out var bytes) || bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, ParseOptions);
            element = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not parse JSON asset '{name}': {ex.Message}");
            return false;
        }
    }

    private static void ReadProjection(JsonElement root, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("general", out var general) ||
            general.ValueKind != JsonValueKind.Object ||
            !general.TryGetProperty("orthogonalprojection", out var projection))
        {
            return;
        }

        TryGetInt(projection, "width", out width);
        TryGetInt(projection, "height", out height);
        if (width is < 64 or > 65536)
        {
            width = 0;
        }

        if (height is < 64 or > 65536)
        {
            height = 0;
        }
    }

    private static void ReadSize(JsonElement element, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("size", out var size))
        {
            return;
        }

        if (size.ValueKind == JsonValueKind.String)
        {
            var parts = (size.GetString() ?? string.Empty)
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWidth))
            {
                width = parsedWidth;
            }

            if (parts.Length >= 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeight))
            {
                height = parsedHeight;
            }

            return;
        }

        if (size.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var values = size.EnumerateArray().ToArray();
        if (values.Length >= 1 && values[0].ValueKind == JsonValueKind.Number)
        {
            width = values[0].GetDouble();
        }

        if (values.Length >= 2 && values[1].ValueKind == JsonValueKind.Number)
        {
            height = values[1].GetDouble();
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                return bool.TryParse(property.GetString(), out value);
            default:
                return false;
        }
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (TryGetDouble(element, name, out var asDouble))
        {
            value = (int)Math.Round(asDouble);
            return true;
        }

        return false;
    }

    private static string CachePath(WallpaperSource source)
    {
        var key = !string.IsNullOrWhiteSpace(source.FileHash) && source.FileHash.Length >= 16
            ? source.FileHash[..16]
            : ShortHash(source.SourcePath);
        return Path.Combine(AppPaths.FrameCacheDirectory, $"scene-{key}.jpg");
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private sealed record SceneLayer(
        string PackageName,
        int PictureWidth,
        int PictureHeight,
        double ObjectWidth,
        double ObjectHeight,
        bool Fullscreen,
        double Alpha,
        SKBlendMode BlendMode,
        int ImageType,
        int Format);

    private sealed record ScenePlan(int CanvasWidth, int CanvasHeight, IReadOnlyList<SceneLayer> Layers);
}
