using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WallpaperStudio.Services;

/// <summary>
/// Read-only discovery of a locally installed Wallpaper Engine. There is no
/// supported CLI for importing wallpapers or publishing to the Workshop, so
/// this service only locates and optionally launches the normal application.
/// </summary>
public sealed partial class WallpaperEngineLocator
{
    public string? Locate(AppSettings settings, out string status)
    {
        if (!string.IsNullOrWhiteSpace(settings.WallpaperEnginePath))
        {
            var configured = ResolveExecutable(settings.WallpaperEnginePath);
            if (configured is not null)
            {
                status = "Wallpaper Engine path from Settings.";
                return configured;
            }

            status = "The Wallpaper Engine path in Settings does not contain wallpaper32.exe or wallpaper64.exe.";
        }
        else
        {
            status = "Wallpaper Engine was not detected. Set its executable in Settings if you want a launch shortcut.";
        }

        foreach (var candidate in DiscoverCandidates())
        {
            if (File.Exists(candidate))
            {
                status = "Wallpaper Engine detected.";
                return candidate;
            }
        }

        return null;
    }

    public bool TryLaunch(string? executable, out string message)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            message = "Wallpaper Engine was not found. Set the executable in Settings.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty
            });
            message = "Wallpaper Engine launched.";
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not launch Wallpaper Engine.", ex);
            message = "Wallpaper Engine could not be launched. Details were written to the log.";
            return false;
        }
    }

    private static IEnumerable<string> DiscoverCandidates()
    {
        foreach (var steamRoot in SteamRoots())
        {
            foreach (var library in SteamLibraries(steamRoot))
            {
                yield return Path.Combine(library, "steamapps", "common", "wallpaper_engine", "wallpaper64.exe");
                yield return Path.Combine(library, "steamapps", "common", "wallpaper_engine", "wallpaper32.exe");
            }
        }

        foreach (var uninstall in UninstallHives())
        {
            string? installLocation = null;
            string? displayIcon = null;
            string? displayName = null;
            try
            {
                displayName = uninstall.GetValue("DisplayName") as string;
                installLocation = uninstall.GetValue("InstallLocation") as string;
                displayIcon = uninstall.GetValue("DisplayIcon") as string;
            }
            catch
            {
                continue;
            }

            if (displayName is null || !displayName.Contains("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                yield return Path.Combine(installLocation, "wallpaper64.exe");
                yield return Path.Combine(installLocation, "wallpaper32.exe");
            }

            if (!string.IsNullOrWhiteSpace(displayIcon))
            {
                var iconPath = displayIcon.Trim().Trim('"');
                var comma = iconPath.LastIndexOf(',');
                if (comma > 2)
                {
                    iconPath = iconPath[..comma].Trim('"');
                }

                if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return iconPath;
                }
            }
        }

        yield return @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe";
        yield return @"C:\Program Files\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe";
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        foreach (var (hive, path) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                var value = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    roots.Add(value.Replace('/', '\\'));
                }
            }
            catch
            {
                // Registry access is best effort.
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SteamLibraries(string steamRoot)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                var text = File.ReadAllText(vdf);
                foreach (Match match in LibraryPathRegex().Matches(text))
                {
                    var path = match.Groups["path"].Value.Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        libraries.Add(path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read Steam library folders: {ex.Message}");
        }

        return libraries;
    }

    private static IEnumerable<RegistryKey> UninstallHives()
    {
        var paths = new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, path) in paths)
        {
            RegistryKey? root = null;
            try
            {
                root = hive.OpenSubKey(path);
            }
            catch
            {
                // Best effort.
            }

            if (root is null)
            {
                continue;
            }

            using (root)
            {
                foreach (var name in SafeSubKeyNames(root))
                {
                    RegistryKey? child = null;
                    try
                    {
                        child = root.OpenSubKey(name);
                    }
                    catch
                    {
                        // Skip inaccessible entries.
                    }

                    if (child is not null)
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch
        {
            return [];
        }
    }

    private static string? ResolveExecutable(string configured)
    {
        try
        {
            var value = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (File.Exists(value) && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (Directory.Exists(value))
            {
                foreach (var name in new[] { "wallpaper64.exe", "wallpaper32.exe" })
                {
                    var candidate = Path.Combine(value, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // Invalid path syntax is reported by the caller.
        }

        return null;
    }

    [GeneratedRegex(@"""path""\s+""(?<path>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
