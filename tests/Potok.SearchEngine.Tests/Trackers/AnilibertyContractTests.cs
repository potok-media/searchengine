using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.Aniliberty;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class AnilibertyContractTests
{
    [Fact]
    public async Task Full_api_objects_are_preserved_and_only_unique_playable_anime_is_returned()
    {
        var handler = new ScenarioHandler((url, _) => Task.FromResult(
            url.Contains("/app/search/releases?", StringComparison.Ordinal)
                ? Json(TrackerTestClients.ReadFixture("Aniliberty", "search-full.json"))
                : Json(TrackerTestClients.ReadFixture("Aniliberty", "torrents-full.json"))));
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Атака титанов Attack on Titan"));

        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("include=", StringComparison.Ordinal));
        Assert.Equal("9329:22988", result.Source!.SourceKey);
        Assert.Equal("https://aniliberty.top/anime/releases/release/ijiranaide-nagatoro-san-2nd-attack#torrent-22988",
            result.Source.SourceUrl);
        Assert.Equal("72dc6256d17301d39b9052425cbdc859274d58d1", result.InfoHash);
        Assert.Equal("magnet:?xt=urn:btih:72dc6256d17301d39b9052425cbdc859274d58d1", result.Magnet);
        Assert.Equal(24_469_065_353, result.Size);
        Assert.Equal(20, result.Sid);
        Assert.Equal(0, result.Pir);
        Assert.Equal(2023, result.ReleaseYear);
        Assert.Equal(1080, result.Quality);
        Assert.Equal("WEBRip", result.VideoType);
        Assert.Equal("Не издевайся, Нагаторо: Вторая атака", result.Name);
        Assert.Equal("Ijiranaide, Nagatoro-san 2nd Attack", result.OriginalName);
        Assert.Equal(["anime"], result.Types!);
        Assert.Equal(TrackerDetailsState.NotRequired, result.Source.DetailsState);
        Assert.NotNull(result.Source.DetailsFetchedAt);
        Assert.Equal("aniliberty/2026-09-22", result.Source.ParserVersion);
        Assert.True(result.Source.ObservedFields.HasFlag(TorrentObservedFields.InfoHash));
        Assert.True(result.Source.ObservedFields.HasFlag(TorrentObservedFields.Magnet));
        Assert.True(result.Source.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        Assert.True(result.Source.ObservedFields.HasFlag(TorrentObservedFields.SourceUpdatedAt));

        var payload = result.Source.SourcePayload;
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(9329, payload.GetProperty("list").GetProperty("id").GetInt32());
        Assert.Equal(50197, payload.GetProperty("list").GetProperty("mal").GetProperty("id").GetInt32());
        Assert.Equal("16+", payload.GetProperty("list").GetProperty("age_rating").GetProperty("label").GetString());
        Assert.Equal(12, payload.GetProperty("list").GetProperty("episodes_total").GetInt32());
        Assert.Equal(6005, payload.GetProperty("list").GetProperty("added_in_users_favorites").GetInt32());
        Assert.Equal("x264/AVC", payload.GetProperty("details").GetProperty("codec").GetProperty("value").GetString());
        Assert.Equal(6622, payload.GetProperty("details").GetProperty("completed_times").GetInt32());
        Assert.Equal("voice", payload.GetProperty("details").GetProperty("torrent_members")[0]
            .GetProperty("role").GetString());
        Assert.Equal(result.Magnet, payload.GetProperty("details").GetProperty("magnet").GetString());
        var payloadText = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret", payloadText, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.example.test", payloadText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_failed_release_and_one_hash_conflict_do_not_discard_another_playable_release()
    {
        var handler = new ScenarioHandler((url, _) =>
        {
            if (url.Contains("/app/search/releases?", StringComparison.Ordinal))
                return Task.FromResult(Json(Releases(100, 200)));
            if (url.EndsWith("/100", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (url.EndsWith("/200", StringComparison.Ordinal))
            {
                const string response = """
                    [
                      {
                        "id": 1,
                        "hash": "ffffffffffffffffffffffffffffffffffffffff",
                        "magnet": "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                        "size": 1,
                        "seeders": 1,
                        "leechers": 0
                      },
                      {
                        "id": 2,
                        "hash": "0123456789abcdef0123456789abcdef01234567",
                        "magnet": "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                        "size": 2,
                        "seeders": 2,
                        "leechers": 0
                      }
                    ]
                    """;
                return Task.FromResult(Json(response));
            }
            throw new InvalidOperationException($"Unexpected Aniliberty request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Атака титанов Attack on Titan"));

        Assert.Equal("200:2", result.Source!.SourceKey);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task All_release_api_failures_are_not_reported_as_a_healthy_empty_search()
    {
        var handler = new ScenarioHandler((url, _) => Task.FromResult(
            url.Contains("/app/search/releases?", StringComparison.Ordinal)
                ? Json(Releases(100, 200))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Атака титанов Attack on Titan"));

        Assert.Equal(TrackerType.Aniliberty, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.InvalidResponse, error.Code);
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Theory]
    [InlineData("", TrackerSearchErrorCode.InvalidResponse)]
    [InlineData("not-json", TrackerSearchErrorCode.InvalidResponse)]
    [InlineData("<html><title>Just a moment...</title><div class='cf-chl-test'></div></html>", TrackerSearchErrorCode.Challenge)]
    [InlineData("{\"message\":\"Unauthenticated.\"}", TrackerSearchErrorCode.Authentication)]
    public async Task Broken_global_search_surface_throws_typed_error(string body, TrackerSearchErrorCode expected)
    {
        var handler = new ScenarioHandler((_, _) => Task.FromResult(Json(body)));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Aniliberty, error.Tracker);
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped_or_swallowed()
    {
        var handler = new ScenarioHandler((_, _) =>
            throw new InvalidOperationException("Canceled search must not issue HTTP."));
        ITrackerSearch tracker = CreateTracker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.SearchAsync("Атака титанов Attack on Titan", cts.Token));
        Assert.Empty(handler.RequestUrls);
    }

    [Fact]
    public async Task Unique_release_torrent_apis_use_at_most_four_concurrent_requests()
    {
        var handler = new BoundedReleaseHandler(6);
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Атака титанов Attack on Titan");

        Assert.Equal(6, results.Count);
        Assert.Equal(1, handler.SearchRequests);
        Assert.Equal(6, handler.TorrentRequests);
        Assert.Equal(4, handler.MaxConcurrentTorrents);
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler) => new AnilibertySearch(
        Options.Create(EnabledConfig()),
        TrackerTestClients.CreateHttpClient(handler, EnabledConfig()),
        new FixtureCache());

    private static Config EnabledConfig() => new()
    {
        Aniliberty = new() { EnableSearch = true }
    };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };

    private static string Releases(params int[] ids) => "[" + string.Join(',', ids.Select(id => $$"""
        {
          "id": {{id}},
          "type": { "value": "TV", "description": "ТВ" },
          "year": 2024,
          "name": { "main": "Release {{id}}", "english": "Release {{id}} EN" },
          "alias": "release-{{id}}"
        }
        """)) + "]";

    private static string Torrent(int releaseId) => $$"""
        [
          {
            "id": {{releaseId + 10_000}},
            "hash": "{{releaseId.ToString("x40")}}",
            "magnet": "magnet:?xt=urn:btih:{{releaseId.ToString("x40")}}&tr=https%3A%2F%2Ftracker.example.test%2Fannounce",
            "size": {{releaseId * 1000L}},
            "type": { "value": "WEBRip" },
            "quality": { "value": "1080p" },
            "seeders": 0,
            "leechers": 0,
            "created_at": "2024-01-01T00:00:00+00:00",
            "updated_at": "2024-01-02T00:00:00+00:00"
          }
        ]
        """;

    private sealed class ScenarioHandler(
        Func<string, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<string> RequestUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            RequestUrls.Enqueue(url);
            return responder(url, ct);
        }
    }

    private sealed class BoundedReleaseHandler(int releaseCount) : HttpMessageHandler
    {
        private int _active;
        private int _maxConcurrent;

        public int SearchRequests;
        public int TorrentRequests;
        public int MaxConcurrentTorrents => Volatile.Read(ref _maxConcurrent);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/app/search/releases?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref SearchRequests);
                var duplicated = Enumerable.Range(1, releaseCount).Append(1).ToArray();
                return Json(Releases(duplicated));
            }

            Interlocked.Increment(ref TorrentRequests);
            var current = Interlocked.Increment(ref _active);
            while (true)
            {
                var max = Volatile.Read(ref _maxConcurrent);
                if (current <= max || Interlocked.CompareExchange(ref _maxConcurrent, current, max) == max)
                    break;
            }

            try
            {
                await Task.Delay(40, ct);
                var releaseId = int.Parse(url[(url.LastIndexOf('/') + 1)..]);
                return Json(Torrent(releaseId));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
