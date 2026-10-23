using System.Windows.Media;
using WallpaperStudio.Infrastructure;

namespace WallpaperStudio.Models;

public enum ImportDecision
{
    Pending,
    Approved,
    ApprovedLowResolution,
    Tossed,
    Staged,
    Failed
}

public sealed class ImportItem : ObservableObject
{
    private ImportDecision _decision = ImportDecision.Pending;
    private string? _message;
    private ImageSource? _thumbnail;
    private bool _isThumbnailLoading;

    public required string FilePath { get; init; }
    public string FileName { get; init; } = string.Empty;
    public ResolutionInfo Resolution { get; init; }
    public long FileSize { get; init; }
    public bool PassesRule { get; init; }
    public string FileHash { get; init; } = string.Empty;

    public ImportDecision Decision
    {
        get => _decision;
        set
        {
            if (SetProperty(ref _decision, value))
            {
                OnPropertyChanged(nameof(StatusLabel), nameof(StatusTone), nameof(CanStage), nameof(IsRejected), nameof(IsStaged));
            }
        }
    }

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
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

    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        set => SetProperty(ref _isThumbnailLoading, value);
    }

    public bool HasThumbnail => Thumbnail is not null;
    public bool IsLowResolution => !PassesRule;
    public bool IsRejected => Decision == ImportDecision.Tossed;
    public bool CanStage => Decision is ImportDecision.Approved or ImportDecision.ApprovedLowResolution;
    public bool IsStaged => Decision == ImportDecision.Staged;
    public string ResolutionText => Resolution.Display;
    public string FileSizeText => PathHelper.HumanSize(FileSize);
    public string RuleText => ResolutionRules.Explain(Resolution.Width, Resolution.Height);

    public string StatusLabel => Decision switch
    {
        ImportDecision.ApprovedLowResolution => "Low-res approved",
        ImportDecision.Approved => "Approved",
        ImportDecision.Tossed => "Tossed",
        ImportDecision.Staged => "Staged",
        ImportDecision.Failed => "Failed",
        _ => IsLowResolution ? "Needs decision" : "Ready"
    };

    public string StatusTone => Decision switch
    {
        ImportDecision.ApprovedLowResolution => "warning",
        ImportDecision.Approved => "accent",
        ImportDecision.Tossed => "danger",
        ImportDecision.Staged => "success",
        ImportDecision.Failed => "danger",
        _ => IsLowResolution ? "warning" : "neutral"
    };
}
