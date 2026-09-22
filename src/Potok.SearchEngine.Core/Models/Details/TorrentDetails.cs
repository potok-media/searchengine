namespace Potok.SearchEngine.Core.Models.Details;

/// <summary>
///     Базовая модель торрента.
/// </summary>
public class TorrentDetails : ICloneable
{
    /// <summary>
    ///     Latest source observation emitted by a tracker adapter. Nullable only while
    ///     legacy tracker adapters are migrated to the ingestion contract.
    /// </summary>
    public TorrentSourceSnapshot? Source { get; set; }

    public string? InfoHash { get; set; }

    public string TrackerName { get; set; } = null!;

    public string[]? Types { get; set; } = null!;

    public string Url { get; set; } = null!;

    public string Title { get; set; } = null!;

    public int Sid { get; set; }

    public int Pir { get; set; }

    public string? SizeName { get; set; } = null!;

    public DateTime CreateTime { get; set; } = DateTime.UtcNow;

    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;

    public string? Magnet { get; set; } = null!;

    public string? Name { get; set; } = null!;

    public string? OriginalName { get; set; } = null!;

    public int ReleaseYear { get; set; }

    public HashSet<string>? Languages { get; set; }

    public double Size { get; set; }

    public int Quality { get; set; }

    public string? VideoType { get; set; } = null!;

    public HashSet<string>? Voices { get; set; }

    public HashSet<int>? Seasons { get; set; }

    public ParsedTorrentInfo? ParsedInfo { get; set; }

    public object Clone()
    {
        return MemberwiseClone();
    }
}
