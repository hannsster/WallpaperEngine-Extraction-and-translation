using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace WallpaperStudio.Services.ReverseSearch;

public sealed record DownloadedOriginal(string Path, ResolutionInfo Resolution, FrameQuality Quality, long Bytes);

/// <summary>
/// Orchestrates a reverse-image search: builds a small query JPEG, asks every
/// configured provider in parallel, enriches booru hits with real dimensions,
/// then ranks the results. Never downloads a full-size original; that only
/// happens when the user explicitly chooses one.
/// </summary>
public sealed class ReverseSearchService
{
    public const int QueryMaxEdge = 1280;
    private const long MaximumDownloadBytes = 80L * 1024 * 1024;

    private readonly SettingsService _settings;
    private readonly ThumbnailService _thumbnails;
    private readonly HttpClient _client;
    private readonly ReverseSearchCache _cache = new();
    private readonly ImageDimensionProbe _probe;
    private readonly BooruEnricher _booru;
    private readonly IReadOnlyList<IReverseImageProvider> _providers;
    private readonly SemaphoreSlim _throttle = new(2, 2);
    private readonly ConcurrentDictionary<string, Task<ReverseSearchSnapshot?>> _running = new(StringComparer.OrdinalIgnoreCase);

    public ReverseSearchService(SettingsService settings, ThumbnailService thumbnails)
    {
        _settings = settings;
        _thumbnails = thumbnails;

        var client = ReverseSearchHttp.CreateClient();
        _client = client;
        _probe = new ImageDimensionProbe(client);
        _booru = new BooruEnricher(client);
        _providers =
        [
            new SauceNaoProvider(client),
            new GoogleVisionProvider(client),
            new IqdbProvider(client),
            new TraceMoeProvider(client)
        ];
    }

    public IReadOnlyList<string> ProviderLabels => _providers.Select(provider => provider.Label).ToList();

    /// <summary>True when consent was given and at least one provider can actually be called.</summary>
    public bool IsReady
    {
        get
        {
            var settings = _settings.Current;
            return settings.ReverseSearchEnabled &&
                   settings.ReverseSearchConsent &&
                   _providers.Any(provider => provider.IsEnabled(settings) && provider.IsConfigured(settings));
        }
    }

    public IReadOnlyList<string> ActiveProviders
    {
        get
        {
            var settings = _settings.Current;
            return _providers
                .Where(provider => provider.IsEnabled(settings) && provider.IsConfigured(settings))
                .Select(provider => provider.Label)
                .ToList();
        }
    }

    /// <summary>Provider hints for the UI when nothing is usable.</summary>
    public IReadOnlyList<string> ConfigurationHints
    {
        get
        {
            var settings = _settings.Current;
            return _providers
                .Where(provider => provider.IsEnabled(settings) && !provider.IsConfigured(settings))
                .Select(provider => provider.ConfigurationHint)
                .ToList();
        }
    }

    /// <summary>
    /// Searches for the source of the item's current candidate frame. Returns
    /// null when the feature is off, the frame is missing, or the same query is
    /// already running.
    /// </summary>
    public async Task<ReverseSearchSnapshot?> SearchAsync(
        WallpaperSource item,
        bool automatic,
        bool ignoreCache,
        CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.ReverseSearchEnabled || !settings.ReverseSearchConsent)
        {
            return null;
        }

