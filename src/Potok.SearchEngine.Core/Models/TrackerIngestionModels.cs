using System.Text.Json;
using Potok.SearchEngine.Core.Enums;

namespace Potok.SearchEngine.Core.Models;

[Flags]
public enum TorrentObservedFields : ulong
{
    None = 0,
    InfoHash = 1UL << 0,
    Magnet = 1UL << 1,
    Title = 1UL << 2,
    Size = 1UL << 3,
    Seeders = 1UL << 4,
    Leechers = 1UL << 5,
    PublishDate = 1UL << 6,
    Names = 1UL << 7,
    ReleaseYear = 1UL << 8,
    Types = 1UL << 9,
    Quality = 1UL << 10,
    VideoType = 1UL << 11,
    Languages = 1UL << 12,
    Voices = 1UL << 13,
    Seasons = 1UL << 14,
    SourceUpdatedAt = 1UL << 15
}

public enum TrackerDetailsState
{
    NotRequired,
    Fetched,
    Partial,
    Failed,
    BudgetExhausted
}

public sealed record TorrentSourceSnapshot(
    TrackerType Tracker,
    string SourceKey,
    string SourceUrl,
    TorrentObservedFields ObservedFields,
    TrackerDetailsState DetailsState,
    int PayloadSchemaVersion,
    string ParserVersion,
    JsonElement SourcePayload,
    DateTimeOffset FetchedAt,
    DateTimeOffset? DetailsFetchedAt,
    DateTimeOffset? SourceUpdatedAt);

public enum TrackerSearchErrorCode
{
    Authentication,
    Challenge,
    Transport,
    InvalidResponse,
    ParserContract
}

public sealed class TrackerSearchException : Exception
{
    public TrackerSearchException(
        TrackerType tracker,
        TrackerSearchErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Tracker = tracker;
        Code = code;
    }

    public TrackerType Tracker { get; }
    public TrackerSearchErrorCode Code { get; }
}

public sealed record TrackerIngestionRequest(
    string Query,
    long? TmdbId,
    IReadOnlyCollection<TrackerType>? Trackers,
    TrackerIngestionReason Reason);

public enum TrackerIngestionReason
{
    Interactive,
    Refresh,
    Popular
}

public sealed record TrackerIngestionBatch(
    TrackerType Tracker,
    TrackerIngestionStatus Status,
    IReadOnlyCollection<TorrentDetails> Results,
    TorrentPersistResult Persistence,
    string? ErrorCode);

public enum TrackerIngestionStatus
{
    Succeeded,
    Empty,
    Partial,
    TimedOut,
    Failed,
    Disabled
}

public sealed record TorrentPersistRequest(
    string Query,
    long? TmdbId,
    TrackerType Tracker,
    IReadOnlyCollection<TorrentDetails> Observations,
    TrackerIngestionReason Reason);

public sealed record TorrentPersistResult(
    int ObservationsWritten,
    int CanonicalTorrentsWritten,
    int MediaLinksWritten,
    IReadOnlyList<TorrentPersistError> Errors)
{
    public static TorrentPersistResult Empty { get; } = new(0, 0, 0, []);
    public bool Succeeded => Errors.Count == 0;
}

public sealed record TorrentPersistError(
    string Code,
    string Tracker,
    string SourceKey,
    string Message);

public sealed record TorrentCatalogQuery(
    long? TmdbId = null,
    string? Title = null,
    int Limit = 500);
