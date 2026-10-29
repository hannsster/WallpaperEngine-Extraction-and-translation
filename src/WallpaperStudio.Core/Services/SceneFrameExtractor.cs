using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using WallpaperStudio.Models;

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
    private const int MinCanvasEdge = 64;
    private const int MaxCanvasEdge = 16384;
    private const double MaxUpscale = 3.0;

    /// <summary>
    /// Textures at least this large (on their longer edge) are treated as real
    /// artwork rather than stretched effect gradients.
    /// </summary>
    private const int EffectGradientMaxEdge = 1024;

    /// <summary>
    /// Set <c>WALLPAPERSTUDIO_DEBUG_LAYERS=1</c> to write one line per scene layer
    /// (texture size, on-screen footprint, and whether it was selected) to the log.
    /// </summary>
    private static readonly bool VerboseLayers =
        Environment.GetEnvironmentVariable("WALLPAPERSTUDIO_DEBUG_LAYERS") == "1";
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
            var plan = BuildPlan(assets, SceneComposition.Balanced);
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
        => ExtractAsync(source, source.SceneComposition, cancellationToken);

    public Task<SceneFrameResult?> ExtractAsync(WallpaperSource source, SceneComposition composition, CancellationToken cancellationToken)
        => Task.Run(() => Extract(source, composition, cancellationToken), cancellationToken);

    private SceneFrameResult? Extract(WallpaperSource source, SceneComposition composition, CancellationToken cancellationToken)
    {
        var cachePath = CachePath(source, composition);
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
            var plan = BuildPlan(assets, composition);
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
            Logger.Info($"Extracted scene frame '{source.SourcePath}' -> {resolution.Display} from {plan.Layers.Count} layer(s) ({composition}).");
            return new SceneFrameResult(cachePath, resolution, isBlack, plan.Layers.Count, null);
        }
    }

    private static SKBitmap? Render(ISceneAssets assets, ScenePlan plan, CancellationToken cancellationToken)
        => RenderCore(assets, plan, cancellationToken, includeFallbackBase: false, allowRetry: true);

    private static SKBitmap? RenderCore(
        ISceneAssets assets,
        ScenePlan plan,
        CancellationToken cancellationToken,
        bool includeFallbackBase,
        bool allowRetry)
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
            var backgroundCoverage = 0.0;
            var canvasArea = (double)plan.CanvasWidth * plan.CanvasHeight;
            using (var skCanvas = new SKCanvas(canvas))
            {
                skCanvas.Clear(SKColors.Black);
                var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

                if (includeFallbackBase && TryDrawFallbackTexture(assets, skCanvas, plan, sampling, cancellationToken))
                {
                    backgroundCoverage = 1;
                }

                var drawn = 0;
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

                    // Padded and spritesheet textures are larger than the picture the
                    // object actually shows, so crop to the declared source rectangle
                    // or the most representative frame of a sprite sheet.
                    var sourceRect = SourceRect(layer, decoded);
                    if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
                    {
                        continue;
                    }

                    var stats = MeasureTexture(decoded, sourceRect);

                    // A pure-black overlay, video sheet or mask drawn last turns an
                    // otherwise fine frame black. Transparent textures are equally
                    // pointless. Additive light sheets are the exception: their black
                    // is a no-op and they must not be dropped.
                    var additive = layer.BlendMode == SKBlendMode.Plus || layer.Spritesheet;
                    if (!additive &&
                        ((drawn > 0 && IsNearBlack(stats)) || (plan.Layers.Count > 1 && stats.OpaqueRatio < 0.02)))
                    {
                        continue;
                    }

                    var blend = layer.BlendMode;
                    if (layer.Spritesheet && IsNearBlack(stats))
                    {
                        // Opaque sprite sheets usually store light on a black card.
                        // Adding them (instead of drawing the black card) is what the
                        // scene's additive light objects do.
                        blend = SKBlendMode.Plus;
                    }

                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        BlendMode = blend,
                        Color = SKColors.White.WithAlpha((byte)Math.Clamp((int)Math.Round(layer.Alpha * 255), 0, 255))
                    };

                    if (layer.Fullscreen || !layer.HasTransform)
                    {
                        skCanvas.DrawBitmap(decoded, sourceRect, new SKRect(0, 0, plan.CanvasWidth, plan.CanvasHeight), sampling, paint);
                    }
                    else
                    {
                        var width = layer.ObjectWidth > 0 ? layer.ObjectWidth : sourceRect.Width;
                        var height = layer.ObjectHeight > 0 ? layer.ObjectHeight : sourceRect.Height;
                        if (width <= 0 || height <= 0)
                        {
                            continue;
                        }

                        // Local bitmap coordinates are Y-down. The scene is Y-up, so
                        // flip the picture, apply the layer/parent transform, then
                        // flip the whole scene onto the canvas.
                        var matrix = Compose(
                            SceneToCanvas(plan.CanvasHeight),
                            Compose(layer.World, FlipY()));
                        skCanvas.Save();
                        skCanvas.SetMatrix(matrix);
                        skCanvas.DrawBitmap(
                            decoded,
                            sourceRect,
                            new SKRect((float)(-width / 2), (float)(-height / 2), (float)(width / 2), (float)(height / 2)),
                            sampling,
                            paint);
                        skCanvas.Restore();
                    }

                    drawn++;

                    var coverage = stats.OpaqueRatio * FootprintWidth(layer) * FootprintHeight(layer) / canvasArea;
                    backgroundCoverage = Math.Max(backgroundCoverage, Math.Min(coverage, 1.0));
                }

                // If the scene only contributed black pixels, look for the actual
                // artwork in the package (customisable scenes often hide their base
                // image behind a user-property default of zero alpha).
                if (!includeFallbackBase &&
                    (drawn == 0 || FrameAnalyzer.IsAllBlack(canvas, out _)))
                {
                    TryDrawFallbackTexture(assets, skCanvas, plan, sampling, cancellationToken);
                }
            }

            // A frame made only of sprites/particles over black is missing its
            // background; redraw it over the best full-size picture in the package.
            if (allowRetry &&
                backgroundCoverage < 0.35 &&
                FrameAnalyzer.Analyze(canvas).DarkFraction >= 0.9)
            {
                canvas.Dispose();
                return RenderCore(assets, plan, cancellationToken, includeFallbackBase: true, allowRetry: false);
            }

            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Picks the part of a texture an object shows. Padded textures use their
    /// declared picture size; sprite sheets use the busiest cell so a still frame
    /// shows an actual explosion/frame instead of an empty start frame.
    /// </summary>
    private static SKRect SourceRect(SceneLayer layer, SKBitmap bitmap)
    {
        var sourceWidth = layer.SourceWidth > 0 ? Math.Min(layer.SourceWidth, bitmap.Width) : bitmap.Width;
        var sourceHeight = layer.SourceHeight > 0 ? Math.Min(layer.SourceHeight, bitmap.Height) : bitmap.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return SKRect.Empty;
        }

        if (layer.Spritesheet && layer.GridColumns > 1 && layer.GridRows > 1)
        {
            var (column, row) = SelectSpritesheetCell(bitmap, layer.GridColumns, layer.GridRows, sourceWidth, sourceHeight);
            var left = Math.Clamp(column * sourceWidth, 0, Math.Max(0, bitmap.Width - sourceWidth));
            var top = Math.Clamp(row * sourceHeight, 0, Math.Max(0, bitmap.Height - sourceHeight));
            return new SKRect(left, top, left + sourceWidth, top + sourceHeight);
        }

        var x = Math.Max(0, Math.Min(layer.SourceX, Math.Max(0, bitmap.Width - sourceWidth)));
        var y = Math.Max(0, Math.Min(layer.SourceY, Math.Max(0, bitmap.Height - sourceHeight)));
        return new SKRect(x, y, x + sourceWidth, y + sourceHeight);
    }

    private static (int Column, int Row) SelectSpritesheetCell(
        SKBitmap bitmap,
        int columns,
        int rows,
        int cellWidth,
        int cellHeight)
    {
        const int Grid = 24;
        var bestScore = -1.0;
        var bestColumn = 0;
        var bestRow = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = column * cellWidth;
                var top = row * cellHeight;
                if (left + cellWidth > bitmap.Width || top + cellHeight > bitmap.Height)
                {
                    continue;
                }

                var score = 0.0;
                for (var y = 0; y < Grid; y++)
                {
                    var py = (int)Math.Clamp(top + ((y + 0.5f) / Grid) * cellHeight, top, top + cellHeight - 1);
                    for (var x = 0; x < Grid; x++)
                    {
                        var px = (int)Math.Clamp(left + ((x + 0.5f) / Grid) * cellWidth, left, left + cellWidth - 1);
                        var pixel = bitmap.GetPixel(px, py);
                        if (pixel.Alpha < 24)
                        {
                            continue;
                        }

                        score += 0.2126 * pixel.Red + 0.7152 * pixel.Green + 0.0722 * pixel.Blue;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestColumn = column;
                    bestRow = row;
                }
            }
        }

        return (bestColumn, bestRow);
    }

    private static SKMatrix SceneToCanvas(int canvasHeight)
        => Matrix(1, 0, 0, -1, 0, canvasHeight);

    private static SKMatrix FlipY()
        => Matrix(1, 0, 0, -1, 0, 0);

    private static SKMatrix Matrix(double a, double b, double c, double d, double e, double f) => new()
    {
        ScaleX = (float)a,
        SkewX = (float)c,
        TransX = (float)e,
        SkewY = (float)b,
        ScaleY = (float)d,
        TransY = (float)f,
        Persp0 = 0,
        Persp1 = 0,
        Persp2 = 1
    };

    /// <summary>Composes two 2D affine matrices: the result applies <paramref name="right"/> first.</summary>
    private static SKMatrix Compose(SKMatrix left, SKMatrix right) => Matrix(
        (double)left.ScaleX * right.ScaleX + (double)left.SkewX * right.SkewY,
        (double)left.SkewY * right.ScaleX + (double)left.ScaleY * right.SkewY,
        (double)left.ScaleX * right.SkewX + (double)left.SkewX * right.ScaleY,
        (double)left.SkewY * right.SkewX + (double)left.ScaleY * right.ScaleY,
        (double)left.ScaleX * right.TransX + (double)left.SkewX * right.TransY + left.TransX,
        (double)left.SkewY * right.TransX + (double)left.ScaleY * right.TransY + left.TransY);

    private static SKMatrix LocalMatrix(JsonElement obj)
    {
        ReadVector2(obj, "origin", out var originX, out var originY);
        if (!ReadVector2(obj, "scale", out var scaleX, out var scaleY))
        {
            scaleX = 1.0;
            scaleY = 1.0;
        }

        if (scaleX <= 0)
        {
            scaleX = 1.0;
        }

        if (scaleY <= 0)
        {
            scaleY = 1.0;
        }

        var angle = ReadAngle(obj);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return Matrix(
            cos * scaleX,
            sin * scaleX,
            -sin * scaleY,
            cos * scaleY,
            originX,
            originY);
    }

    private static double ReadAngle(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("angles", out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("value", out var inner))
        {
            property = inner;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var parts = (property.GetString() ?? string.Empty)
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                return z;
            }

            return 0;
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            var values = property.EnumerateArray().ToArray();
            if (values.Length >= 3 && values[2].ValueKind == JsonValueKind.Number)
            {
                return values[2].GetDouble();
            }
        }

        return 0;
    }

    private static bool CoversCanvas(double extent, int canvas)
        => extent >= canvas * 0.995 && extent <= canvas * 1.05;

    private readonly record struct TextureStats(double OpaqueRatio, double BlackRatio);

    private static TextureStats MeasureTexture(SKBitmap bitmap, SKRect source)
    {
        const int Grid = 32;
        var opaque = 0;
        var black = 0;
        var total = Grid * Grid;
        for (var row = 0; row < Grid; row++)
        {
            var y = (int)Math.Clamp(source.Top + ((row + 0.5f) / Grid) * source.Height, source.Top, Math.Max(source.Top, source.Bottom - 1));
            for (var column = 0; column < Grid; column++)
            {
                var x = (int)Math.Clamp(source.Left + ((column + 0.5f) / Grid) * source.Width, source.Left, Math.Max(source.Left, source.Right - 1));
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 24)
                {
                    continue;
                }

                opaque++;
                var luminance = (0.2126 * pixel.Red) + (0.7152 * pixel.Green) + (0.0722 * pixel.Blue);
                if (luminance < 12)
                {
                    black++;
                }
            }
        }

        return new TextureStats(
            opaque / (double)total,
            opaque == 0 ? 0 : black / (double)opaque);
    }

    private static bool IsNearBlack(TextureStats stats)
        => stats.OpaqueRatio >= 0.2 && stats.BlackRatio >= 0.9;

    /// <summary>
    /// Draws the most promising picture in the package when a scene renders black.
    /// Customisable scenes frequently keep their base artwork on a layer whose
    /// user-property default is zero alpha, so it never appears in the plan.
    /// </summary>
    private static bool TryDrawFallbackTexture(
        ISceneAssets assets,
        SKCanvas canvas,
        ScenePlan plan,
        SKSamplingOptions sampling,
        CancellationToken cancellationToken)
    {
        const int MaxCandidates = 48;

        var candidates = new List<(string Name, int Width, int Height, long Area)>();
        foreach (var name in assets.Names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) ||
                IsUtilityAsset(name) ||
                name.Contains("/masks/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!assets.TryReadRange(name, HeaderProbeBytes, out var header) ||
                !TexDecoder.TryReadInfo(header, out var width, out var height, out var imageType, out var format) ||
                width < MinPlausibleEdge || height < MinPlausibleEdge ||
                !IsColourTexture(imageType, format))
            {
                continue;
            }

            candidates.Add((name, width, height, (long)width * height));
        }

        candidates.Sort((left, right) => right.Area.CompareTo(left.Area));

        string? bestName = null;
        var bestScore = 0.0;
        var examined = 0;
        foreach (var candidate in candidates)
        {
            if (++examined > MaxCandidates)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!assets.TryRead(candidate.Name, out var bytes))
            {
                continue;
            }

            using var bitmap = TexDecoder.Decode(bytes);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                continue;
            }

            var stats = MeasureTexture(bitmap, new SKRect(0, 0, bitmap.Width, bitmap.Height));
            if (stats.OpaqueRatio < 0.08 || IsNearBlack(stats))
            {
                continue;
            }

            // Prefer a picture that fills its own frame; a mostly transparent
            // body-part sprite would leave the canvas black.
            var score = stats.OpaqueRatio * (candidate.Area / (double)candidates[0].Area);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestName = candidate.Name;
            if (stats.OpaqueRatio >= 0.6)
            {
                break;
            }
        }

        if (bestName is null || !assets.TryRead(bestName, out var fallbackBytes))
        {
            return false;
        }

        using var fallback = TexDecoder.Decode(fallbackBytes);
        if (fallback is null || fallback.Width <= 0 || fallback.Height <= 0)
        {
            return false;
        }

        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
        canvas.DrawBitmap(
            fallback,
            new SKRect(0, 0, fallback.Width, fallback.Height),
            new SKRect(0, 0, plan.CanvasWidth, plan.CanvasHeight),
            sampling,
            paint);
        return true;
    }

    private static ScenePlan? BuildPlan(ISceneAssets assets, SceneComposition composition)
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
            var items = objects.EnumerateArray().ToList();
            var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (TryGetId(item, "id", out var id))
                {
                    byId[id] = item;
                }
            }

            var worldCache = new Dictionary<string, SKMatrix>(StringComparer.Ordinal);
            foreach (var obj in items)
            {
                var world = ResolveWorld(obj, byId, worldCache, new HashSet<string>(StringComparer.Ordinal), depth: 0);
                var layer = TryBuildLayer(assets, obj, world);
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

        var canvasWidth = projectionWidth;
        var canvasHeight = projectionHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            // No orthogonal projection: use the largest texture, as before.
            var largestTexture = layers[0];
            long largestArea = 0;
            foreach (var layer in layers)
            {
                var area = (long)layer.PictureWidth * layer.PictureHeight;
                if (area > largestArea)
                {
                    largestArea = area;
                    largestTexture = layer;
                }
            }

            canvasWidth = largestTexture.PictureWidth;
            canvasHeight = largestTexture.PictureHeight;
        }

        if (canvasWidth < MinCanvasEdge || canvasHeight < MinCanvasEdge ||
            canvasWidth > MaxCanvasEdge || canvasHeight > MaxCanvasEdge)
        {
            return BuildFallbackPlan(assets);
        }

        var canvasArea = (double)canvasWidth * canvasHeight;

        // A background is a layer that already fills a good part of the frame. Once
        // one exists, small props and light layers are meaningful instead of
        // floating on black, so "Balanced" keeps them. Stretched effect gradients
        // stay out unless the user explicitly asks for Everything.
        var hasBackground = layers.Any(layer => !layer.Upscaled && CoversCanvasArea(layer, canvasArea));
        var selected = composition switch
        {
            SceneComposition.Full => layers.ToList(),
            SceneComposition.Background => layers.Where(layer => CoversMostOfCanvas(layer, canvasArea)).ToList(),
            _ => hasBackground
                ? layers.Where(layer => !layer.Upscaled).ToList()
                : SelectLayers(layers.Where(layer => !layer.Upscaled), canvasArea)
        };

        if (selected.Count == 0)
        {
            selected = SelectLayers(layers, canvasArea);
        }

        if (selected.Count == 0)
        {
            // The scene has no image that covers its projection, so show the
            // largest one; the canvas itself must keep the projected size.
            var largest = layers[0];
            double largestFootprint = 0;
            foreach (var layer in layers)
            {
                var area = FootprintWidth(layer) * FootprintHeight(layer);
                if (area > largestFootprint)
                {
                    largestFootprint = area;
                    largest = layer;
                }
            }

            selected = [largest];
        }

        var selectionSnapshot = VerboseLayers ? selected.ToHashSet() : null;

        // A wallpaper built from a single image should fill the frame even when the
        // author left the scene projection smaller than the artwork, but a small
        // object must not be blown up to cover the whole canvas.
        if (selected.Count == 1 && CoversCanvasArea(selected[0], canvasArea))
        {
            selected[0] = selected[0] with { Fullscreen = true };
        }

        if (VerboseLayers && selectionSnapshot is not null)
        {
            Logger.Debug($"scene plan {composition}: canvas={canvasWidth}x{canvasHeight} candidates={layers.Count} selected={selected.Count}");
            foreach (var layer in layers)
            {
                Logger.Debug(
                    $"  layer {layer.PackageName}: tex={layer.PictureWidth}x{layer.PictureHeight} " +
                    $"obj={layer.ObjectWidth}x{layer.ObjectHeight} " +
                    $"screen={FootprintWidth(layer):F0}x{FootprintHeight(layer):F0} " +
                    $"fullscreen={layer.Fullscreen} upscaled={layer.Upscaled} blend={layer.BlendMode} " +
                    $"selected={selectionSnapshot.Contains(layer)}");
            }
        }

        return new ScenePlan(canvasWidth, canvasHeight, selected);
    }

    private static List<SceneLayer> SelectLayers(IEnumerable<SceneLayer> layers, double canvasArea)
        => layers.Where(layer => CoversCanvasArea(layer, canvasArea)).ToList();

    /// <summary>
    /// A backdrop fills most of the frame: a fullscreen image, or one drawn at
    /// roughly the canvas size. Large foreground objects are deliberately not
    /// enough, so "Background only" really leaves them out.
    /// </summary>
    private static bool CoversMostOfCanvas(SceneLayer layer, double canvasArea)
    {
        if (layer.Fullscreen)
        {
            return true;
        }

        var area = FootprintWidth(layer) * FootprintHeight(layer);
        return area >= canvasArea * 0.75;
    }

    /// <summary>
    /// A layer earns a place in the frame when it is roughly as large as the
    /// canvas. Tiny props are skipped because nothing else in the scene is drawn
    /// either, and leaving them floating on black looks like a bug.
    /// </summary>
    private static bool CoversCanvasArea(SceneLayer layer, double canvasArea)
    {
        if (layer.Fullscreen)
        {
            return true;
        }

        var area = FootprintWidth(layer) * FootprintHeight(layer);
        return area >= canvasArea * 0.4;
    }

    private static double FootprintWidth(SceneLayer layer) => Footprint(layer).Width;

    private static double FootprintHeight(SceneLayer layer) => Footprint(layer).Height;

    /// <summary>
    /// How large the layer is actually drawn on screen, before rotation widens its
    /// bounding box. Used to tell a stretched low-resolution effect from artwork
    /// that merely sits in a rotated or scaled group.
    /// </summary>
    private static (double Width, double Height) DrawnSize(SceneLayer layer)
    {
        var width = layer.ObjectWidth > 0 ? layer.ObjectWidth : layer.PictureWidth;
        var height = layer.ObjectHeight > 0 ? layer.ObjectHeight : layer.PictureHeight;
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        if (!layer.HasTransform)
        {
            return (width, height);
        }

        var matrix = layer.World;
        var scaleX = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var scaleY = Math.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
        return (width * scaleX, height * scaleY);
    }

    private static (double Width, double Height) Footprint(SceneLayer layer)
    {
        var width = layer.ObjectWidth > 0 ? layer.ObjectWidth : layer.PictureWidth;
        var height = layer.ObjectHeight > 0 ? layer.ObjectHeight : layer.PictureHeight;
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        if (!layer.HasTransform)
        {
            return (width, height);
        }

        var halfWidth = width / 2;
        var halfHeight = height / 2;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[]
                 {
                     (-halfWidth, -halfHeight), (halfWidth, -halfHeight),
                     (halfWidth, halfHeight), (-halfWidth, halfHeight)
                 })
        {
            var (tx, ty) = Apply(layer.World, x, y);
            minX = Math.Min(minX, tx);
            minY = Math.Min(minY, ty);
            maxX = Math.Max(maxX, tx);
            maxY = Math.Max(maxY, ty);
        }

        return (maxX - minX, maxY - minY);
    }

    private static (double X, double Y) Apply(SKMatrix matrix, double x, double y)
        => (matrix.ScaleX * x + matrix.SkewX * y + matrix.TransX,
            matrix.SkewY * x + matrix.ScaleY * y + matrix.TransY);

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
            best = new SceneLayer(
                name,
                width,
                height,
                width,
                height,
                SKMatrix.Identity,
                HasTransform: false,
                Fullscreen: true,
                Alpha: 1.0,
                SKBlendMode.SrcOver,
                imageType,
                format,
                0,
                0,
                width,
                height,
                Spritesheet: false,
                GridColumns: 1,
                GridRows: 1,
                Upscaled: false);
        }

        return best is null
            ? null
            : new ScenePlan(best.PictureWidth, best.PictureHeight, [best]);
    }

    private static SKMatrix ResolveWorld(
        JsonElement obj,
        Dictionary<string, JsonElement> byId,
        Dictionary<string, SKMatrix> cache,
        HashSet<string> visiting,
        int depth)
    {
        var hasId = TryGetId(obj, "id", out var id);
        if (hasId && cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var local = LocalMatrix(obj);
        if (hasId && !visiting.Add(id))
        {
            return local;
        }

        if (depth < 32 && TryGetId(obj, "parent", out var parentId) && byId.TryGetValue(parentId, out var parent))
        {
            var parentWorld = ResolveWorld(parent, byId, cache, visiting, depth + 1);
            local = Compose(parentWorld, local);
        }

        if (hasId)
        {
            visiting.Remove(id);
            cache[id] = local;
        }

        return local;
    }

    private static bool TryGetId(JsonElement element, string name, out string id)
    {
        id = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.Number:
                id = property.GetRawText();
                return id.Length > 0;
            case JsonValueKind.String:
                id = property.GetString() ?? string.Empty;
                return id.Length > 0;
            default:
                return false;
        }
    }

    private static SceneLayer? TryBuildLayer(ISceneAssets assets, JsonElement obj, SKMatrix world)
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

        // Puppet and model images are sprite atlases assembled by the engine. Drawing
        // the flat texture would show loose body parts, so they are not composited.
        if (TryGetString(imageJson, "puppet", out _) || TryGetString(imageJson, "model", out _))
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
            !TryGetFirstTexture(assets, materialJson, out var textureName, out var blending, out var spritesheet))
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

        ReadDimension(imageJson, "width", out var declaredWidth);
        ReadDimension(imageJson, "height", out var declaredHeight);

        // Spritesheets store many animation frames in one texture. Crop to a single
        // cell (the busiest one) instead of drawing every frame at once.
        var sourceWidth = width;
        var sourceHeight = height;
        var gridColumns = 1;
        var gridRows = 1;
        if (spritesheet)
        {
            var cellWidth = declaredWidth > 0 ? declaredWidth : objectWidth;
            var cellHeight = declaredHeight > 0 ? declaredHeight : objectHeight;
            if (cellWidth > 0 && cellHeight > 0)
            {
                var columns = (int)Math.Floor((width + 0.5) / cellWidth);
                var rows = (int)Math.Floor((height + 0.5) / cellHeight);
                var fits = columns >= 1 && rows >= 1 && columns * rows > 1 &&
                           columns * cellWidth <= width + 1 && rows * cellHeight <= height + 1 &&
                           width - columns * cellWidth <= cellWidth && height - rows * cellHeight <= cellHeight;
                if (fits)
                {
                    gridColumns = columns;
                    gridRows = rows;
                    sourceWidth = (int)Math.Round(cellWidth);
                    sourceHeight = (int)Math.Round(cellHeight);
                }
            }
        }

        if (gridColumns == 1 && gridRows == 1 &&
            declaredWidth > 0 && declaredHeight > 0 &&
            declaredWidth <= width && declaredHeight <= height &&
            (declaredWidth < width - 1 || declaredHeight < height - 1))
        {
            // Padded texture: the real picture sits in the top-left corner.
            sourceWidth = (int)Math.Round(declaredWidth);
            sourceHeight = (int)Math.Round(declaredHeight);
        }

        if (objectWidth <= 0)
        {
            objectWidth = sourceWidth;
        }

        if (objectHeight <= 0)
        {
            objectHeight = sourceHeight;
        }

        var additive = (TryGetInt(obj, "colorBlendMode", out var objectBlend) && objectBlend == 31) ||
                       (TryGetInt(imageJson, "colorBlendMode", out var imageBlend) && imageBlend == 31);
        var blend = additive ? SKBlendMode.Plus : MapBlend(blending);

        // A layer without any transform keeps the old behaviour of covering the
        // canvas. Parent group transforms mean even "default" children are placed.
        var hasTransform = !world.IsIdentity || fullscreen;

        var layer = new SceneLayer(
            packageName,
            width,
            height,
            objectWidth,
            objectHeight,
            world,
            hasTransform,
            fullscreen,
            alpha,
            blend,
            imageType,
            format,
            0,
            0,
            sourceWidth,
            sourceHeight,
            spritesheet,
            gridColumns,
            gridRows,
            Upscaled: false);

        // Low-resolution effect gradients are stretched many times over the scene
        // and would paint the whole frame with a tint. Flag them so they only take
        // part when nothing else covers the canvas. Full-resolution artwork is never
        // flagged, even when it is scaled up for a parallax background: dropping it
        // would leave the scene's character floating on black.
        if (!fullscreen && layer.HasTransform)
        {
            var drawn = DrawnSize(layer);
            var ratio = Math.Max(
                drawn.Width / Math.Max(1, sourceWidth),
                drawn.Height / Math.Max(1, sourceHeight));
            var detailed = Math.Max(sourceWidth, sourceHeight) >= EffectGradientMaxEdge;
            if (!detailed && ratio > MaxUpscale)
            {
                layer = layer with { Upscaled = true };
            }
        }

        return layer;
    }

    private static bool TryGetFirstTexture(
        ISceneAssets assets,
        JsonElement materialJson,
        out string name,
        out string? blending,
        out bool spritesheet)
    {
        name = string.Empty;
        blending = null;
        spritesheet = false;
        if (materialJson.ValueKind != JsonValueKind.Object ||
            !materialJson.TryGetProperty("passes", out var passes) ||
            passes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        TryReadSpriteSheetCombo(materialJson, out spritesheet);

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
                if (string.IsNullOrWhiteSpace(value) || IsUtilityAsset(value) || !IsColourTextureAsset(assets, value))
                {
                    continue;
                }

                name = value;
                TryGetString(pass, "blending", out var passBlending);
                blending = passBlending;
                TryReadSpriteSheetCombo(pass, out spritesheet);
                return true;
            }
        }

        return false;
    }

    private static void TryReadSpriteSheetCombo(JsonElement materialJson, out bool spritesheet)
    {
        spritesheet = false;
        if (!materialJson.TryGetProperty("combos", out var combos) || combos.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!combos.TryGetProperty("SPRITESHEET", out var value))
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                spritesheet = value.GetDouble() != 0;
                break;
            case JsonValueKind.True:
                spritesheet = true;
                break;
            case JsonValueKind.String:
                spritesheet = !string.IsNullOrWhiteSpace(value.GetString()) &&
                              !value.GetString()!.Equals("0", StringComparison.Ordinal);
                break;
        }
    }

    private static bool IsColourTextureAsset(ISceneAssets assets, string name)
    {
        var packageName = "materials/" + name + ".tex";
        return assets.TryReadRange(packageName, HeaderProbeBytes, out var header) &&
               TexDecoder.TryReadInfo(header, out _, out _, out var imageType, out var format) &&
               IsColourTexture(imageType, format);
    }

    private static bool ReadVector2(JsonElement element, string name, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return false;
        }

        // Animated or user-bound properties store the current/default value in "value".
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("value", out var inner))
        {
            value = inner;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var parts = (value.GetString() ?? string.Empty)
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            {
                return true;
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = value.EnumerateArray().ToArray();
        if (values.Length >= 2 &&
            values[0].ValueKind == JsonValueKind.Number &&
            values[1].ValueKind == JsonValueKind.Number)
        {
            x = values[0].GetDouble();
            y = values[1].GetDouble();
            return true;
        }

        return false;
    }

    private static bool ReadDimension(JsonElement element, string name, out double value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("value", out var inner))
        {
            property = inner;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            value = property.GetDouble();
            return value > 0;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

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

        if (size.ValueKind == JsonValueKind.Object &&
            size.TryGetProperty("value", out var inner))
        {
            size = inner;
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
            case JsonValueKind.Object:
                // User-adjustable property: its default is stored in "value".
                if (property.TryGetProperty("value", out var inner) &&
                    inner.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = inner.ValueKind == JsonValueKind.True;
                    return true;
                }

                return false;
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

        if (property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("value", out var inner) &&
            inner.ValueKind == JsonValueKind.Number)
        {
            value = inner.GetDouble();
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

    private static string CachePath(WallpaperSource source, SceneComposition composition)
    {
        var key = !string.IsNullOrWhiteSpace(source.FileHash) && source.FileHash.Length >= 16
            ? source.FileHash[..16]
            : ShortHash(source.SourcePath);

        // Bump this whenever the compositor changes so old frames are re-extracted
        // instead of being served from a previous version's cache.
        var variant = composition switch
        {
            SceneComposition.Full => "full",
            SceneComposition.Background => "background",
            _ => "balanced"
        };
        return Path.Combine(AppPaths.FrameCacheDirectory, $"scene-v4-{variant}-{key}.jpg");
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
        SKMatrix World,
        bool HasTransform,
        bool Fullscreen,
        double Alpha,
        SKBlendMode BlendMode,
        int ImageType,
        int Format,
        int SourceX,
        int SourceY,
        int SourceWidth,
        int SourceHeight,
        bool Spritesheet,
        int GridColumns,
        int GridRows,
        bool Upscaled);

    private sealed record ScenePlan(int CanvasWidth, int CanvasHeight, IReadOnlyList<SceneLayer> Layers);
}
