using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.NNMClub;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class NNMClubContractTests
{
    [Fact]
    public async Task Windows_1251_list_fetches_every_allowed_topic_once_and_preserves_release_facts()
    {
        var router = new NnmClubRouter(
            Fixture("search-list.html"),
            _ => Html1251(Fixture("topic-complete.html")));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(2, results.Count);
        Assert.Equal(3, router.Handler.RequestUrls.Count);
        Assert.Contains("nm=%c4%fe%ed%e0+Dune", router.PostBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, router.Handler.RequestUrls.Count(url => url.EndsWith("?t=1716311", StringComparison.Ordinal)));
        Assert.Equal(1, router.Handler.RequestUrls.Count(url => url.EndsWith("?t=1761100", StringComparison.Ordinal)));
        Assert.DoesNotContain(router.Handler.RequestUrls, url => url.EndsWith("?t=1999999", StringComparison.Ordinal));

        var complete = results.Single(result => result.Source!.SourceKey == "1716311");
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", complete.InfoHash);
        Assert.Contains("xt=urn:btih:7CC6F866595149DE96DAD57F566AD1481F6837E3", complete.Magnet);
        Assert.Equal("Дюна: Часть вторая", complete.Name);
        Assert.Equal("Dune: Part Two", complete.OriginalName);
        Assert.Equal(2024, complete.ReleaseYear);
        Assert.Equal(1080, complete.Quality);
        Assert.Equal("WEB-DL", complete.VideoType);
        Assert.Equal(9_035_640_990, complete.Size);
        Assert.Equal(66, complete.Sid);
        Assert.Equal(0, complete.Pir);
        Assert.Equal(["movie"], Assert.IsType<string[]>(complete.Types));
        Assert.Contains("Русский", complete.Languages!);
        Assert.Contains("Английский", complete.Languages!);
        Assert.Contains("Дублированный", complete.Voices!);

        var source = complete.Source!;
        Assert.Equal(TrackerType.NNMClub, source.Tracker);
        Assert.Equal("https://nnmclub.to/forum/viewtopic.php?t=1716311", source.SourceUrl);
        Assert.Equal(TrackerDetailsState.Fetched, source.DetailsState);
        Assert.NotNull(source.DetailsFetchedAt);
        Assert.Equal(1, source.PayloadSchemaVersion);
        Assert.Equal("nnmclub/2026-09-22", source.ParserVersion);
        foreach (var field in new[]
                 {
                     TorrentObservedFields.InfoHash, TorrentObservedFields.Magnet, TorrentObservedFields.Title,
                     TorrentObservedFields.Size, TorrentObservedFields.Seeders, TorrentObservedFields.Leechers,
                     TorrentObservedFields.PublishDate, TorrentObservedFields.Names, TorrentObservedFields.ReleaseYear,
                     TorrentObservedFields.Types, TorrentObservedFields.Quality, TorrentObservedFields.VideoType,
                     TorrentObservedFields.Languages, TorrentObservedFields.Voices
                 })
            Assert.True(source.ObservedFields.HasFlag(field), $"Missing observed field: {field}");

        var payload = source.SourcePayload;
        Assert.Equal("RGplanB", payload.GetProperty("list").GetProperty("author").GetString());
        Assert.Equal("Bravo Records Georgia", payload.GetProperty("list").GetProperty("uploaderGroup").GetString());
        Assert.Equal(19_464, payload.GetProperty("list").GetProperty("views").GetInt32());
        Assert.Equal(21, payload.GetProperty("list").GetProperty("replies").GetInt32());
        Assert.False(payload.GetProperty("list").TryGetProperty("transferSpeed", out _));
        var facts = payload.GetProperty("details").GetProperty("facts");
        foreach (var label in new[]
                 {
                     "Название", "Оригинальное название", "Год выпуска", "Жанр", "Выпущено", "Режиссер",
                     "В ролях", "Описание", "Продолжительность", "Перевод", "Субтитры", "Качество видео",
                     "Видео", "Аудио 1", "Аудио 2"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing NNMClub fact: {label}");
        Assert.Contains("https://www.imdb.com/title/tt15239678/",
            payload.GetProperty("details").GetProperty("externalLinks").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal("MediaInfo", payload.GetProperty("details").GetProperty("hiddenSections")[0]
            .GetProperty("title").GetString());
        Assert.Equal("Видео. Кино, Театр, Муз.видео",
            payload.GetProperty("unmapped").GetProperty("breadcrumbs")[0].GetString());
        var rawPayload = payload.GetRawText();
        Assert.DoesNotContain("3.02 MB/s", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Сид был", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Сидер замечен", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Upload speed", rawPayload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-secret", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-user", rawPayload, StringComparison.Ordinal);

        var serial = results.Single(result => result.Source!.SourceKey == "1761100");
        Assert.Equal(TrackerDetailsState.Fetched, serial.Source!.DetailsState);
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", serial.InfoHash);
        Assert.True(serial.Source.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        Assert.Equal(3, serial.Pir);
        Assert.Equal(["serial"], Assert.IsType<string[]>(serial.Types));
    }

    [Fact]
    public async Task Failed_topic_is_skipped_without_discarding_another_valid_topic()
    {
        var router = new NnmClubRouter(
            Fixture("search-list.html"),
            url => url.Query.Contains("1761100", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Html1251(Fixture("topic-complete.html")));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        var valid = Assert.Single(results);
        Assert.Equal("1716311", valid.Source!.SourceKey);
        Assert.Equal(TrackerDetailsState.Fetched, valid.Source.DetailsState);
        Assert.Equal(3, router.Handler.RequestUrls.Count);
        Assert.Equal(1, router.Handler.RequestUrls.Count(url => url.EndsWith("?t=1716311", StringComparison.Ordinal)));
        Assert.Equal(1, router.Handler.RequestUrls.Count(url => url.EndsWith("?t=1761100", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Invalid_detail_magnets_are_skipped_instead_of_returning_list_only_rows()
    {
        var router = new NnmClubRouter(
            Fixture("search-list.html"),
            _ => Html1251(Fixture("topic-invalid-magnet.html")));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Equal(3, router.Handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Explicit_hash_mismatch_is_skipped()
    {
        var mismatch = Fixture("topic-complete.html").Replace(
            "data-hash=\"7CC6F866595149DE96DAD57F566AD1481F6837E3\"",
            "data-hash=\"0123456789ABCDEF0123456789ABCDEF01234567\"",
            StringComparison.Ordinal);
        var router = new NnmClubRouter(Fixture("search-list.html"), _ => Html1251(mismatch));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Equal(3, router.Handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Empty_topic_responses_produce_a_healthy_empty_result()
    {
        var router = new NnmClubRouter(Fixture("search-list.html"), _ => Html1251(string.Empty));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Equal(3, router.Handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Non_video_forum_is_filtered_before_topic_requests()
    {
        var router = new NnmClubRouter(
            ListWithOnlyTopics("1999999"),
            _ => throw new InvalidOperationException("A non-video topic must not be fetched."));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Single(router.Handler.RequestUrls);
    }

    [Fact]
    public async Task Topic_fetches_are_bounded_to_four_and_each_unique_topic_is_requested_once()
    {
        var router = new NnmClubRouter(
            ListWithAllowedTopicCopies(6),
            _ => Html1251(Fixture("topic-complete.html")),
            topicDelay: TimeSpan.FromMilliseconds(60));
        ITrackerSearch tracker = CreateTracker(router.Handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(6, results.Count);
        Assert.Equal(7, router.Handler.RequestUrls.Count);
        Assert.Equal(4, router.Handler.MaxConcurrentProbedRequests);
        Assert.All(router.Handler.RequestUrls.Where(url => url.Contains("viewtopic.php", StringComparison.Ordinal))
            .GroupBy(url => url), group => Assert.Single(group));
    }

    [Fact]
    public async Task Authentication_page_throws_typed_authentication_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => Html1251(Fixture("search-authentication.html")));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.NNMClub, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.Authentication, error.Code);
    }

    [Fact]
    public async Task Challenge_page_throws_typed_challenge_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => Html1251(Fixture("search-challenge.html")));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.NNMClub, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.Challenge, error.Code);
    }

    [Fact]
    public async Task Empty_list_response_throws_typed_invalid_response_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => Html1251(string.Empty));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.NNMClub, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var handler = new ScriptedHttpMessageHandler();
        ITrackerSearch tracker = CreateTracker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.SearchAsync("Дюна Dune", cts.Token));
    }

    private static string ListWithOnlyTopics(params string[] topicIds)
    {
        var document = new HtmlParser().ParseDocument(Fixture("search-list.html"));
        var allowed = topicIds.ToHashSet(StringComparer.Ordinal);
        foreach (var row in document.QuerySelectorAll("tr.prow1, tr.prow2").ToArray())
        {
            var href = row.QuerySelector("a[href*='viewtopic.php?t=']")?.GetAttribute("href") ?? string.Empty;
            if (!allowed.Any(id => href.Contains($"t={id}", StringComparison.Ordinal)))
                row.Remove();
        }
        return document.DocumentElement.OuterHtml;
    }

    private static string ListWithAllowedTopicCopies(int count)
    {
        var document = new HtmlParser().ParseDocument(ListWithOnlyTopics("1716311"));
        var body = document.QuerySelector("tbody")!;
        var template = body.QuerySelector("tr")!.OuterHtml;
        body.InnerHtml = string.Concat(Enumerable.Range(0, count).Select(index => template
            .Replace("1716311", (1_716_311 + index).ToString(), StringComparison.Ordinal)
            .Replace("1318327", (1_318_327 + index).ToString(), StringComparison.Ordinal)));
        return document.DocumentElement.OuterHtml;
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler) => new NNMClubSearch(
        Options.Create(EnabledConfig()),
        TrackerTestClients.CreateHttpClient(handler, EnabledConfig()),
        new FixtureCache());

    private static Config EnabledConfig() => new()
    {
        NNMClub = new() { EnableSearch = true }
    };

    private static HttpResponseMessage Html1251(string html)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var content = new ByteArrayContent(Encoding.GetEncoding("windows-1251").GetBytes(html));
        content.Headers.ContentType = new("text/html") { CharSet = "windows-1251" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string Fixture(string name) => TrackerTestClients.ReadFixture("NNMClub", name);

    /// <summary>
    ///     Routes the search POST to the list fixture and every topic GET to the scripted
    ///     response, capturing the POST body; topic URLs are concurrency-probed.
    /// </summary>
    private sealed class NnmClubRouter
    {
        private readonly string _listHtml;
        private readonly Func<Uri, HttpResponseMessage> _topicResponse;
        private readonly TimeSpan? _topicDelay;

        public NnmClubRouter(
            string listHtml,
            Func<Uri, HttpResponseMessage> topicResponse,
            TimeSpan? topicDelay = null)
        {
            _listHtml = listHtml;
            _topicResponse = topicResponse;
            _topicDelay = topicDelay;
            Handler = new RoutingHttpMessageHandler(RouteAsync, "viewtopic.php");
        }

        public RoutingHttpMessageHandler Handler { get; }
        public string PostBody { get; private set; } = string.Empty;

        private async Task<HttpResponseMessage> RouteAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostBody = await request.Content!.ReadAsStringAsync(ct);
                return Html1251(_listHtml);
            }

            if (_topicDelay is { } delay)
                await Task.Delay(delay, ct);
            return _topicResponse(request.RequestUri!);
        }
    }
}
