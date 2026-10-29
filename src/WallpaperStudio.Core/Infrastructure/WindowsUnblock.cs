using System.Runtime.InteropServices;

namespace WallpaperStudio.Infrastructure;

/// <summary>
/// Removes the Mark-of-the-Web (the <c>Zone.Identifier</c> alternate data stream)
/// from the executables that ship beside the app. An unsigned build that keeps
/// that mark makes Windows show the SmartScreen prompt on every launch, not just
/// the first one; once the mark is gone, later launches are quiet. This runs
/// silently at startup and can never block the app.
/// </summary>
public static class WindowsUnblock
{
    private const string ZoneIdentifierStream = ":Zone.Identifier";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFileW(string fileName);

    /// <summary>Unblocks the running executable and any other .exe beside it.</summary>
    public static void Cleanup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var unblocked = new List<string>();

            // The file that was actually launched is the one Windows checks.
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && Unblock(processPath))
            {
                unblocked.Add(Path.GetFileName(processPath));
            }

            var directory = AppContext.BaseDirectory;
            if (Directory.Exists(directory))
            {
                foreach (var exe in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (unblocked.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Unblock(exe))
                    {
                        unblocked.Add(Path.GetFileName(exe));
                    }
                }
            }

            if (unblocked.Count > 0)
            {
                Logger.Info($"Removed the Windows download mark from: {string.Join(", ", unblocked)}.");
            }
        }
        catch (Exception ex)
        {
            // Never worth interrupting startup over.
            Logger.Warn("Could not remove the Windows download mark from the executable.", ex);
        }
    }

    /// <summary>
    /// Deletes <c>path:Zone.Identifier</c>. The managed File API rejects stream
    /// paths, so the Win32 call is used directly. A missing stream is expected and
    /// simply returns false.
    /// </summary>
    private static bool Unblock(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return DeleteFileW(path + ZoneIdentifierStream);
        }
        catch
        {
            return false;
        }
    }
}
