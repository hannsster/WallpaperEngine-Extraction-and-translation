using System.Collections.ObjectModel;
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
    private SceneComposition _sceneComposition;
    private bool _usesPreviewFallback;
    private bool _isThumbnailLoading;
    private byte[]? _thumbnail;
    private bool _isPreviewLoading;
    private ReverseSearchStatus _reverseStatus = ReverseSearchStatus.Idle;
    private string? _reverseNote;
    private string? _reverseQueryPath;
    private DateTimeOffset? _reverseSearchedAt;
    private bool _isReverseSearching;
    private string? _candidateNote;

    public WallpaperSource()
    {
        ReverseResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(
                nameof(HasReverseResults),
                nameof(HasBetterOriginal),
                nameof(HasReverseMatches),
                nameof(BestOriginal),
                nameof(OriginalChipText),
                nameof(ReverseResultsHeader),
                nameof(ReverseResultsTone),
                nameof(ShowReverseStatus),
                nameof(ShowReverseStatusChip));
        };
    }

    /// <summary>Original-artwork matches found by reverse image search.</summary>
    public ObservableCollection<ReverseSearchResult> ReverseResults { get; } = [];

    /// <summary>A short note about the chosen candidate, e.g. why a different frame was picked.</summary>
    public string? CandidateNote
    {
        get => _candidateNote;
        set
        {
            if (SetProperty(ref _candidateNote, value))
            {
                OnPropertyChanged(nameof(HasCandidateNote));
            }
        }
    }

    public bool HasCandidateNote => !string.IsNullOrWhiteSpace(CandidateNote);

    public ReverseSearchStatus ReverseStatus
    {
        get => _reverseStatus;
        set
        {
            if (SetProperty(ref _reverseStatus, value))
            {
                OnPropertyChanged(nameof(ReverseStatusLabel), nameof(ReverseStatusTone), nameof(ReverseResultsHeader), nameof(ReverseResultsTone), nameof(ShowReverseStatus), nameof(ShowReverseStatusChip));
            }
        }
    }

    public string? ReverseNote
    {
        get => _reverseNote;
        set
        {
            if (SetProperty(ref _reverseNote, value))
            {
                OnPropertyChanged(nameof(HasReverseNote));
            }
        }
    }

    public bool HasReverseNote => !string.IsNullOrWhiteSpace(ReverseNote);

    /// <summary>Downscaled JPEG that was actually sent to the providers, kept for transparency.</summary>
    public string? ReverseQueryPath
    {
        get => _reverseQueryPath;
        set => SetProperty(ref _reverseQueryPath, value);
    }

    public DateTimeOffset? ReverseSearchedAt
    {
        get => _reverseSearchedAt;
        set => SetProperty(ref _reverseSearchedAt, value);
    }

    public bool IsReverseSearching
    {
        get => _isReverseSearching;
        set
        {
            if (SetProperty(ref _isReverseSearching, value))
            {
                OnPropertyChanged(nameof(ReverseResultsHeader), nameof(ReverseResultsTone), nameof(ShowReverseStatus), nameof(ShowReverseStatusChip));
            }
        }
    }

    public bool HasReverseResults => ReverseResults.Count > 0;

    /// <summary>Compact card chip for searches that finished without a usable match.</summary>
    public bool ShowReverseStatusChip => !IsReverseSearching &&
                                        !HasReverseResults &&
                                        ReverseStatus is ReverseSearchStatus.NoMatch
                                            or ReverseSearchStatus.NeedsBetterFrame
                                            or ReverseSearchStatus.Error;

    /// <summary>Hides the empty "no original yet" line until something meaningful happened.</summary>
    public bool ShowReverseStatus => IsReverseSearching ||
                                     ReverseResults.Count > 0 ||
                                     ReverseStatus != ReverseSearchStatus.Idle;

    /// <summary>True when a source was identified but it is not a larger image.</summary>
    public bool HasReverseMatches => HasReverseResults && !HasBetterOriginal;

    public bool HasBetterOriginal => ReverseResults.Any(result => result.IsBetterThanCurrent);

    public ReverseSearchResult? BestOriginal => ReverseResults.FirstOrDefault(result => result.IsBetterThanCurrent)
                                                ?? ReverseResults.FirstOrDefault();

    /// <summary>Short badge text for the library card.</summary>
    public string OriginalChipText => HasBetterOriginal && BestOriginal is not null
        ? "Original " + BestOriginal.ResolutionText
        : "Source found";

    public string ReverseResultsHeader => IsReverseSearching
        ? "Searching for the original"
        : ReverseResults.Count > 0
            ? $"{ReverseResults.Count} match{(ReverseResults.Count == 1 ? string.Empty : "es")} found"
            : ReverseStatus switch
            {
                ReverseSearchStatus.NoMatch => "No match found",
                ReverseSearchStatus.NeedsBetterFrame => "Frame too blank to match",
                ReverseSearchStatus.NotConfigured => "Reverse search not configured",
                ReverseSearchStatus.Skipped => "Reverse search paused",
                ReverseSearchStatus.Error => "Reverse search failed",
                _ => "No original found yet"
            };

    public string ReverseResultsTone => IsReverseSearching
        ? "accent"
        : ReverseResults.Count > 0
            ? (HasBetterOriginal ? "success" : "neutral")
            : ReverseStatus switch
            {
                ReverseSearchStatus.NoMatch => "warning",
                ReverseSearchStatus.NeedsBetterFrame => "warning",
                ReverseSearchStatus.Error => "danger",
                ReverseSearchStatus.NotConfigured => "muted",
                ReverseSearchStatus.Skipped => "muted",
                _ => "neutral"
            };

    public string ReverseStatusLabel => ReverseStatus switch
    {
        ReverseSearchStatus.Searching => "Searching",
        ReverseSearchStatus.Results => "Matches found",
        ReverseSearchStatus.NoMatch => "No match",
        ReverseSearchStatus.NotConfigured => "Not configured",
        ReverseSearchStatus.NeedsBetterFrame => "Needs a better frame",
        ReverseSearchStatus.Skipped => "Paused",
        ReverseSearchStatus.Error => "Error",
        _ => "Not searched"
    };

    public string ReverseStatusTone => ReverseStatus switch
    {
        ReverseSearchStatus.Results => "success",
        ReverseSearchStatus.NoMatch => "warning",
        ReverseSearchStatus.NeedsBetterFrame => "warning",
        ReverseSearchStatus.Error => "danger",
        ReverseSearchStatus.NotConfigured => "muted",
        ReverseSearchStatus.Skipped => "muted",
        _ => "neutral"
    };

    public string ReverseSearchedText => ReverseSearchedAt is null
        ? string.Empty
        : "last search " + ReverseSearchedAt.Value.LocalDateTime.ToString("g");

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

    /// <summary>Which layers are baked into the static frame for a scene project.</summary>
    public SceneComposition SceneComposition
    {
        get => _sceneComposition;
        set
        {
            if (SetProperty(ref _sceneComposition, value))
            {
                OnPropertyChanged(nameof(SceneCompositionLabel));
            }
        }
    }

    public string SceneCompositionLabel => SceneComposition switch
    {
        WallpaperStudio.Models.SceneComposition.Full => "Everything",
        WallpaperStudio.Models.SceneComposition.Background => "Background only",
        _ => "Balanced"
    };

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

    public byte[]? Thumbnail
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

    public bool HasThumbnail => Thumbnail is { Length: > 0 };

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

    /// <summary>Replaces the match list from a completed (or cached) search.</summary>
    public void ApplyReverseSearch(ReverseSearchSnapshot? snapshot)
    {
        ReverseResults.Clear();
        if (snapshot is null)
        {
            ReverseStatus = ReverseSearchStatus.Idle;
            ReverseNote = null;
            return;
        }

        foreach (var result in snapshot.Results)
        {
            ReverseResults.Add(result);
        }

        ReverseStatus = snapshot.Status;
        ReverseNote = snapshot.Note;
        ReverseQueryPath = snapshot.QueryImagePath;
        ReverseSearchedAt = snapshot.SearchedAt;
    }

    public void ResetReverseSearch()
    {
        ReverseResults.Clear();
        ReverseStatus = ReverseSearchStatus.Idle;
        ReverseNote = null;
        ReverseQueryPath = null;
        ReverseSearchedAt = null;
        IsReverseSearching = false;
    }

    public void ResetForRescan()
    {
        CandidateFramePath = null;
        Thumbnail = null;
        ErrorMessage = null;
        ExportStatus = ExportStatus.NotExported;
        CandidateNote = null;
        ResetReverseSearch();
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
