namespace WallpaperStudio.Models;

/// <summary>Persistent memory of one Workshop publish attempt, keyed by content folder.</summary>
public sealed class PublishRecord
{
    public string ContentFolder { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public PublishVisibility Visibility { get; set; } = PublishVisibility.Private;
    public string? PublishedFileId { get; set; }
    public string? WorkshopUrl { get; set; }
    public DateTimeOffset? PublishedDate { get; set; }
    public string? ErrorMessage { get; set; }
}
