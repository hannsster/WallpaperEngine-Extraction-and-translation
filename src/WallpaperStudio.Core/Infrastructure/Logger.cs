using System.Diagnostics;
using System.Text;

namespace WallpaperStudio.Infrastructure;

/// <summary>Simple daily rolling log file. Logging never throws into the UI path.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string? _logFilePath;

    public static string? LogFilePath => _logFilePath;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            _logFilePath = Path.Combine(AppPaths.LogDirectory, $"wallpaper-studio-{DateTime.Now:yyyyMMdd}.log");
            Info($"Application started. Version={typeof(Logger).Assembly.GetName().Version}; Portable={AppPaths.PortableMode}; Data={AppPaths.DataDirectory}");
        }
        catch
        {
            // Logging must never prevent startup.
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            if (_logFilePath is null)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"));
            builder.Append(" [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                builder.AppendLine();
                builder.Append(exception);
            }

            lock (Gate)
            {
                File.AppendAllText(_logFilePath, builder.ToString() + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Logger failure: {ex}");
        }
    }

    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            PlatformShell.TryOpenFolder(AppPaths.LogDirectory);
        }
        catch (Exception ex)
        {
            Error("Could not open log folder.", ex);
        }
    }
}
