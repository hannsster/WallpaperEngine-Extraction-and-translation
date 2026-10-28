using System.Diagnostics;

namespace WallpaperStudio.Infrastructure;

/// <summary>
/// The handful of places where the app has to talk to the host operating system.
/// Everything else is plain .NET file and process code that behaves the same on
/// Windows, Linux and macOS.
/// </summary>
public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
    public static bool IsLinux { get; } = OperatingSystem.IsLinux();
    public static bool IsMacOS { get; } = OperatingSystem.IsMacOS();

    /// <summary>".exe" on Windows, an empty string everywhere else.</summary>
    public static string ExecutableSuffix => IsWindows ? ".exe" : string.Empty;

    /// <summary>Adds the platform suffix to a tool name (ffmpeg -> ffmpeg, steamcmd -> steamcmd.sh).</summary>
    public static string ToolName(string baseName, string? windowsName = null, string? unixName = null)
    {
        if (IsWindows)
        {
            return windowsName ?? baseName + ".exe";
        }

        return unixName ?? baseName;
    }
}

/// <summary>Opens folders and reveals files with the host file manager.</summary>
public static class PlatformShell
{
    public static bool TryOpenFolder(string path)
    {
        try
        {
            if (PlatformInfo.IsWindows)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = PlatformInfo.IsMacOS ? "open" : "xdg-open",
                Arguments = $"\"{path}\"",
                UseShellExecute = false
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not open '{path}' in the file manager.", ex);
            return false;
        }
    }

    public static bool TryRevealFile(string path)
    {
        if (PlatformInfo.IsWindows)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not reveal '{path}'.", ex);
                return false;
            }
        }

        // Every Linux and macOS file manager understands "open the containing folder".
        return TryOpenFolder(Path.GetDirectoryName(path) ?? path);
    }
}