        var candidate = item.CandidateFramePath;
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(PathHelper.ToExtendedPath(candidate)))
        {
            return null;
        }

        var providers = _providers
            .Where(provider => provider.IsEnabled(settings) && provider.IsConfigured(settings))
            .ToList();

        if (providers.Count == 0)
        {
            return new ReverseSearchSnapshot
            {
                Status = ReverseSearchStatus.NotConfigured,
                SearchedAt = DateTimeOffset.Now,
                Notes = ConfigurationHints.ToList()
            };
        }

        var quality = await Task.Run(() => FrameAnalyzer.AnalyzeFile(candidate), cancellationToken).ConfigureAwait(false);
        if (quality.IsWeak)
        {
            return new ReverseSearchSnapshot
            {
                Status = ReverseSearchStatus.NeedsBetterFrame,
                SearchedAt = DateTimeOffset.Now,
                Notes = [quality.IsAllBlack
                    ? "The selected frame is almost entirely black, so there is nothing to match. Pick another frame."
                    : "This frame has very little visible detail. Pick a frame with the artwork fully on screen."]
            };
        }

        var key = BuildKey(item, candidate, providers, settings);

        if (!ignoreCache)
        {
            var cached = _cache.Load(key);
            if (cached is not null)
            {
                await HydrateThumbnailsAsync(cached, cancellationToken).ConfigureAwait(false);
                return cached;
            }
        }

        var running = _running.GetOrAdd(key, _ => RunAsync(item, key, candidate, providers, settings, automatic));

        try
        {
            return await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (running.IsCompleted)
            {
                _running.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Returns a previously cached result for this source without touching the
    /// network, so chips and review cards survive an app restart or rescan.
    /// </summary>
    public async Task<ReverseSearchSnapshot?> TryGetCachedAsync(
        WallpaperSource item,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (!settings.ReverseSearchEnabled || !settings.ReverseSearchConsent)
        {
            return null;
        }

        var candidate = item.CandidateFramePath;
        if (string.IsNullOrWhiteSpace(candidate) && item.Kind == WallpaperKind.ImageWallpaper)
        {
            candidate = item.PrimaryFilePath;
        }

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(PathHelper.ToExtendedPath(candidate)))
        {
            return null;
        }

        var providers = _providers
            .Where(provider => provider.IsEnabled(settings) && provider.IsConfigured(settings))
            .ToList();

        if (providers.Count == 0)
        {
            return null;
        }

        var key = BuildKey(item, candidate, providers, settings);
        var cached = _cache.Load(key);
        if (cached is null)
        {
            return null;
        }

        await HydrateThumbnailsAsync(cached, cancellationToken).ConfigureAwait(false);
        return cached;
    }

    private async Task<ReverseSearchSnapshot?> RunAsync(
        WallpaperSource item,
        string key,
        string candidate,
        IReadOnlyList<IReverseImageProvider> providers,
        AppSettings settings,
        bool automatic)
    {
        await _throttle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (automatic && !TryConsumeBudget(settings))
            {
                Logger.Info("Reverse search skipped: automatic daily budget reached.");
                return new ReverseSearchSnapshot
                {
                    Status = ReverseSearchStatus.Skipped,
                    SearchedAt = DateTimeOffset.Now,
                    Notes = [$"The automatic search limit for today ({settings.ReverseSearchDailyLimit}) has been reached. You can still search this wallpaper manually, or turn the daily cap off in Settings."]
                };
            }

            var query = await Task.Run(() => BuildQuery(candidate, key), CancellationToken.None).ConfigureAwait(false);

            var snapshot = new ReverseSearchSnapshot
            {
                Key = key,
                QueryImagePath = query.Path,
                QueryWidth = query.Width,
                QueryHeight = query.Height,
                SearchedAt = DateTimeOffset.Now,
                ProvidersQueried = providers.Select(provider => provider.Label).ToList()
            };

            var payload = new ReverseImageQuery(query.Jpeg, query.Width, query.Height, item.Title, item.Kind.Label());

            var responses = await Task.WhenAll(providers.Select(provider => RunProviderAsync(provider, payload, settings))).ConfigureAwait(false);

            var results = new List<ReverseSearchResult>();
            foreach (var response in responses)
            {
                if (response.Note is not null)
                {
                    snapshot.Notes.Add(response.Note);
                }

                results.AddRange(response.Results);
            }

            var failures = responses.Count(response => response.Failed);
            snapshot.Notes.AddRange(responses.Where(response => response.Error is not null).Select(response => response.Error!));

            if (failures == responses.Length)
            {
                snapshot.Status = ReverseSearchStatus.Error;
                return snapshot;
            }

            results = Deduplicate(results);

            if (settings.ReverseSearchEnrichBooru)
            {
                await _booru.EnrichAsync(results, CancellationToken.None).ConfigureAwait(false);
            }

            await ProbeDimensionsAsync(results).ConfigureAwait(false);

            var candidatePixels = item.Resolution.IsKnown
                ? (long)item.Resolution.Width * item.Resolution.Height
                : (long)query.Width * query.Height;

            var floor = settings.ReverseSearchMinSimilarity;
            var accepted = results
                .Where(result => result.Similarity >= (result.IsIdentificationOnly ? Math.Max(0.80, floor) : floor))
                .ToList();

            if (accepted.Count == 0)
            {
                snapshot.Status = ReverseSearchStatus.NoMatch;
                snapshot.Notes.Add(results.Count == 0
                    ? "No provider recognised this frame."
                    : $"All {results.Count} match(es) were below the {floor:P0} confidence floor.");
            }
            else
            {
                Rank(accepted, candidatePixels);
                snapshot.Results = accepted;
                snapshot.Status = ReverseSearchStatus.Results;
            }

            await DownloadThumbnailsAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

            if (snapshot.Status is ReverseSearchStatus.Results or ReverseSearchStatus.NoMatch)
            {
                _cache.Save(snapshot);
            }

            Logger.Info($"Reverse search for '{item.Title}': {snapshot.Results.Count} match(es) via {string.Join(", ", snapshot.ProvidersQueried)}.");
            return snapshot;
        }
        catch (Exception ex)
        {
            Logger.Error("Reverse search failed unexpectedly.", ex);
            return new ReverseSearchSnapshot
            {
                Key = key,
                Status = ReverseSearchStatus.Error,
                SearchedAt = DateTimeOffset.Now,
                Notes = ["The search failed unexpectedly. See the log for details."]
            };
        }
        finally
        {
            _throttle.Release();
        }
    }

    private sealed record ProviderOutcome(
        IReadOnlyList<ReverseSearchResult> Results,
        string? Note,
        string? Error)
    {
        public bool Failed => Error is not null;
    }

    private static async Task<ProviderOutcome> RunProviderAsync(
        IReverseImageProvider provider,
        ReverseImageQuery query,
        AppSettings settings)
    {
        try
        {
            var response = await provider.SearchAsync(query, settings, CancellationToken.None).ConfigureAwait(false);
            return new ProviderOutcome(response.Results, response.Note, null);
        }
        catch (ReverseSearchProviderException ex)
        {
            Logger.Warn($"{provider.Label} search failed: {ex.Message}");
            return new ProviderOutcome([], null, $"{provider.Label}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"{provider.Label} search failed unexpectedly.", ex);
            return new ProviderOutcome([], null, $"{provider.Label}: unexpected error ({ex.GetType().Name}).");
        }
    }

    private static List<ReverseSearchResult> Deduplicate(List<ReverseSearchResult> results)
    {
        var kept = new Dictionary<string, ReverseSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results)
        {
            var key = Normalize(result.PageUrl)
                      ?? Normalize(result.ImageUrl)
                      ?? $"{result.SourceName}|{result.Title}|{result.Artist}";
            if (!kept.TryGetValue(key, out var existing))
            {
                kept[key] = result;
                continue;
            }

            // Keep the more confident copy but never lose a size or direct link.
            var winner = result.Similarity > existing.Similarity ? result : existing;
            var other = ReferenceEquals(winner, result) ? existing : result;
            if (winner.Width <= 0 && other.Width > 0)
            {
                winner.Width = other.Width;
                winner.Height = other.Height;
            }

            winner.ImageUrl ??= other.ImageUrl;
            winner.PageUrl ??= other.PageUrl;
            winner.ThumbnailUrl ??= other.ThumbnailUrl;
            kept[key] = winner;
        }

        return kept.Values.ToList();
    }

    private async Task ProbeDimensionsAsync(List<ReverseSearchResult> results)
    {
        var targets = results
            .Where(result => result.Width <= 0 && !string.IsNullOrWhiteSpace(result.ImageUrl ?? result.PageUrl))
            .OrderByDescending(result => result.Similarity)
            .Take(8)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        var tasks = targets.Select(async result =>
        {
            var probe = await _probe.ProbeAsync(result.ImageUrl ?? result.PageUrl!, CancellationToken.None).ConfigureAwait(false);
            if (probe is null)
            {
                return;
            }

            if (probe.IsImage && probe.HasSize)
            {
                result.Width = probe.Width;
                result.Height = probe.Height;
                // A page URL that turns out to be a real image can be downloaded.
                result.ImageUrl ??= result.PageUrl;
            }
            else if (!probe.IsImage && result.ImageUrl is not null && result.PageUrl == result.ImageUrl)
            {
                // It was only ever a guess; do not offer a download that will fail.
                result.ImageUrl = null;
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static void Rank(List<ReverseSearchResult> results, long candidatePixels)
    {
        foreach (var result in results)
        {
            var pixels = (long)result.Width * result.Height;
            var ratio = pixels > 0 && candidatePixels > 0 ? (double)pixels / candidatePixels : 0;
            result.IsBetterThanCurrent = !result.IsIdentificationOnly &&
                                         pixels > 0 &&
                                         ratio > 1.03 &&
                                         result.Similarity >= 0.75;

            var providerWeight = result.ProviderId switch
            {
                "saucenao" => 1.0,
                "googlevision" => 0.9,
                "iqdb" => 0.85,
                _ => 0.45
            };

            var resolutionScore = ratio > 0 ? Math.Min(1.0, Math.Sqrt(ratio) / 1.5) : 0;
            result.Score = Math.Round(
                (0.55 * result.Similarity) + (0.35 * resolutionScore) + (0.10 * providerWeight),
                4);
        }

        results.Sort((left, right) => right.Score.CompareTo(left.Score));
    }

    private async Task DownloadThumbnailsAsync(ReverseSearchSnapshot snapshot, CancellationToken cancellationToken)
    {
        var targets = snapshot.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            .Take(12)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = targets.Select(async result =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var path = _cache.ThumbnailPath(result.ThumbnailUrl!);
                if (!File.Exists(path))
                {
                    var bytes = await ReverseSearchHttp.GetBytesAsync(
                            _client,
                            result.ThumbnailUrl!,
                            1_500_000,
                            TimeSpan.FromSeconds(8),
                            cancellationToken)
                        .ConfigureAwait(false);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                }

                result.ThumbnailPath = path;
                result.Thumbnail = await _thumbnails.GetAsync(path, 220, "reverse", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Thumbnail download failed for '{result.ThumbnailUrl}': {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task HydrateThumbnailsAsync(ReverseSearchSnapshot snapshot, CancellationToken cancellationToken)
    {
        var targets = snapshot.Results
            .Where(result => !string.IsNullOrWhiteSpace(result.ThumbnailUrl))
            .Take(12)
            .ToList();

        foreach (var result in targets)
        {
            var path = _cache.ThumbnailPath(result.ThumbnailUrl!);
            if (!File.Exists(path))
            {
                continue;
            }

            result.ThumbnailPath = path;
            result.Thumbnail = await _thumbnails.GetAsync(path, 220, "reverse", cancellationToken).ConfigureAwait(false);
        }
    }

    private (byte[] Jpeg, int Width, int Height, string Path) BuildQuery(string candidatePath, string key)
    {
        using var source = SkiaImage.DecodeOriented(candidatePath)
                           ?? throw new ReverseSearchProviderException("The selected frame is not a supported image.");

        using var resized = SkiaImage.ResizeToFit(source, QueryMaxEdge);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        var bytes = data.ToArray();

        var path = _cache.QueryImagePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        return (bytes, resized.Width, resized.Height, path);
    }

    private bool TryConsumeBudget(AppSettings settings)
    {
        if (!settings.ReverseSearchLimitAutomatic)
        {
            // The cap is optional. Counting is skipped entirely so turning it back
            // on later starts from a clean slate instead of an exhausted day.
            return true;
        }

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var usage = _cache.LoadUsage();
        if (!string.Equals(usage.Date, today, StringComparison.Ordinal))
        {
            usage = new ReverseSearchUsage { Date = today };
        }

        if (usage.AutomaticSearches >= settings.ReverseSearchDailyLimit)
        {
            return false;
        }

        usage.AutomaticSearches++;
        _cache.SaveUsage(usage);
        return true;
    }

    public int AutomaticSearchesToday
    {
        get
        {
            var usage = _cache.LoadUsage();
            return string.Equals(usage.Date, DateTime.Now.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
                ? usage.AutomaticSearches
                : 0;
        }
    }

    /// <summary>
    /// Downloads one chosen original, verifies that it decodes and is not blank,
    /// and stores it beside the frame cache. The caller decides whether to use it.
    /// </summary>
    public async Task<DownloadedOriginal> DownloadOriginalAsync(
        ReverseSearchResult result,
        CancellationToken cancellationToken)
    {
        if (!result.CanUseImage)
        {
            throw new ReverseSearchProviderException("This match does not expose a downloadable image. Open the page instead.");
        }

        var bytes = await ReverseSearchHttp.GetBytesAsync(
                _client,
                result.ImageUrl!,
                MaximumDownloadBytes,
                TimeSpan.FromSeconds(90),
                cancellationToken)
            .ConfigureAwait(false);

        var bitmap = await Task.Run(() => SkiaImage.DecodeOriented(bytes), cancellationToken).ConfigureAwait(false);
        if (bitmap is null)
        {
            throw new ReverseSearchProviderException("The downloaded file is not a supported image.");
        }

        using (bitmap)
        {
            var quality = FrameAnalyzer.Analyze(bitmap);
            if (quality.IsAllBlack)
            {
                throw new ReverseSearchProviderException("The downloaded image is almost entirely black, so it was rejected.");
            }

            var extension = DetectExtension(bytes);
            var hash = Convert.ToHexString(SHA1.HashData(bytes))[..16].ToLowerInvariant();
            var path = Path.Combine(AppPaths.FrameCacheDirectory, $"original-{hash}{extension}");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            Logger.Info($"Downloaded original '{path}' ({bitmap.Width}x{bitmap.Height}, {PathHelper.HumanSize(bytes.Length)}).");
            return new DownloadedOriginal(path, new ResolutionInfo(bitmap.Width, bitmap.Height), quality, bytes.Length);
        }
    }

    private static string DetectExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            return ".webp";
        }

        if (bytes.Length >= 3 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
        {
            return ".gif";
        }

        return ".img";
    }

    private static string BuildKey(
        WallpaperSource item,
        string candidate,
        IReadOnlyList<IReverseImageProvider> providers,
        AppSettings settings)
    {
        var info = new FileInfo(PathHelper.ToExtendedPath(candidate));
        // Frame time only matters for extracted video frames; keeping it out of the
        // key for images means a rescan reuses the same cached result.
        var frameTime = item.Kind == WallpaperKind.VideoWallpaper
            ? item.CandidateFrameTime.ToString("0.###")
            : "0";
        var raw = string.Join('|',
            item.FileHash,
            frameTime,
            info.Length,
            info.LastWriteTimeUtc.Ticks,
            string.Join(",", providers.Select(provider => provider.Id).OrderBy(id => id, StringComparer.Ordinal)),
            settings.ReverseSearchMinSimilarity.ToString("0.00"),
            QueryMaxEdge);
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..20].ToLowerInvariant();
    }

    private static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        var query = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (query > 0)
        {
            trimmed = trimmed[..query];
        }

        return trimmed.TrimEnd('/').ToLowerInvariant();
    }
}
