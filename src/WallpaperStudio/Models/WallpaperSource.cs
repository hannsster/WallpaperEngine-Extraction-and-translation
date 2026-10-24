using System.Windows.Media;
using WallpaperStudio.Infrastructure;

namespace WallpaperStudio.Models;

/// <summary>
/// One detected wallpaper source plus its in-memory review/export state.
/// Persistent fields are mirrored into <see cref="ExportRecord"/>.
/// </summary>
public sealed class WallpaperSource : ObservableObject
{
    private string _title = string.Empty;
    private string? _previewPath;
    private ResolutionInfo _resolution = ResolutionInfo.Unknown;
    private long _fileSize;
    private DateTimeOffset _lastModified;
    private string _fileHash = string.Empty;
    private double _durationSeconds;
    private SourceStatus _status = SourceStatus.Ready;
    private ApprovalStatus _approvalStatus = ApprovalStatus.NotRequired;
    private ExportStatus _exportStatus = ExportStatus.NotExported;
    private string? _exportPath;
    private DateTimeOffset? _exportedDate;
    private string? _errorMessage;
    private string? _candidateFramePath;
    private double _candidateFrameTime;
    private bool _usesPreviewFallback;
    private bool _isThumbnailLoading;
    private ImageSource? _thumbnail;
    private bool _isPreviewLoading;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>File for loose wallpapers, project folder for a project.json source.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The file that determines identity/hash (video, image or scene package).</summary>
    public required string PrimaryFilePath { get; init; }

    public string? ProjectFilePath { get; init; }

    public WallpaperKind Kind { get; init; }

