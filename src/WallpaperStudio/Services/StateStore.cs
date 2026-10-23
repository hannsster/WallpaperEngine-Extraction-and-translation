using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallpaperStudio.Services;

/// <summary>
/// Portable export/approval memory stored in state.json. Saves are atomic and
/// a corrupt file is archived instead of blocking the app.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ExportRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PublishRecord> _publishRecords = new(StringComparer.OrdinalIgnoreCase);

    public string? LoadWarning { get; private set; }
    public int RecordCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public int PublishedCount
    {
        get
        {
            lock (_gate)
            {
                return _publishRecords.Values.Count(record => !string.IsNullOrWhiteSpace(record.PublishedFileId));
            }
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            _records.Clear();
            _publishRecords.Clear();
            try
            {
                if (!File.Exists(AppPaths.StateFile))
                {
                    return;
                }

                var json = File.ReadAllText(AppPaths.StateFile);
                var document = JsonSerializer.Deserialize<StateDocument>(json, Options);
                if (document?.Records is null)
                {
                    throw new JsonException("state.json has no records array.");
                }

                foreach (var record in document.Records)
                {
                    var key = PathHelper.Normalize(record.SourcePath);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    _records[key] = record;
                }

                foreach (var record in document.PublishRecords ?? [])
                {
                    var key = PathHelper.Normalize(record.ContentFolder);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    _publishRecords[key] = record;
                }

                Logger.Info($"Loaded {_records.Count} state records and {_publishRecords.Count} publish records.");
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read state.json; starting with empty state.", ex);
                LoadWarning = "state.json was damaged. It was archived and the library memory starts fresh; existing exports are untouched.";
                TryArchiveCorrupt();
                _records.Clear();
                _publishRecords.Clear();
            }
        }
    }

    public ExportRecord? Get(string sourcePath)
    {
        lock (_gate)
        {
            return _records.TryGetValue(PathHelper.Normalize(sourcePath), out var record) ? record : null;
        }
    }

    public void RecordApproval(WallpaperSource source, bool clearExportStatus = false, bool save = true)
    {
        lock (_gate)
        {
            var record = GetOrCreate(source);
            record.ApprovalStatus = source.ApprovalStatus;
            record.Title = source.Title;
            record.SourceType = source.SourceTypeLabel;
            record.Resolution = source.Resolution.Display;
            record.FrameTimestampSeconds = source.CandidateFrameTime;
            record.UsesPreviewFallback = source.UsesPreviewFallback;
            if (clearExportStatus)
            {
                // A newly approved frame after a previous export is a new deliverable;
                // the old JPEG still exists but should not make the next scan look "done".
                record.ExportStatus = ExportStatus.NotExported;
                record.ExportPath = null;
                record.ExportedDate = null;
            }

            if (save)
            {
                SaveUnlocked();
            }
        }
    }

    public void RecordExport(WallpaperSource source, bool save = true)
    {
        lock (_gate)
        {
            var record = GetOrCreate(source);
            record.SourceType = source.SourceTypeLabel;
            record.FileHash = source.FileHash;
            record.LastModified = source.LastModified;
            record.ExportedDate = source.ExportedDate;
            record.ExportPath = source.ExportPath;
            record.Resolution = source.Resolution.Display;
            record.ExportStatus = source.ExportStatus;
            record.ApprovalStatus = source.ApprovalStatus;
            record.FrameTimestampSeconds = source.CandidateFrameTime;
            record.UsesPreviewFallback = source.UsesPreviewFallback;
            record.Title = source.Title;
            if (save)
            {
                SaveUnlocked();
            }
        }
    }

    public void RecordFailure(WallpaperSource source, bool save = true)
    {
        lock (_gate)
        {
            var record = GetOrCreate(source);
            record.ExportStatus = ExportStatus.Failed;
            record.ExportPath = source.ExportPath;
            record.Resolution = source.Resolution.Display;
            record.ApprovalStatus = source.ApprovalStatus;
            record.Title = source.Title;
            if (save)
            {
                SaveUnlocked();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            SaveUnlocked();
        }
    }

    public PublishRecord? GetPublish(string contentFolder)
    {
        if (string.IsNullOrWhiteSpace(contentFolder))
        {
            return null;
        }

        lock (_gate)
        {
            return _publishRecords.TryGetValue(PathHelper.Normalize(contentFolder), out var record) ? record : null;
        }
    }

    public void RecordPublish(PublishItem item, bool save = true)
    {
        lock (_gate)
        {
            var record = GetOrCreatePublish(item);
            record.SourcePath = item.SourcePath;
            record.Title = item.Title;
            record.Kind = item.KindLabel;
            record.Visibility = item.Visibility;
            record.PublishedFileId = item.PublishedFileId;
            record.WorkshopUrl = item.WorkshopUrl;
            record.PublishedDate = DateTimeOffset.Now;
            record.ErrorMessage = null;
            if (save)
            {
                SaveUnlocked();
            }
        }
    }

    public void RecordPublishFailure(PublishItem item, string? error, bool save = true)
    {
        lock (_gate)
        {
            var record = GetOrCreatePublish(item);
            record.SourcePath = item.SourcePath;
            record.Title = item.Title;
            record.Kind = item.KindLabel;
            record.Visibility = item.Visibility;
            record.ErrorMessage = error;
            if (save)
            {
                SaveUnlocked();
            }
        }
    }

    private PublishRecord GetOrCreatePublish(PublishItem item)
    {
        var key = PathHelper.Normalize(item.ContentFolder);
        if (key.Length == 0)
        {
            key = PathHelper.Normalize(item.SourcePath);
        }

        if (!_publishRecords.TryGetValue(key, out var record))
        {
            record = new PublishRecord { ContentFolder = item.ContentFolder, SourcePath = item.SourcePath };
            _publishRecords[key] = record;
        }

        return record;
    }

    private ExportRecord GetOrCreate(WallpaperSource source)
    {
        var key = PathHelper.Normalize(source.SourcePath);
        if (!_records.TryGetValue(key, out var record))
        {
            record = new ExportRecord
            {
                SourcePath = source.SourcePath,
                LastModified = source.LastModified,
                FileHash = source.FileHash
            };
            _records[key] = record;
        }

        return record;
    }

    private void SaveUnlocked()
    {
        try
        {
            var document = new StateDocument
            {
                Version = 1,
                Records = _records.Values
                    .OrderBy(record => record.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                PublishRecords = _publishRecords.Values
                    .OrderBy(record => record.ContentFolder, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            var json = JsonSerializer.Serialize(document, Options);
            AtomicFile.WriteAllText(AppPaths.StateFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save state.json.", ex);
            throw new IOException("The library memory could not be saved. Check that the app folder is writable.", ex);
        }
    }

    private static void TryArchiveCorrupt()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile))
            {
                return;
            }

            var destination = $"{AppPaths.StateFile}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(AppPaths.StateFile, destination, overwrite: true);
            Logger.Warn($"Archived corrupt state file to '{destination}'.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not archive corrupt state file.", ex);
        }
    }
}
