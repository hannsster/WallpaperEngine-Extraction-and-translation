namespace WallpaperStudio.Models;

public sealed class AppSettings
{
    public string SourceFolder { get; set; } = string.Empty;
    public string ExportFolder { get; set; } = string.Empty;
    public int JpegQuality { get; set; } = 90;
    public bool EnforceMinimumResolution { get; set; } = true;
    public double DefaultFrameTimestampSeconds { get; set; } = 1.0;
    public string FfmpegPath { get; set; } = string.Empty;
    public string WallpaperEnginePath { get; set; } = string.Empty;
    public string SteamCmdPath { get; set; } = string.Empty;
    public string SteamUsername { get; set; } = string.Empty;
    public bool SkipAlreadyExported { get; set; } = true;
    public bool OpenExportFolderAfterBatch { get; set; }
    public bool AutoApproveScenes { get; set; } = true;

    public void Normalize()
    {
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        DefaultFrameTimestampSeconds = Math.Clamp(DefaultFrameTimestampSeconds, 0, 86_400);
        SourceFolder ??= string.Empty;
        ExportFolder ??= string.Empty;
        FfmpegPath ??= string.Empty;
        WallpaperEnginePath ??= string.Empty;
        SteamCmdPath ??= string.Empty;
        SteamUsername = (SteamUsername ?? string.Empty).Trim();
    }
}