    public bool IsWorkshop { get; init; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? PreviewPath
    {
        get => _previewPath;
        set => SetProperty(ref _previewPath, value);
    }

    public ResolutionInfo Resolution
    {
        get => _resolution;
        set
        {
            if (SetProperty(ref _resolution, value))
            {
                OnPropertyChanged(nameof(ResolutionText), nameof(MetadataSummary), nameof(ResolutionDetail));
            }
        }
    }

    public long FileSize
    {
        get => _fileSize;
        set
        {
            if (SetProperty(ref _fileSize, value))
            {
                OnPropertyChanged(nameof(FileSizeText));
            }
        }
    }

    public DateTimeOffset LastModified
    {
        get => _lastModified;
        set
        {
            if (SetProperty(ref _lastModified, value))
            {
                OnPropertyChanged(nameof(LastModifiedText));
            }
        }
    }

    public string FileHash
    {
        get => _fileHash;
        set => SetProperty(ref _fileHash, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, value))
            {
                OnPropertyChanged(nameof(DurationText), nameof(HasDuration));
            }
        }
    }

    public SourceStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                NotifyStatusComputed();
            }
        }
    }

    public ApprovalStatus ApprovalStatus
    {
        get => _approvalStatus;
        set
        {
            if (SetProperty(ref _approvalStatus, value))
            {
                NotifyStatusComputed();
            }
        }
    }

    public ExportStatus ExportStatus
    {
        get => _exportStatus;
        set
        {
            if (SetProperty(ref _exportStatus, value))
            {
                NotifyStatusComputed();
            }
        }
    }

    public string? ExportPath
    {
        get => _exportPath;
        set
        {
            if (SetProperty(ref _exportPath, value))
            {
                OnPropertyChanged(nameof(ExportPathDisplay));
            }
        }
    }

    public DateTimeOffset? ExportedDate
    {
        get => _exportedDate;
        set
        {
            if (SetProperty(ref _exportedDate, value))
            {
                OnPropertyChanged(nameof(ExportedDateText));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>Path to the frame that would be exported: extracted video frame or scene preview.</summary>
    public string? CandidateFramePath
    {
        get => _candidateFramePath;
        set
        {
            if (SetProperty(ref _candidateFramePath, value))
            {
                OnPropertyChanged(nameof(HasCandidate), nameof(CandidateSourceLabel));
            }
        }
    }

    public double CandidateFrameTime
    {
        get => _candidateFrameTime;
        set
        {
            if (SetProperty(ref _candidateFrameTime, value))
            {
                OnPropertyChanged(nameof(CandidateTimeText));
            }
        }
    }

    public bool UsesPreviewFallback
    {
        get => _usesPreviewFallback;
        set
        {
            if (SetProperty(ref _usesPreviewFallback, value))
            {
                OnPropertyChanged(nameof(PreviewFallbackNotice), nameof(HasPreviewFallbackNotice));
            }
        }
    }

    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        set => SetProperty(ref _isThumbnailLoading, value);
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        set => SetProperty(ref _isPreviewLoading, value);
    }

    public string SourceTypeLabel => IsWorkshop ? $"Workshop · {Kind.Label()}" : Kind.Label();

    public string KindLabel => Kind.Label();

    public string KindGlyph => Kind.Glyph();

    public string MetadataSummary => $"{SourceTypeLabel} · {Resolution.Display}";

    public string ResolutionText => Resolution.Display;

    public string ResolutionDetail => Resolution.IsKnown
        ? $"{Resolution.Display} · {Resolution.Orientation} · {(Resolution.PassesMinimum1080p ? "meets 1080p" : "below 1080p / tablet minimum")}"
        : "Resolution not detected";

    public string FileSizeText => FileSize > 0 ? PathHelper.HumanSize(FileSize) : string.Empty;

    public string LastModifiedText => LastModified == default ? string.Empty : LastModified.LocalDateTime.ToString("g");

    public string ExportedDateText => ExportedDate?.LocalDateTime.ToString("g") ?? string.Empty;

    public string DurationText => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : string.Empty;

    public bool HasDuration => DurationSeconds > 0;

    public string CandidateTimeText => DurationSeconds > 0
        ? TimeSpan.FromSeconds(CandidateFrameTime).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : string.Empty;

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasCandidate => !string.IsNullOrWhiteSpace(CandidateFramePath) && File.Exists(CandidateFramePath);

    public bool HasPreviewFallbackNotice => UsesPreviewFallback;

    public string PreviewFallbackNotice => Kind == WallpaperKind.SceneProject
        ? "The full-resolution scene image could not be extracted, so this low-resolution project preview is used instead. Review it before exporting."
        : "No safe renderer is available for this source. A preview/fallback image is used instead. Review it before exporting.";

    public string CandidateSourceLabel => UsesPreviewFallback
        ? "Preview / fallback"
        : Kind switch
        {
            WallpaperKind.VideoWallpaper => "Extracted video frame",
            WallpaperKind.SceneProject => "Extracted scene image",
            _ => "Source image"
        };

    public string ExportPathDisplay => string.IsNullOrWhiteSpace(ExportPath) ? string.Empty : PathHelper.Ellipsize(ExportPath, 100);

    public bool RequiresApproval => Kind is WallpaperKind.VideoWallpaper or WallpaperKind.SceneProject or WallpaperKind.WorkshopItem or WallpaperKind.Unknown;

    public bool IsExported => ExportStatus == ExportStatus.Exported && Status == SourceStatus.Exported;

    public bool NeedsReview => RequiresApproval && ApprovalStatus != ApprovalStatus.Approved && Status is SourceStatus.NeedsReview or SourceStatus.Ready;

    public bool CanExportNow => Status is SourceStatus.Ready or SourceStatus.Approved or SourceStatus.Exported
                                && (!RequiresApproval || ApprovalStatus == ApprovalStatus.Approved || Status == SourceStatus.Exported);

    public string StatusLabel => Status switch
    {
        SourceStatus.Exported => "Exported",
        SourceStatus.Approved => "Approved",
        SourceStatus.NeedsReview => "Needs review",
        SourceStatus.Rejected => "Tossed",
        SourceStatus.Unsupported => "Unsupported",
        SourceStatus.Error => "Error",
        _ => "Ready"
    };

    public string StatusTone => Status switch
    {
        SourceStatus.Exported => "success",
        SourceStatus.Approved => "accent",
        SourceStatus.NeedsReview => "warning",
        SourceStatus.Rejected => "danger",
        SourceStatus.Unsupported => "muted",
        SourceStatus.Error => "danger",
        _ => "neutral"
    };

    public void SetResolution(int width, int height)
    {
        Resolution = new ResolutionInfo(width, height);
    }

    public void ResetForRescan()
    {
        CandidateFramePath = null;
        Thumbnail = null;
        ErrorMessage = null;
        ExportStatus = ExportStatus.NotExported;
    }

    private void NotifyStatusComputed()
    {
        OnPropertyChanged(
            nameof(StatusLabel),
            nameof(StatusTone),
            nameof(IsExported),
            nameof(NeedsReview),
            nameof(CanExportNow));
    }
}
