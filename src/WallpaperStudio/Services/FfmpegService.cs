using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WallpaperStudio.Services;

public sealed record MediaInfo(int Width, int Height, double DurationSeconds, string? Codec, string? Error)
{
    public static MediaInfo Unknown(string? error = null) => new(0, 0, 0, null, error);
    public bool HasResolution => Width > 0 && Height > 0;
}

public sealed record FrameExtractionResult(string Path, double TimeSeconds, string? Warning);

public sealed class FfmpegUnavailableException : Exception
{
    public FfmpegUnavailableException()
        : base("FFmpeg was not found. Set the FFmpeg folder in Settings, place ffmpeg.exe beside the app or in an 'ffmpeg' subfolder, or add it to PATH.")
    {
    }
}

public sealed class Toolchain
{
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public bool HasFfmpeg => !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath);
    public bool HasFfprobe => !string.IsNullOrWhiteSpace(FfprobePath) && File.Exists(FfprobePath);

    public string StatusText => HasFfmpeg
        ? (HasFfprobe ? "FFmpeg and ffprobe ready" : "FFmpeg ready (ffprobe not found; using ffmpeg probing)")
        : "FFmpeg not found";
}

public sealed partial class FfmpegService
{
    private Toolchain _toolchain = new();

    public Toolchain Toolchain => _toolchain;

    public Toolchain Locate(AppSettings settings)
    {
        var ffmpeg = FindExecutable(settings.FfmpegPath, "ffmpeg.exe");
        var ffprobe = FindSibling(ffmpeg, "ffprobe.exe") ?? FindExecutable(settings.FfmpegPath, "ffprobe.exe");
        _toolchain = new Toolchain { FfmpegPath = ffmpeg, FfprobePath = ffprobe };
        Logger.Info($"Toolchain: ffmpeg='{ffmpeg ?? "missing"}', ffprobe='{ffprobe ?? "missing"}'.");
        return _toolchain;
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(PathHelper.ToExtendedPath(path)))
        {
            return MediaInfo.Unknown("File not found.");
        }

        if (_toolchain.HasFfprobe)
        {
            var result = await ProbeWithFfprobeAsync(path, cancellationToken).ConfigureAwait(false);
            if (result.HasResolution)
            {
                return result;
            }
        }

        if (_toolchain.HasFfmpeg)
        {
            return await ProbeWithFfmpegAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return MediaInfo.Unknown("FFmpeg is not configured.");
    }

