namespace Potok.SearchEngine.Core.Models;

public record TorrentSearchRequest(
    string Query,
    string MediaType,
    string? EnglishTitle = null,
    IEnumerable<string>? Genres = null,
    long? Id = null,
    string? OriginalTitle = null,
    string? Title = null,
    string? Year = null,
    bool? ForceSearch = null,
    Guid? WorkId = null
);

public record TorrentTag(string Kind, string Value);

public record TorrentSearchResult(
    string Id,
    string Title,
    string? Tracker = null,
    long? SizeBytes = null,
    string? SizeLabel = null,
    int? Seeders = null,
    int? Leechers = null,
    string? PublishDate = null,
    string? MagnetUri = null,
    string? Link = null,
    IEnumerable<TorrentTag>? Tags = null,
    bool? Viewed = null,
    TorrentOverrideSummary? Override = null
);

// Compact search-result badge. Omit (null) when both maps are empty. primary* only if season_map has exactly one key.
public record TorrentOverrideSummary(
    int SeasonCount,
    int FileCount,
    string? PrimarySource = null,
    int? PrimarySeason = null,
    int? PrimaryOffset = null)
{
    public static TorrentOverrideSummary? From(
        IReadOnlyDictionary<string, SeasonOverrideEntry>? seasonMap,
        IReadOnlyDictionary<string, FileOverrideEntry>? fileMap)
    {
        var seasonCount = seasonMap?.Count ?? 0;
        var fileCount = fileMap?.Count ?? 0;
        if (seasonCount == 0 && fileCount == 0)
            return null;

        if (seasonMap is { Count: 1 })
        {
            var (source, entry) = seasonMap.First();
            return new TorrentOverrideSummary(seasonCount, fileCount, source, entry.Season, entry.Offset);
        }

        return new TorrentOverrideSummary(seasonCount, fileCount);
    }
}

public record TorrentSearchResponse(IEnumerable<TorrentSearchResult> Results);

/// <summary>
///     One NDJSON line from POST /api/v1/torrents/search/stream.
///     <c>type</c> is <c>batch</c> (with results) or <c>done</c>.
/// </summary>
public record TorrentSearchStreamEvent(
    string Type,
    string? Source = null,
    IEnumerable<TorrentSearchResult>? Results = null);

// Per-season override model (stored in SearchEngine, keyed by torrent infohash). season_map keys are source-season
// numbers as strings; the sentinel "_" buckets files with no parseable season. Each entry remaps that source
// season to a TMDB (Season, Offset): displayedEpisode = parsedEpisode + Offset.
public record SeasonOverrideEntry(int Season, int Offset);

// Overrides stay in the existing file_map JSONB. Canonical targets are validated by ARM
// against its active layout at release resolution; SearchEngine owns persistence only.
// Scoped anchors map files in manifest order within one ARM entry, and pins do not
// consume that run. Nullable numeric coordinates retain older persisted overrides.
// The v2 graph dropped the ordering indirection and renamed groupId to entryId; the
// DropArmOverrideTargets migration cleared pre-v2 bindings whose episode ids are dead.
public sealed record ArmEpisodeOverrideTarget(Guid WorkId, Guid EntryId, Guid EpisodeId);

public record FileOverrideEntry(
    int? Season,
    decimal? Episode,
    string Mode,
    ArmEpisodeOverrideTarget? ArmTarget = null,
    IReadOnlyList<string>? ScopeFileIds = null);

public record TorrentOverrideMap(
    string Hash,
    Dictionary<string, SeasonOverrideEntry> SeasonMap,
    Dictionary<string, FileOverrideEntry>? FileMap = null);

public record UpsertSeasonOverrideRequest(int? SourceSeason, int TargetSeason, int Offset);

public record UpsertFileOverrideRequest(
    string FileId,
    int? Season = null,
    decimal? Episode = null,
    string Mode = "anchor",
    ArmEpisodeOverrideTarget? ArmTarget = null,
    IReadOnlyList<string>? ScopeFileIds = null);

// Continue-watching cursor for a title on this SearchEngine instance. One row per (mediaType, tmdbId).
// `Stream` is the opaque torrent payload the plugin needs to POST /api/torrents again.
public record TorrentContinueCursor(
    string MediaType,
    long TmdbId,
    string Title,
    string FileIndex,
    int ProgressSeconds,
    int DurationSeconds,
    string? PosterSrc = null,
    string? BackdropSrc = null,
    string? StillSrc = null,
    int? Season = null,
    int? Episode = null,
    string? AudioName = null,
    string? InfoHash = null,
    object? Stream = null,
    DateTimeOffset? UpdatedAt = null);

public record TorrentContinueListResponse(IEnumerable<TorrentContinueCursor> Items);

public record TorrentContinueRemoveRequest(string MediaType, long TmdbId);
