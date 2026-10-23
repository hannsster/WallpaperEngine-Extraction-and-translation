using System.Windows.Media;

namespace WallpaperStudio.Models;

public enum PublishKind
{
    /// <summary>An existing Wallpaper Engine project folder that is uploaded as-is.</summary>
    ExistingProject,

    /// <summary>A new "video" wallpaper project built from a loose video file.</summary>
    NewProjectFromVideo,

    /// <summary>A new "scene" wallpaper project built from a static image. Experimental.</summary>
    NewProjectFromImage
}

/// <summary>Maps directly to the Steam Workshop <c>visibility</c> value.</summary>
public enum PublishVisibility
{
    Public = 0,
    FriendsOnly = 1,
    Private = 2,
    Unlisted = 3
}

public enum PublishState
{
    Draft,
    Building,
    Ready,
    Uploading,
    Published,
    Failed
}

/// <summary>
/// One wallpaper queued for publishing to the Steam Workshop through SteamCMD.
/// Nothing here stores or requests Steam credentials.
/// </summary>
public sealed class PublishItem : ObservableObject
{
    private static readonly (PublishVisibility Value, string Label)[] VisibilityMap =
    [
        (PublishVisibility.Public, "Public"),
        (PublishVisibility.FriendsOnly, "Friends only"),
        (PublishVisibility.Private, "Private (hidden)"),
        (PublishVisibility.Unlisted, "Unlisted")
    ];

    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _changeNote = string.Empty;
    private PublishVisibility _visibility = PublishVisibility.Private;
    private string _contentFolder = string.Empty;
    private string? _previewFile;
    private PublishState _state = PublishState.Draft;
    private string? _publishedFileId;
    private string? _workshopUrl;
    private string? _errorMessage;
    private string _statusDetail = string.Empty;
    private long _contentBytes;
    private bool _isExperimental;
    private string? _warningText;
    private ImageSource? _thumbnail;
    private bool _isThumbnailLoading;

    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Project folder for an existing project, or the image/video file for a new one.</summary>
    public required string SourcePath { get; init; }

    public PublishKind Kind { get; init; }

    /// <summary>Workshop id discovered in an existing project.json, used to update that item.</summary>
    public string? ExistingWorkshopId { get; init; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(CanPublish), nameof(DisplayTitle));
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string ChangeNote
    {
        get => _changeNote;
        set => SetProperty(ref _changeNote, value);
    }

    public PublishVisibility Visibility
    {
        get => _visibility;
        set
        {
            if (SetProperty(ref _visibility, value))
            {
                OnPropertyChanged(nameof(VisibilityLabel), nameof(VisibilityTone));
            }
        }
    }

    /// <summary>Two-way friendly label used by the visibility picker.</summary>
    public string VisibilityLabel
    {
        get => VisibilityMap.First(entry => entry.Value == _visibility).Label;
        set
        {
            var match = VisibilityMap.FirstOrDefault(entry =>
                string.Equals(entry.Label, value, StringComparison.OrdinalIgnoreCase));
            if (match.Label is not null)
            {
                Visibility = match.Value;
            }
        }
    }

    public static IReadOnlyList<string> VisibilityChoices { get; } = VisibilityMap.Select(entry => entry.Label).ToList();

    public string ContentFolder
    {
        get => _contentFolder;
        set
        {
            if (SetProperty(ref _contentFolder, value))
            {
                OnPropertyChanged(nameof(IsBuilt), nameof(CanPublish), nameof(ContentFolderDisplay), nameof(ContentSizeText));
            }
        }
    }

    public string? PreviewFile
    {
        get => _previewFile;
        set
        {
            if (SetProperty(ref _previewFile, value))
            {
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    public PublishState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(
                    nameof(StateLabel), nameof(StateTone), nameof(CanPublish), nameof(IsPublished), nameof(IsBusyState));
            }
        }
    }

    public string? PublishedFileId
    {
        get => _publishedFileId;
        set
        {
            if (SetProperty(ref _publishedFileId, value))
            {
                OnPropertyChanged(nameof(IsPublished), nameof(WorkshopUrl));
            }
        }
    }

    public string? WorkshopUrl
    {
        get => _workshopUrl;
        set
        {
            if (SetProperty(ref _workshopUrl, value))
            {
                OnPropertyChanged(nameof(HasWorkshopLink));
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => SetProperty(ref _statusDetail, value);
    }

    public long ContentBytes
    {
        get => _contentBytes;
        set
        {
            if (SetProperty(ref _contentBytes, value))
            {
                OnPropertyChanged(nameof(ContentSizeText));
            }
        }
    }

    public bool IsExperimental
    {
        get => _isExperimental;
        set => SetProperty(ref _isExperimental, value);
    }

    public string? WarningText
    {
        get => _warningText;
        set
        {
            if (SetProperty(ref _warningText, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
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

    public bool RequiresBuild => Kind != PublishKind.ExistingProject;

    public bool IsBuilt => !string.IsNullOrWhiteSpace(ContentFolder) && Directory.Exists(PathHelper.ToExtendedPath(ContentFolder));

    public bool CanPublish => !string.IsNullOrWhiteSpace(Title)
        && IsBuilt
        && State is PublishState.Draft or PublishState.Ready or PublishState.Published or PublishState.Failed;

    public bool IsBusyState => State is PublishState.Building or PublishState.Uploading;

    public bool IsPublished => State == PublishState.Published && !string.IsNullOrWhiteSpace(PublishedFileId);

    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewFile) && File.Exists(PathHelper.ToExtendedPath(PreviewFile!));

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasWorkshopLink => !string.IsNullOrWhiteSpace(WorkshopUrl);

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title;

    public string KindLabel => Kind switch
    {
        PublishKind.ExistingProject => "Existing project",
        PublishKind.NewProjectFromVideo => "New · video wallpaper",
        _ => "New · scene from image"
    };

    public string KindGlyph => Kind switch
    {
        PublishKind.ExistingProject => "\uE8B7",
        PublishKind.NewProjectFromVideo => "\uE714",
        _ => "\uE91B"
    };

    public string SourceDisplay => PathHelper.Ellipsize(SourcePath, 96);

    public string ContentFolderDisplay => string.IsNullOrWhiteSpace(ContentFolder)
        ? "Not built yet"
        : PathHelper.Ellipsize(ContentFolder, 96);

    public string ContentSizeText => ContentBytes > 0 ? PathHelper.HumanSize(ContentBytes) : string.Empty;

    public string StateLabel => State switch
    {
        PublishState.Draft => "Draft",
        PublishState.Building => "Building…",
        PublishState.Ready => "Ready",
        PublishState.Uploading => "Uploading…",
        PublishState.Published => "Published",
        _ => "Failed"
    };

    public string StateTone => State switch
    {
        PublishState.Draft => "muted",
        PublishState.Building => "accent",
        PublishState.Ready => "accent",
        PublishState.Uploading => "warning",
        PublishState.Published => "success",
        _ => "danger"
    };

    public string VisibilityTone => Visibility switch
    {
        PublishVisibility.Public => "warning",
        PublishVisibility.Private => "success",
        _ => "muted"
    };

    public void MarkAvailableForPublish()
        => OnPropertyChanged(nameof(IsBuilt), nameof(CanPublish), nameof(ContentFolderDisplay), nameof(ContentSizeText));
}
