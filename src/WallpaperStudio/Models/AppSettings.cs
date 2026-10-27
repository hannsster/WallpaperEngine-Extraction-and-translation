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

    // ---------------------------------------------------------------- reverse search
    /// <summary>Master switch. When false no image ever leaves this machine.</summary>
    public bool ReverseSearchEnabled { get; set; }

    /// <summary>One-time acknowledgement that frames are uploaded to third-party services.</summary>
    public bool ReverseSearchConsent { get; set; }

    /// <summary>Search automatically once a usable frame has been extracted.</summary>
    public bool ReverseSearchAutomatic { get; set; } = true;

    /// <summary>trace.moe is keyless; the others need a free API key.</summary>
    public bool ReverseSearchTraceMoe { get; set; } = true;

    public string SauceNaoApiKey { get; set; } = string.Empty;
    public string GoogleVisionApiKey { get; set; } = string.Empty;

    /// <summary>Automatic searches allowed per calendar day. Manual searches are never blocked.</summary>
    public int ReverseSearchDailyLimit { get; set; } = 40;

    /// <summary>0..1 confidence floor applied to every provider.</summary>
    public double ReverseSearchMinSimilarity { get; set; } = 0.80;

    /// <summary>Look up exact dimensions and direct file URLs on public booru APIs.</summary>
    public bool ReverseSearchEnrichBooru { get; set; } = true;

    public void Normalize()
    {
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        DefaultFrameTimestampSeconds = Math.Clamp(DefaultFrameTimestampSeconds, 0, 86_400);
        ReverseSearchDailyLimit = Math.Clamp(ReverseSearchDailyLimit, 1, 500);
        ReverseSearchMinSimilarity = Math.Clamp(ReverseSearchMinSimilarity, 0.50, 0.99);
        SauceNaoApiKey = (SauceNaoApiKey ?? string.Empty).Trim();
        GoogleVisionApiKey = (GoogleVisionApiKey ?? string.Empty).Trim();
        SourceFolder ??= string.Empty;
        ExportFolder ??= string.Empty;
        FfmpegPath ??= string.Empty;
        WallpaperEnginePath ??= string.Empty;
        SteamCmdPath ??= string.Empty;
        SteamUsername = (SteamUsername ?? string.Empty).Trim();
    }
}
