namespace WallpaperStudio.Models;

public sealed class ExportRecord
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DateTimeOffset LastModified { get; set; }
    public DateTimeOffset? ExportedDate { get; set; }
    public string? ExportPath { get; set; }
    public string Resolution { get; set; } = "Unknown";
    public ExportStatus ExportStatus { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public double FrameTimestampSeconds { get; set; }
    public bool UsesPreviewFallback { get; set; }
    public string Title { get; set; } = string.Empty;

    public bool Matches(string fileHash, DateTimeOffset lastModified)
        => !string.IsNullOrWhiteSpace(fileHash)
           && string.Equals(FileHash, fileHash, StringComparison.OrdinalIgnoreCase)
           && LastModified == lastModified;

    public bool IsExportedAndCurrent(string fileHash, DateTimeOffset lastModified)
        => ExportStatus == ExportStatus.Exported
           && !string.IsNullOrWhiteSpace(ExportPath)
           && Matches(fileHash, lastModified);
}

public sealed class StateDocument
{
    public int Version { get; set; } = 1;
    public List<ExportRecord> Records { get; set; } = [];
}
