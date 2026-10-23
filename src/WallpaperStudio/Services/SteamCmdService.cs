using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperStudio.Services;

public sealed class SteamCmdUnavailableException : Exception
{
    public SteamCmdUnavailableException()
        : base("SteamCMD was not found. Download steamcmd.exe from Valve, place it in a 'steamcmd' subfolder next to the app or at C:\\steamcmd, add it to PATH, or set its path in Settings.")
    {
    }
}

public sealed record SteamCmdResult(
    bool Success,
    string? PublishedFileId,
    string? WorkshopUrl,
    string? Error,
    bool NeedsSignIn,
    IReadOnlyList<string> Output);

/// <summary>
/// Thin, honest wrapper around Valve's SteamCMD. It never invents a Steam API,
/// never stores credentials, and only reports success when SteamCMD actually
/// writes a new published file id back into the VDF it was given.
/// </summary>
public sealed partial class SteamCmdService
{
    private static readonly string[] SignInMarkers =
    [
        "login failure", "invalid password", "please login", "please log in",
        "no cached credentials", "two-factor", "steam guard", "account logon denied"
    ];

    private static readonly string[] FailureMarkers =
    [
        "error!", "failed", "failure", "access denied", "does not own",
        "no subscription", "not authorized", "unauthorized", "denied"
    ];

    public string? SteamCmdPath { get; private set; }

    public string? Locate(AppSettings settings, out string status)
    {
        SteamCmdPath = FindSteamCmd(settings.SteamCmdPath);
        if (SteamCmdPath is not null)
        {
            status = "SteamCMD ready · " + SteamCmdPath;
        }
        else
        {
            status = "SteamCMD not found. Download it from Valve (steamcmd.exe) and point Settings at it.";
        }

        Logger.Info($"SteamCMD: '{SteamCmdPath ?? "missing"}'.");
        return SteamCmdPath;
    }

    /// <summary>
    /// Opens the real SteamCMD console so the user can sign in interactively.
    /// The password is typed into Valve's own window and is never seen or stored
    /// by Wallpaper Studio; SteamCMD caches its own session token for later uploads.
    /// </summary>
    public bool TryOpenSignIn(AppSettings settings, out string message)
    {
        var executable = SteamCmdPath ?? FindSteamCmd(settings.SteamCmdPath);
        if (executable is null)
        {
            message = "SteamCMD was not found. Set its path in Settings first.";
            return false;
        }

        var user = settings.SteamUsername?.Trim() ?? string.Empty;
        var arguments = string.IsNullOrWhiteSpace(user) ? string.Empty : $"+login \"{user}\"";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            });

            message = string.IsNullOrWhiteSpace(user)
                ? "SteamCMD opened. Type 'login <your_account>' at its prompt, finish Steam Guard, then close the window. Your password stays inside Valve's program."
                : $"SteamCMD opened with '+login {user}'. Enter your password and Steam Guard code there, then close the window. Wallpaper Studio never sees your password.";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not launch SteamCMD for sign-in.", ex);
            message = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Runs <c>steamcmd +login &lt;user&gt; +workshop_build_item &lt;vdf&gt; +quit</c> with
    /// redirected output. Success is decided only by re-reading the VDF.
    /// </summary>
    public async Task<SteamCmdResult> UploadAsync(
        string vdfPath,
        AppSettings settings,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        if (SteamCmdPath is null || !File.Exists(PathHelper.ToExtendedPath(SteamCmdPath)))
        {
            throw new SteamCmdUnavailableException();
        }

        var user = settings.SteamUsername?.Trim() ?? string.Empty;
        var startInfo = new ProcessStartInfo
        {
            FileName = SteamCmdPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(SteamCmdPath) ?? AppContext.BaseDirectory
        };

        if (!string.IsNullOrWhiteSpace(user))
        {
            startInfo.ArgumentList.Add("+login");
            startInfo.ArgumentList.Add(user);
        }

        startInfo.ArgumentList.Add("+workshop_build_item");
        startInfo.ArgumentList.Add(ToSpaceFreePath(vdfPath));
        startInfo.ArgumentList.Add("+quit");

        var output = new List<string>();
        var gate = new object();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start steamcmd.exe.");
        }

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (gate)
            {
                output.Add(line);
            }

            Logger.Debug($"[steamcmd] {line}");
            progress?.Report(line);
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            // Flush the async readers so nothing is lost after exit.
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("SteamCMD did not finish within 30 minutes. The upload was cancelled.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        string[] snapshot;
        lock (gate)
        {
            snapshot = [.. output];
        }

        var combined = string.Join("\n", snapshot);
        var needsSignIn = SignInMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (WorkshopVdfWriter.TryReadPublishedFileId(vdfPath, out var publishedFileId) &&
            !string.IsNullOrWhiteSpace(publishedFileId))
        {
            var url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={publishedFileId}";
            Logger.Info($"SteamCMD published item {publishedFileId}.");
            return new SteamCmdResult(true, publishedFileId, url, null, false, snapshot);
        }

        var error = needsSignIn
            ? "SteamCMD is not signed in. Use \"Sign in to SteamCMD\" first, then try again."
            : BuildError(combined, process.ExitCode);

        Logger.Warn($"SteamCMD upload failed: {error}");
        return new SteamCmdResult(false, null, null, error, needsSignIn, snapshot);
    }

    private static string BuildError(string output, int exitCode)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FailureMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return line.Length > 400 ? line[..400] : line;
            }
        }

        return $"SteamCMD finished (exit code {exitCode}) but did not report a new Workshop item id. " +
               "The item may not have been created. Check the log pane for the full SteamCMD output.";
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

    private static string? FindSteamCmd(string? configured)
    {
        foreach (var candidate in EnumerateCandidates(configured))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(PathHelper.ToExtendedPath(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var trimmed = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            yield return trimmed;
            if (Directory.Exists(trimmed))
            {
                yield return Path.Combine(trimmed, "steamcmd.exe");
            }
        }

        yield return Path.Combine(AppContext.BaseDirectory, "steamcmd", "steamcmd.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "steamcmd.exe");

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? candidate = null;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), "steamcmd.exe");
            }
            catch
            {
                // Ignore malformed PATH entries.
            }

            if (candidate is not null)
            {
                yield return candidate;
            }
        }

        yield return @"C:\steamcmd\steamcmd.exe";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamcmd", "steamcmd.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamcmd", "steamcmd.exe");
    }

    /// <summary>
    /// SteamCMD historically mishandles a VDF path that contains spaces. When the
    /// path has spaces we hand SteamCMD the 8.3 short form instead, falling back
    /// to the original path when short names are unavailable.
    /// </summary>
    private static string ToSpaceFreePath(string path)
    {
        try
        {
            if (!path.Contains(' '))
            {
                return path;
            }

            var buffer = new StringBuilder(260);
            var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
            if (length == 0)
            {
                return path;
            }

            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length);
                length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
                if (length == 0)
                {
                    return path;
                }
            }

            var shortPath = buffer.ToString();
            return shortPath.Contains(' ') ? path : shortPath;
        }
        catch
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}
