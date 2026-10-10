using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.Aniliberty;

/// <summary>
///     Aniliberty JSON-API adapter: one full-object release search, then one torrent-list
///     request per unique playable anime release (at most four in flight). A failed release
///     API only drops that release; the search throws a typed
///     <see cref="TrackerSearchException"/> when the global surface is broken or every
///     release request failed. Torrents whose explicit hash conflicts with the magnet hash
///     are discarded. The API already returns complete objects, so no topic-page fetch is
///     required (<see cref="TrackerDetailsState.NotRequired"/>).
/// </summary>
public class AnilibertySearch : BaseTrackerSearch
{
    static AnilibertySearch()
    {
        // BaseTrackerSearch resolves windows-1251 in a static field initializer that runs
        // before its own RegisterProvider; register here so the base static init cannot fail.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private const string ParserVersion = "aniliberty/2026-10-07";
    private const int MaxConcurrentReleaseRequests = 4;

    private static readonly HashSet<string> PlayableAnimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TV", "TV_SHORT", "MOVIE", "OVA", "ONA", "SPECIAL"
    };

    public AnilibertySearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Aniliberty;
    public override string TrackerName => "aniliberty";
    public override string Host => "https://aniliberty.top";

    public override async Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Config.Aniliberty.EnableSearch)
            return [];

        var releases = await SearchReleasesAsync(query, ct);
        if (releases.Count == 0)
            return [];

        var results = new ConcurrentBag<TorrentDetails>();
        var failures = 0;
        await Parallel.ForEachAsync(
            releases,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentReleaseRequests, CancellationToken = ct },
            async (release, token) =>
            {
                IReadOnlyCollection<TorrentDetails> mapped;
                try
                {
                    mapped = await FetchReleaseTorrentsAsync(release, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                foreach (var torrent in mapped)
                    results.Add(torrent);
            });

        if (failures == releases.Count)
            throw new TrackerSearchException(
                Tracker,
                TrackerSearchErrorCode.InvalidResponse,
                "Every Aniliberty release torrents request failed.");

        return results
            .OrderByDescending(torrent => torrent.Sid)
            .ThenByDescending(torrent => torrent.Pir)
            .ThenBy(torrent => torrent.Source?.SourceKey, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<ReleaseEntry>> SearchReleasesAsync(string query, CancellationToken ct)
    {
        var url = $"{Host}/api/v1/app/search/releases?query={Uri.EscapeDataString(query)}";
        string body;
        try
        {
            body = await HttpService.GetStringAsync(url, ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TrackerSearchException(
                Tracker, TrackerSearchErrorCode.Transport, "Aniliberty release search request failed.", ex);
        }

        var classified = TrackerResponseClassifier.Classify(body);
        if (classified is not null)
            throw new TrackerSearchException(Tracker, classified.Value, "Aniliberty release search is unavailable.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new TrackerSearchException(
                Tracker, TrackerSearchErrorCode.InvalidResponse, "Aniliberty release search returned invalid JSON.", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new TrackerSearchException(
                    Tracker, TrackerSearchErrorCode.InvalidResponse, "Aniliberty release search returned a non-array body.");

            var releases = new List<ReleaseEntry>();
            var seen = new HashSet<int>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("id", out var idElement) ||
                    !idElement.TryGetInt32(out var id) || id <= 0 ||
                    !seen.Add(id) ||
                    !IsPlayableAnime(element))
                    continue;

                releases.Add(new ReleaseEntry(
                    id,
                    element.TryGetProperty("year", out var year) && year.TryGetInt32(out var parsedYear)
                        ? parsedYear
                        : 0,
                    element.TryGetProperty("alias", out var alias) ? alias.GetString() : null,
                    ReadName(element, "main"),
                    ReadName(element, "english"),
                    (Dictionary<string, object?>)ToPlainObject(element)!));
            }

            return releases;
        }
    }

    private async Task<IReadOnlyCollection<TorrentDetails>> FetchReleaseTorrentsAsync(
        ReleaseEntry release, CancellationToken ct)
    {
        var url = $"{Host}/api/v1/anime/torrents/release/{release.Id}";
        var body = await HttpService.GetStringAsync(url, ct: ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new TrackerSearchException(
                Tracker, TrackerSearchErrorCode.InvalidResponse,
                $"Aniliberty release {release.Id} torrents request failed.");

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new TrackerSearchException(
                Tracker, TrackerSearchErrorCode.InvalidResponse,
                $"Aniliberty release {release.Id} torrents returned a non-array body.");

        var results = new List<TorrentDetails>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var torrent = MapTorrent(release, element);
            if (torrent is not null)
                results.Add(torrent);
        }

        return results;
    }

    private TorrentDetails? MapTorrent(ReleaseEntry release, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt32(out var torrentId) || torrentId <= 0)
            return null;

        var magnetRaw = element.TryGetProperty("magnet", out var magnetElement) &&
                        magnetElement.ValueKind == JsonValueKind.String
            ? magnetElement.GetString()
            : null;
        var explicitHashRaw = element.TryGetProperty("hash", out var hashElement) &&
                              hashElement.ValueKind == JsonValueKind.String
            ? hashElement.GetString()
            : null;

        var magnetHash = MagnetBuilder.HashFromMagnet(magnetRaw);
        var explicitHash = MagnetBuilder.NormalizeHash(explicitHashRaw);
        if (magnetHash is not null && explicitHash is not null &&
            !string.Equals(magnetHash, explicitHash, StringComparison.Ordinal))
            return null;

        var infoHash = explicitHash ?? magnetHash;
        if (infoHash is null)
            return null;

        var magnet = MagnetBuilder.Build(infoHash, sourceMagnets: [magnetRaw]);
        if (magnet is null)
            return null;

        var warnings = new List<string>();
        if (explicitHashRaw is not null && explicitHash is null)
            warnings.Add("explicit_hash_invalid");
        if (magnetRaw is null)
            warnings.Add("magnet_missing");

        long size = 0;
        var seeders = 0;
        var leechers = 0;
        var hasSize = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out size);
        var hasSeeders = element.TryGetProperty("seeders", out var seedersElement) &&
                         seedersElement.TryGetInt32(out seeders);
        var hasLeechers = element.TryGetProperty("leechers", out var leechersElement) &&
                          leechersElement.TryGetInt32(out leechers);
        var qualityRaw = ReadNestedValue(element, "quality");
        var quality = StringConvert.ParseQuality(qualityRaw);
        var videoType = ReadNestedValue(element, "type");
        var createdAt = ReadDate(element, "created_at");
        var updatedAt = ReadDate(element, "updated_at");

        var observed = TorrentObservedFields.InfoHash | TorrentObservedFields.Magnet | TorrentObservedFields.Title |
                       TorrentObservedFields.Types;
        if (createdAt is not null) observed |= TorrentObservedFields.PublishDate;
        if (hasSize) observed |= TorrentObservedFields.Size;
        if (hasSeeders) observed |= TorrentObservedFields.Seeders;
        if (hasLeechers) observed |= TorrentObservedFields.Leechers;
        if (!string.IsNullOrWhiteSpace(release.Name)) observed |= TorrentObservedFields.Names;
        if (release.Year > 0) observed |= TorrentObservedFields.ReleaseYear;
        if (quality > 0) observed |= TorrentObservedFields.Quality;
        if (!string.IsNullOrWhiteSpace(videoType)) observed |= TorrentObservedFields.VideoType;
        if (updatedAt is not null) observed |= TorrentObservedFields.SourceUpdatedAt;

        var details = (Dictionary<string, object?>)ToPlainObject(element)!;
        // The only secret vector in Aniliberty API objects is the raw magnet (passkey tr);
        // replace it with the canonical rebuilt magnet before the payload is stored. The
        // fragments go in as JsonElement so TrackerPayload.Sanitize leaves benign keys like
        // "added_in_users_favorites" (substring-matched by its sensitive-key regex) intact.
        details["magnet"] = magnet;
        var detailsPayload = JsonSerializer.SerializeToElement(details);
        var listPayload = JsonSerializer.SerializeToElement(release.List);

        var sourceUrl =
            $"{Host}/anime/releases/release/{release.Alias ?? release.Id.ToString()}#torrent-{torrentId}";
        var name = string.IsNullOrWhiteSpace(release.Name)
            ? ReadLabel(element) ?? $"Torrent {torrentId}"
            : release.Name;
        var now = DateTimeOffset.UtcNow;

        return new TorrentDetails
        {
            TrackerName = TrackerName,
            Types = ["anime"],
            Url = sourceUrl,
            Title = name!,
            Name = release.Name,
            OriginalName = release.OriginalName,
            Sid = hasSeeders ? seeders : 0,
            Pir = hasLeechers ? leechers : 0,
            Size = hasSize ? size : 0,
            SizeName = hasSize && size > 0 ? TrackerText.FormatSize(size) : null,
            CreateTime = createdAt?.UtcDateTime ?? now.UtcDateTime,
            UpdateTime = updatedAt?.UtcDateTime ?? createdAt?.UtcDateTime ?? now.UtcDateTime,
            Magnet = magnet,
            InfoHash = infoHash,
            ReleaseYear = release.Year,
            Quality = quality,
            VideoType = videoType,
            Source = new TorrentSourceSnapshot(
                Tracker,
                $"{release.Id}:{torrentId}",
                sourceUrl,
                observed,
                TrackerDetailsState.NotRequired,
                TrackerPayload.SchemaVersion,
                ParserVersion,
                TrackerPayload.Create(ParserVersion, listPayload, detailsPayload, TrackerPayload.EmptyObject, warnings),
                now,
                now,
                updatedAt)
        };
    }

    private static bool IsPlayableAnime(JsonElement release)
    {
        var type = ReadNestedValue(release, "type");
        return type is null || PlayableAnimeTypes.Contains(type);
    }

    private static string? ReadName(JsonElement release, string key) =>
        release.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.Object &&
        name.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedValue(JsonElement element, string key) =>
        element.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadLabel(JsonElement element) =>
        element.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String
            ? label.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static object? ToPlainObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => ToPlainObject(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlainObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private sealed record ReleaseEntry(
        int Id,
        int Year,
        string? Alias,
        string? Name,
        string? OriginalName,
        Dictionary<string, object?> List);
}
