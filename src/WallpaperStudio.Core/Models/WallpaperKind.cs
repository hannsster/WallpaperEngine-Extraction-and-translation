namespace WallpaperStudio.Models;

public enum WallpaperKind
{
    ImageWallpaper,
    VideoWallpaper,
    SceneProject,
    WorkshopItem,
    Unknown
}

public enum SourceStatus
{
    Ready,
    NeedsReview,
    Approved,
    Rejected,
    Exported,
    Unsupported,
    Error
}

public enum ApprovalStatus
{
    NotRequired,
    Pending,
    Approved,
    Rejected
}

public enum ExportStatus
{
    NotExported,
    Exported,
    Skipped,
    Failed
}

public static class WallpaperKindLabels
{
    public static string Label(this WallpaperKind kind) => kind switch
    {
        WallpaperKind.ImageWallpaper => "Image",
        WallpaperKind.VideoWallpaper => "Video",
        WallpaperKind.SceneProject => "Scene",
        WallpaperKind.WorkshopItem => "Workshop",
        _ => "Unsupported"
    };

    public static string Glyph(this WallpaperKind kind) => kind switch
    {
        WallpaperKind.ImageWallpaper => "\uE8B9",
        WallpaperKind.VideoWallpaper => "\uE714",
        WallpaperKind.SceneProject => "\uE81E",
        WallpaperKind.WorkshopItem => "\uE8B7",
        _ => "\uE7BA"
    };
}
