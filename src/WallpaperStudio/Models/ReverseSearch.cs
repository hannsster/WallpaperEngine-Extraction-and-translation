using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace WallpaperStudio.Models;

/// <summary>Where a reverse-image search stands for one wallpaper source.</summary>
public enum ReverseSearchStatus
{
    /// <summary>No search has run yet.</summary>
    Idle,

    /// <summary>A search is in flight.</summary>
    Searching,

    /// <summary>The providers returned at least one usable match.</summary>
    Results,

    /// <summary>The providers answered, but nothing passed the confidence floor.</summary>
    NoMatch,

    /// <summary>No provider is usable (no key configured, or trace.moe disabled).</summary>
    NotConfigured,

    /// <summary>The frame is blank/dark, so searching it would be pointless.</summary>
    NeedsBetterFrame,

    /// <summary>Turned off, or the automatic daily budget was reached.</summary>
    Skipped,

    /// <summary>A provider failed; see <see cref="ReverseSearchSnapshot.Notes"/>.</summary>
    Error
}

/// <summary>
/// One candidate original returned by a provider. Immutable apart from the
/// decoded thumbnail, so it can be cached as JSON and reloaded cheaply.
/// </summary>
public sealed class ReverseSearchResult
{
    public string ProviderId { get; init; } = string.Empty;

    public string ProviderLabel { get; init; } = string.Empty;

    /// <summary>Human name of the site the match lives on, e.g. Pixiv or Danbooru.</summary>
    public string SourceName { get; init; } = string.Empty;

    public string? Title { get; set; }

    public string? Artist { get; set; }

    /// <summary>Page a person should open to see the artwork in context.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Direct image URL. Present only when the provider or an enrichment call supplied one.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Provider-hosted preview used for the card while the search runs. Never the full image.</summary>
    public string? ThumbnailUrl { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Match confidence normalised to 0..1.</summary>
    public double Similarity { get; set; }

    /// <summary>Ranking score computed from confidence, resolution and provider reliability.</summary>
    public double Score { get; set; }

    /// <summary>Extra identification such as "Episode 12 at 02:03", for scene matches.</summary>
    public string? Identification { get; init; }

    /// <summary>True for matches that only identify the source (anime episode) and cannot be downloaded.</summary>
    public bool IsIdentificationOnly { get; init; }

    /// <summary>True when this match is meaningfully larger than the frame currently queued for export.</summary>
    public bool IsBetterThanCurrent { get; set; }

    /// <summary>Booru post id used to fetch exact dimensions and a direct file URL.</summary>
    public int? BooruPostId { get; init; }

    /// <summary>"danbooru", "yandere" or "konachan" when <see cref="BooruPostId"/> is set.</summary>
    public string? BooruSite { get; init; }

    public int? PixivId { get; init; }

    /// <summary>Cached provider preview on disk. Populated after the search, not serialised.</summary>
    [JsonIgnore]
    public string? ThumbnailPath { get; set; }

    [JsonIgnore]
    public ImageSource? Thumbnail { get; set; }

    public bool HasPage => !string.IsNullOrWhiteSpace(PageUrl);

    public bool CanUseImage => !IsIdentificationOnly && !string.IsNullOrWhiteSpace(ImageUrl);

    public string Header => string.IsNullOrWhiteSpace(Title)
        ? SourceName
        : Title!;

    public string Subheader
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Artist))
            {
                parts.Add(Artist!);
            }

            if (!string.IsNullOrWhiteSpace(Identification))
            {
                parts.Add(Identification!);
            }

            if (parts.Count == 0)
            {
                parts.Add(ProviderLabel);
            }

            return string.Join(" · ", parts);
        }
    }

    public string ResolutionText => Width > 0 && Height > 0
        ? $"{Width} x {Height}"
        : "Size unknown";

    public string SimilarityText => $"{Math.Round(Similarity * 100):0}% match";

    public string SizeBadge => IsIdentificationOnly
        ? "IDENTIFICATION"
        : IsBetterThanCurrent
            ? "HIGHER RES"
            : Width > 0 && Height > 0
                ? "SIZES"
                : "UNKNOWN SIZE";

    public string SizeTone => IsIdentificationOnly
        ? "muted"
        : IsBetterThanCurrent
            ? "success"
            : "neutral";

    public string ProviderChip => SourceName;

    public string Tooltip => $"{Header}\n{Subheader}\n{ResolutionText}\n{ProviderLabel}";
}

/// <summary>Everything one search produced, cached on disk per query image.</summary>
public sealed class ReverseSearchSnapshot
{
    public string Key { get; set; } = string.Empty;

    public string QueryImagePath { get; set; } = string.Empty;

    public int QueryWidth { get; set; }

    public int QueryHeight { get; set; }

    public DateTimeOffset SearchedAt { get; set; }

    public ReverseSearchStatus Status { get; set; } = ReverseSearchStatus.Idle;

    public List<string> ProvidersQueried { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<ReverseSearchResult> Results { get; set; } = [];

    public bool FromCache { get; set; }

    public string? Note => Notes.Count == 0 ? null : string.Join(" ", Notes);

    public bool HasBetterOriginal => Results.Any(result => result.IsBetterThanCurrent);
}

/// <summary>Small on-disk record of how many automatic searches ran today.</summary>
public sealed class ReverseSearchUsage
{
    public string Date { get; set; } = string.Empty;

    public int AutomaticSearches { get; set; }
}

/// <summary>A frame offered by the filmstrip picker.</summary>
public sealed class FrameSample : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _isSelected;

    public required int Index { get; init; }

    public required string Path { get; init; }

    public required double TimeSeconds { get; init; }

    public required double Score { get; init; }

    public bool IsAllBlack { get; init; }

    public bool IsWeak { get; init; }

    public string TimeText => TimeSpan.FromSeconds(Math.Max(0, TimeSeconds)).ToString(@"m\:ss");

    public string ScoreText => IsAllBlack ? "black" : $"{Score:0}";

    public string Tone => IsAllBlack ? "danger" : IsWeak ? "warning" : Score >= 45 ? "success" : "neutral";

    public string Label => $"{TimeText} · {ScoreText}";

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

    public bool HasThumbnail => _thumbnail is not null;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectionTone), nameof(SelectionWeight));
            }
        }
    }

    public string SelectionTone => _isSelected ? "accent" : "neutral";

    public string SelectionWeight => _isSelected ? "SemiBold" : "Normal";
}
