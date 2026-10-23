using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperStudio.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }

    public void Load()
    {
        Current = new AppSettings();
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                ApplyDefaults();
                return;
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null)
            {
                throw new JsonException("settings.json deserialized to null.");
            }

            Current = loaded;
            ApplyDefaults();
        }
        catch (Exception ex)
        {
            LoadWarning = "settings.json was unreadable. Default settings were loaded; the damaged file was kept as a .corrupt copy.";
            Logger.Error("Could not load settings.", ex);
            TryArchiveCorrupt(AppPaths.SettingsFile);
            Current = new AppSettings();
            ApplyDefaults();
        }
    }

    public void Save()
    {
        Current.Normalize();
        var json = JsonSerializer.Serialize(Current, Options);
        AtomicFile.WriteAllText(AppPaths.SettingsFile, json);
        Logger.Info("Settings saved.");
    }

    private void ApplyDefaults()
    {
        Current.Normalize();
        if (string.IsNullOrWhiteSpace(Current.ExportFolder))
        {
            Current.ExportFolder = AppPaths.DefaultExportDirectory;
        }

        if (string.IsNullOrWhiteSpace(Current.SourceFolder) &&
            !string.IsNullOrWhiteSpace(Current.ExportFolder))
        {
            // The source folder is intentionally left empty until the user chooses one.
        }
    }

    private static void TryArchiveCorrupt(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            var destination = $"{file}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(file, destination, overwrite: true);
            Logger.Warn($"Archived corrupt settings file to '{destination}'.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not archive corrupt settings file.", ex);
        }
    }
}

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