    private async Task<MediaInfo> ProbeWithFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await RunAsync(
                _toolchain.FfprobePath!,
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height,codec_name:format=duration",
                    "-of", "json",
                    path
                ],
                30,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ffprobe failed for '{path}'.", ex);
            return MediaInfo.Unknown("ffprobe could not read this file.");
        }

        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            return MediaInfo.Unknown("ffprobe returned no data.");
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var width = 0;
            var height = 0;
            string? codec = null;

            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var stream = streams[0];
                if (stream.TryGetProperty("width", out var widthElement))
                {
                    width = widthElement.GetInt32();
                }

                if (stream.TryGetProperty("height", out var heightElement))
                {
                    height = heightElement.GetInt32();
                }

                if (stream.TryGetProperty("codec_name", out var codecElement))
                {
                    codec = codecElement.GetString();
                }
            }

            var duration = 0d;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationElement))
            {
                if (durationElement.ValueKind == JsonValueKind.String &&
                    double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    duration = parsed;
                }
                else if (durationElement.ValueKind == JsonValueKind.Number)
                {
                    duration = durationElement.GetDouble();
                }
            }

            return new MediaInfo(width, height, duration, codec, null);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not parse ffprobe output.", ex);
            return MediaInfo.Unknown("Could not parse video metadata.");
        }
    }

    private async Task<MediaInfo> ProbeWithFfmpegAsync(string path, CancellationToken cancellationToken)
    {
        // ffmpeg has no "probe" mode; it writes stream details to stderr while exiting non-zero.
        var result = await RunAsync(
            _toolchain.FfmpegPath!,
            ["-hide_banner", "-nostdin", "-i", path],
            30,
            cancellationToken,
            throwOnNonZero: false).ConfigureAwait(false);

        var stderr = result.Stderr;
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return MediaInfo.Unknown("FFmpeg returned no metadata.");
        }

        var duration = 0d;
        var durationMatch = DurationRegex().Match(stderr);
        if (durationMatch.Success)
        {
            var hours = int.Parse(durationMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(durationMatch.Groups["m"].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(durationMatch.Groups["s"].Value, CultureInfo.InvariantCulture);
            duration = hours * 3600 + minutes * 60 + seconds;
        }

        var width = 0;
        var height = 0;
        var videoLine = stderr.Split('\n')
            .FirstOrDefault(line => line.Contains("Video:", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(videoLine))
        {
            var sizeMatch = SizeRegex().Match(videoLine);
            if (sizeMatch.Success)
            {
                width = int.Parse(sizeMatch.Groups["w"].Value, CultureInfo.InvariantCulture);
                height = int.Parse(sizeMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
            }
        }

        return new MediaInfo(width, height, duration, null, width > 0 ? null : "Could not read video dimensions.");
    }

    public async Task<FrameExtractionResult> ExtractFrameAsync(
        string videoPath,
        double timeSeconds,
        string outputPath,
        CancellationToken cancellationToken)
    {        if (!_toolchain.HasFfmpeg)
        {
            throw new FfmpegUnavailableException();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        TryDelete(outputPath);

        var warning = await TryExtractAsync(videoPath, timeSeconds, outputPath, cancellationToken).ConfigureAwait(false);
        if (warning is not null && timeSeconds > 0.05)
        {
            // Seeking near the end or a broken keyframe can fail; zero is a safe retry.
            TryDelete(outputPath);
            await TryExtractAsync(videoPath, 0, outputPath, cancellationToken).ConfigureAwait(false);
            if (File.Exists(outputPath))
            {
                return new FrameExtractionResult(outputPath, 0, "Requested timestamp could not be decoded; frame 0 was used.");
            }
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("FFmpeg did not produce a frame. The video codec may be unsupported.");
        }

        return new FrameExtractionResult(outputPath, timeSeconds, warning);
    }

    /// <summary>
    /// Renders a filmstrip of evenly spaced frames so the user can pick a good
    /// moment instead of guessing a timestamp. Scores come from
    /// <see cref="FrameAnalyzer"/> and are used to suggest the best frame.
    /// </summary>
    public async Task<IReadOnlyList<FrameSample>> ExtractFilmstripAsync(
        string videoPath,
        double durationSeconds,
        int count,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!_toolchain.HasFfmpeg)
        {
            throw new FfmpegUnavailableException();
        }

        count = Math.Clamp(count, 4, 24);
        if (durationSeconds <= 0.25)
        {
            return [];
        }

        var start = durationSeconds > 6 ? durationSeconds * 0.02 : 0;
        var end = durationSeconds * 0.96;
        var span = Math.Max(0.5, end - start);
        var safeKey = string.IsNullOrWhiteSpace(cacheKey) ? "video" : cacheKey[..Math.Min(16, cacheKey.Length)];
        var directory = Path.Combine(AppPaths.FrameCacheDirectory, "filmstrip");
        Directory.CreateDirectory(directory);

        var pattern = Path.Combine(directory, $"{safeKey}-{count}-%03d.jpg");
        var existing = Enumerable.Range(1, count)
            .Select(index => Path.Combine(directory, $"{safeKey}-{count}-{index:000}.jpg"))
            .ToList();

        if (existing.Any(path => !File.Exists(path)))
        {
            foreach (var path in existing)
            {
                TryDelete(path);
            }

            var fps = Math.Max(0.01, count / span);
            await RunAsync(
                _toolchain.FfmpegPath!,
                [
                    "-hide_banner",
                    "-nostdin",
                    "-loglevel", "error",
                    "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", videoPath,
                    "-t", span.ToString("0.###", CultureInfo.InvariantCulture),
                    "-vf", $"fps={fps.ToString("0.####", CultureInfo.InvariantCulture)},scale=480:-2",
                    "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                    "-q:v", "4",
                    "-y",
                    pattern
                ],
                180,
                cancellationToken,
                throwOnNonZero: false).ConfigureAwait(false);
        }

        var samples = new List<FrameSample>(count);
        for (var index = 1; index <= count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, $"{safeKey}-{count}-{index:000}.jpg");
            if (!File.Exists(path))
            {
                continue;
            }

            var time = start + ((index - 0.5) * span / count);
            var quality = FrameAnalyzer.AnalyzeFile(path);
            samples.Add(new FrameSample
            {
                Index = index,
                Path = path,
                TimeSeconds = Math.Clamp(time, 0, Math.Max(0, durationSeconds - 0.1)),
                Score = quality.Score,
                IsAllBlack = quality.IsAllBlack,
                IsWeak = quality.IsWeak
            });
        }

        Logger.Debug($"Filmstrip for '{Path.GetFileName(videoPath)}': {samples.Count} frame(s), span {start:0.0}-{end:0.0}s.");
        return samples;
    }

    private async Task<string?> TryExtractAsync(
        string videoPath,
        double timeSeconds,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var time = Math.Max(0, timeSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var result = await RunAsync(
            _toolchain.FfmpegPath!,
            [
                "-hide_banner",
                "-nostdin",
                "-loglevel", "error",
                "-ss", time,
                "-i", videoPath,
                "-map", "0:v:0",
                "-frames:v", "1",
                "-y",
                outputPath
            ],
            120,
            cancellationToken,
            throwOnNonZero: false).ConfigureAwait(false);

        if (!File.Exists(outputPath) && !string.IsNullOrWhiteSpace(result.Stderr))
        {
            return result.Stderr.Trim();
        }

        return null;
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool throwOnNonZero = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{Path.GetFileName(executable)} timed out after {timeoutSeconds} seconds.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await outputTask.ConfigureAwait(false);
        var stderr = await errorTask.ConfigureAwait(false);

        if (throwOnNonZero && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with {process.ExitCode}: {Truncate(stderr)}");
        }

        return new ProcessResult(stdout, stderr, process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not delete temp frame '{path}': {ex.Message}");
        }
    }

    private static string? FindExecutable(string? configured, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (File.Exists(trimmed))
            {
                return trimmed;
            }

            if (Directory.Exists(trimmed))
            {
                var candidate = Path.Combine(trimmed, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", fileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string? FindSibling(string? executable, string siblingName)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(executable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            var candidate = Path.Combine(directory, siblingName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string value, int max = 500)
        => value.Length <= max ? value : value[..max] + "…";

    private sealed record ProcessResult(string Stdout, string Stderr, int ExitCode);

    [GeneratedRegex(@"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Video:.*?,\s*(?<w>\d{2,5})x(?<h>\d{2,5})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
}
