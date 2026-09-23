using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.MegaPeer;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class MegaPeerContractTests
{
    [Fact]
    public async Task Video_category_lists_fetch_every_unique_topic_and_return_only_playable_release_facts()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(_ => Html1251(Fixture("search-list.html")));
        for (var i = 2; i < 6; i++)
            handler.Enqueue(_ => Html1251("<div id=\"index\"><table></table></div>"));
        handler.Enqueue(_ => Html1251(Fixture("topic-complete.html")));
        handler.Enqueue(_ => Html1251(Fixture("topic-complete.html")));
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(2, results.Count);
        Assert.Equal(8, handler.RequestUrls.Count);
        Assert.Contains("%C4", handler.RequestUrls.First());
        Assert.DoesNotContain("%D0%94", handler.RequestUrls.First(), StringComparison.OrdinalIgnoreCase);
        foreach (var category in new[] { 80, 79, 5, 6, 55, 76 })
            Assert.Single(handler.RequestUrls, url => url.Contains($"cat={category}&", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("cat=0&", StringComparison.Ordinal));
        Assert.Single(handler.RequestUrls, url => url.Contains("/torrent/175063/", StringComparison.Ordinal));
        Assert.Single(handler.RequestUrls, url => url.Contains("/torrent/146895/", StringComparison.Ordinal));

        var complete = results.Single(result => result.Source!.SourceKey == "175063");
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", complete.InfoHash);
        Assert.Contains("xt=urn:btih:7CC6F866595149DE96DAD57F566AD1481F6837E3", complete.Magnet);
        Assert.Equal("Дюна: Часть вторая", complete.Name);
        Assert.Equal("Dune: Part Two", complete.OriginalName);
        Assert.Equal(2024, complete.ReleaseYear);
        Assert.Equal(1080, complete.Quality);
        Assert.Equal("Matroska", complete.VideoType);
        Assert.Equal(9_035_640_990, complete.Size);
        Assert.Equal(104, complete.Sid);
        Assert.Equal(11, complete.Pir);
        Assert.Equal(["movie"], complete.Types!);
        Assert.Contains("Русский", complete.Languages!);
        Assert.Contains("Английский", complete.Languages!);
        Assert.Contains("Дублированный (Bravo Records Georgia / Movie Dubbing)", complete.Voices!);

        var source = complete.Source!;
        Assert.Equal(TrackerType.Megapeer, source.Tracker);
        Assert.Equal("https://megapeer.vip/torrent/175063/duna-chast-vtoraya_dune-part-two-2024-web-dl-1080p", source.SourceUrl);
        Assert.Equal(TrackerDetailsState.Fetched, source.DetailsState);
        Assert.NotNull(source.DetailsFetchedAt);
        Assert.Null(source.SourceUpdatedAt);
        Assert.Equal(1, source.PayloadSchemaVersion);
        Assert.Equal("megapeer/2026-09-22.2", source.ParserVersion);
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
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("list").GetProperty("comments").ValueKind);
        Assert.Equal("Dune.Part.Two.2024.WEB.DL.1080p.torrent",
            payload.GetProperty("details").GetProperty("downloadFileName").GetString());
        var facts = payload.GetProperty("details").GetProperty("facts");
        foreach (var label in new[]
                 {
                     "Название", "Оригинальное название", "Год выпуска", "Жанр", "Выпущено", "Режиссер",
                     "В ролях", "Описание", "Продолжительность", "Качество видео", "Перевод", "Субтитры",
                     "Видео", "Аудио 1", "Аудио 2"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing MegaPeer fact: {label}");
        Assert.Equal("Зарубежные фильмы", payload.GetProperty("details").GetProperty("metadata")
            .GetProperty("Категория").GetString());
        Assert.False(payload.GetProperty("details").GetProperty("metadata").TryGetProperty("Сид был", out _));
        Assert.False(payload.GetProperty("details").GetProperty("facts").TryGetProperty("Transfer speed", out _));
        Assert.Equal("9.1 / 10", payload.GetProperty("details").GetProperty("metadata")
            .GetProperty("Оценка").GetString());
        Assert.DoesNotContain("29 августа 2026", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("30 августа 2026", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("18 MB/s", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("4 часа", payload.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("https://imdb.com/title/tt15239678",
            payload.GetProperty("details").GetProperty("externalLinks").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains("https://images.example.test/dune-poster.jpg",
            payload.GetProperty("details").GetProperty("images").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal("MediaInfo", payload.GetProperty("details").GetProperty("hiddenSections")[0]
            .GetProperty("title").GetString());
        Assert.Equal("Скриншоты:", payload.GetProperty("details").GetProperty("hiddenSections")[1]
            .GetProperty("title").GetString());
        Assert.Equal("Кино, Видео и TV",
            payload.GetProperty("unmapped").GetProperty("categoryBreadcrumbs")[0].GetString());
        var rawPayload = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret-cookie", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-user", rawPayload, StringComparison.Ordinal);
        Assert.Contains("sensitive_fact_redacted", payload.GetProperty("warnings").EnumerateArray()
            .Select(value => value.GetString()));

        var second = results.Single(result => result.Source!.SourceKey == "146895");
        Assert.Equal(TrackerDetailsState.Fetched, second.Source!.DetailsState);
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", second.InfoHash);
        Assert.NotNull(second.Magnet);
        Assert.True(second.Source.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        Assert.Equal(7, second.Source.SourcePayload.GetProperty("list").GetProperty("comments").GetInt32());
    }

    [Fact]
    public async Task One_topic_failure_is_skipped_without_discarding_another_playable_result()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        EnqueueVideoLists(handler, Fixture("search-list.html"));
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(_ => Html1251(Fixture("topic-complete.html")));
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        var playable = Assert.Single(results);
        Assert.Equal(TrackerDetailsState.Fetched, playable.Source!.DetailsState);
        Assert.NotNull(playable.InfoHash);
        Assert.NotNull(playable.Magnet);
        Assert.Equal(8, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Invalid_detail_magnets_are_not_returned()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        EnqueueVideoLists(handler, Fixture("search-list.html"));
        handler.Enqueue(_ => Html1251(Fixture("topic-invalid-magnet.html")));
        handler.Enqueue(_ => Html1251(Fixture("topic-invalid-magnet.html")));
        ITrackerSearch tracker = CreateTracker(handler);

        Assert.Empty(await tracker.SearchAsync("Дюна Dune"));
        Assert.Equal(8, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Hash_mismatch_and_non_video_topic_are_not_returned()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        EnqueueVideoLists(handler, Fixture("search-list.html"));
        var mismatch = Fixture("topic-complete.html").Replace(
            "<b>Название:</b>",
            "<b>Инфо-хеш:</b> 0123456789abcdef0123456789abcdef01234567<br /><b>Название:</b>",
            StringComparison.Ordinal);
        var nonVideo = Fixture("topic-complete.html")
            .Replace("/cat/80", "/cat/94", StringComparison.Ordinal)
            .Replace("Зарубежные фильмы", "Музыка", StringComparison.Ordinal);
        handler.Enqueue(_ => Html1251(mismatch));
        handler.Enqueue(_ => Html1251(nonVideo));
        ITrackerSearch tracker = CreateTracker(handler);

        Assert.Empty(await tracker.SearchAsync("Дюна Dune"));
        Assert.Equal(8, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Empty_list_response_throws_typed_invalid_response_error()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        for (var i = 0; i < 6; i++)
            handler.Enqueue(_ => Html1251(string.Empty));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Megapeer, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var handler = new ConcurrentScriptedHttpMessageHandler();
        ITrackerSearch tracker = CreateTracker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.SearchAsync("Дюна Dune", cts.Token));
    }

    [Fact]
    public async Task All_unique_topics_are_fetched_with_at_most_four_concurrent_requests()
    {
        var handler = new BoundedTopicHandler(
            VideoList(5),
            Fixture("topic-complete.html"));
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(5, results.Count);
        Assert.Equal(6, handler.ListRequests);
        Assert.Equal(5, handler.TopicRequests);
        Assert.Equal(4, handler.MaxConcurrentTopics);
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler) => new MegaPeerSearch(
        Options.Create(EnabledConfig()),
        TrackerTestClients.CreateHttpClient(handler, EnabledConfig()),
        new FixtureCache());

    private static void EnqueueVideoLists(ConcurrentScriptedHttpMessageHandler handler, string firstCategoryHtml)
    {
        handler.Enqueue(_ => Html1251(firstCategoryHtml));
        for (var i = 1; i < 6; i++)
            handler.Enqueue(_ => Html1251("<div id=\"index\"><table></table></div>"));
    }

    private static Config EnabledConfig() => new()
    {
        MegaPeer = new() { EnableSearch = true }
    };

    private static string VideoList(int count)
    {
        var rows = string.Join("", Enumerable.Range(1, count).Select(index => $"""
            <tr class="table_fon">
              <td>15 Апр 24</td>
              <td colspan="2"><a class="url" href="/torrent/{200000 + index}/video-{index}">Видео {index} / Video {index} (2024) WEB-DL 1080p</a></td>
              <td>1.00 GB</td>
              <td><font color="#008000">{100 - index}</font><font color="#8b0000">0</font></td>
            </tr>
            """));
        return $"<div id=\"index\"><table>{rows}</table></div>";
    }

    private static HttpResponseMessage Html1251(string html)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var content = new ByteArrayContent(Encoding.GetEncoding("windows-1251").GetBytes(html));
        content.Headers.ContentType = new("text/html") { CharSet = "windows-1251" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string Fixture(string name) => TrackerTestClients.ReadFixture("MegaPeer", name);

    private sealed class ConcurrentScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _scripts = new();
        private readonly ConcurrentQueue<string> _requestUrls = new();

        public IReadOnlyCollection<string> RequestUrls => _requestUrls.ToArray();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) => _scripts.Enqueue(respond);

        public void EnqueueStatus(HttpStatusCode status) =>
            Enqueue(_ => new HttpResponseMessage(status));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestUrls.Enqueue(request.RequestUri!.ToString());
            if (!_scripts.TryDequeue(out var script))
                throw new InvalidOperationException($"No scripted response for {request.RequestUri}");
            return Task.FromResult(script(request));
        }
    }

    private sealed class BoundedTopicHandler(string firstListHtml, string topicHtml) : HttpMessageHandler
    {
        private int _activeTopics;
        private int _listRequests;
        private int _maxConcurrentTopics;
        private int _topicRequests;

        public int ListRequests => _listRequests;
        public int TopicRequests => _topicRequests;
        public int MaxConcurrentTopics => _maxConcurrentTopics;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listRequests);
                return Html1251(url.Contains("cat=80", StringComparison.Ordinal)
                    ? firstListHtml
                    : "<div id=\"index\"><table></table></div>");
            }

            Interlocked.Increment(ref _topicRequests);
            var active = Interlocked.Increment(ref _activeTopics);
            UpdateMaximum(ref _maxConcurrentTopics, active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return Html1251(topicHtml);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTopics);
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
