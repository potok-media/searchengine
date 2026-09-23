using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.AnimeLayer;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class AnimeLayerContractTests
{
    [Fact]
    public async Task Only_unique_playable_anime_is_returned_and_each_topic_is_requested_once()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(TrackerTestClients.ReadFixture("AnimeLayer", "search-list.html")));
            if (url.Contains("aaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(Topic("0123456789ABCDEF0123456789ABCDEF01234567")));
            if (url.Contains("bbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(Topic("7CC6F866595149DE96DAD57F566AD1481F6837E3")));
            throw new InvalidOperationException($"Unexpected AnimeLayer request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Атака титанов Attack on Titan");

        Assert.Equal(2, results.Count);
        Assert.Equal(3, handler.RequestUrls.Count);
        Assert.Single(handler.RequestUrls, url => url.Contains("/torrents/anime/?q=", StringComparison.Ordinal));
        Assert.Single(handler.RequestUrls, url => url.EndsWith("/torrent/aaaaaaaaaaaaaaaaaaaaaaaa/", StringComparison.Ordinal));
        Assert.Single(handler.RequestUrls, url => url.EndsWith("/torrent/bbbbbbbbbbbbbbbbbbbbbbbb/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("cccccccccccccccccccccccc", StringComparison.Ordinal));
        Assert.All(results, result =>
        {
            Assert.NotNull(result.Magnet);
            Assert.NotNull(result.InfoHash);
            Assert.Equal(["anime"], result.Types!);
            Assert.Equal(TrackerDetailsState.Fetched, result.Source!.DetailsState);
        });

        var attack = results.Single(result => result.Source!.SourceKey == "aaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", attack.InfoHash);
        Assert.Equal(12, attack.Sid);
        Assert.Equal(0, attack.Pir);
        Assert.Equal(1_610_612_736, attack.Size);
        Assert.Equal("Атака титанов", attack.Name);
        Assert.Equal("Attack on Titan", attack.OriginalName);
        Assert.Equal(2013, attack.ReleaseYear);
        Assert.Equal(1080, attack.Quality);
        Assert.Equal("mkv", attack.VideoType);
        Assert.Contains("Японский", attack.Languages!);
        Assert.Contains("Русский", attack.Languages!);
        Assert.True(attack.Source!.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        Assert.True(attack.Source.ObservedFields.HasFlag(TorrentObservedFields.InfoHash));
        Assert.Equal("animelayer/2026-09-22", attack.Source.ParserVersion);
        Assert.NotNull(attack.Source.DetailsFetchedAt);

        var payload = attack.Source.SourcePayload;
        Assert.Equal("аниме", payload.GetProperty("details").GetProperty("category").GetString());
        Assert.Equal("Attack.on.Titan.S01.torrent",
            payload.GetProperty("details").GetProperty("downloadFileName").GetString());
        Assert.Equal("MediaInfo", payload.GetProperty("details").GetProperty("hiddenSections")[0]
            .GetProperty("title").GetString());
        var facts = payload.GetProperty("details").GetProperty("facts");
        foreach (var label in new[]
                 {
                     "Страна", "Жанр", "Тип", "Год выхода", "Кол серий", "Режиссер", "Описание",
                     "Продолжительность", "Качество", "Видео", "Аудио", "Аудио 2", "Субтитры", "Формат"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing AnimeLayer fact: {label}");
        Assert.False(facts.TryGetProperty("Transfer speed", out _));
        Assert.False(facts.TryGetProperty("Cookie", out _));
        var payloadText = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret", payloadText, StringComparison.Ordinal);
        Assert.DoesNotContain("25 MB/s", payloadText, StringComparison.Ordinal);
        Assert.Contains("https://www.imdb.com/title/tt2560140/", payloadText, StringComparison.Ordinal);
        Assert.Contains("https://images.example.test/screenshot.jpg", payloadText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_unplayable_or_unavailable_topic_is_skipped_without_discarding_other_candidates()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(TrackerTestClients.ReadFixture("AnimeLayer", "search-list.html")));
            if (url.Contains("aaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (url.Contains("bbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(Topic("7CC6F866595149DE96DAD57F566AD1481F6837E3")));
            throw new InvalidOperationException($"Unexpected AnimeLayer request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Унесенные призраками Spirited Away"));

        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbb", result.Source!.SourceKey);
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", result.InfoHash);
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Anonymous_topic_without_magnet_is_skipped_while_another_candidate_remains_playable()
    {
        const string signedOutTopic = """
            <html><body><div id="wrapper">
              <div class="torrent-left"><a class="category" href="/torrents/anime/">аниме</a>
                Чтобы скачать этот торрент, вам необходимо зарегистрироваться или войти на сайт
              </div>
              <main class="torrent-info"><h1>Signed out anime</h1></main>
            </div></body></html>
            """;
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(TrackerTestClients.ReadFixture("AnimeLayer", "search-list.html")));
            if (url.Contains("aaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(signedOutTopic));
            if (url.Contains("bbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(Topic("7CC6F866595149DE96DAD57F566AD1481F6837E3")));
            throw new InvalidOperationException($"Unexpected AnimeLayer request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Атака титанов Attack on Titan"));

        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbb", result.Source!.SourceKey);
    }

    [Fact]
    public async Task Missing_invalid_and_mismatched_magnets_are_not_returned()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(TrackerTestClients.ReadFixture("AnimeLayer", "search-list.html")));
            if (url.Contains("aaaaaaaaaaaaaaaaaaaaaaaa", StringComparison.Ordinal))
                return Task.FromResult(TrackerTestClients.Html(Topic("not-a-hash")));
            if (url.Contains("bbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal))
            {
                var mismatch = Topic("7CC6F866595149DE96DAD57F566AD1481F6837E3")
                    .Replace("<div class=\"description\">",
                        "<div class=\"description\"><strong>Info hash: </strong>0123456789ABCDEF0123456789ABCDEF01234567<br>",
                        StringComparison.Ordinal);
                return Task.FromResult(TrackerTestClients.Html(mismatch));
            }
            throw new InvalidOperationException($"Unexpected AnimeLayer request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        Assert.Empty(await tracker.SearchAsync("Атака титанов Attack on Titan"));
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Theory]
    [InlineData("<html><title>Just a moment...</title><div class='cf-chl-test'></div></html>", TrackerSearchErrorCode.Challenge)]
    [InlineData("<html><form action='/auth/login/'><input name='login'><input name='password' type='password'></form></html>", TrackerSearchErrorCode.Authentication)]
    [InlineData("", TrackerSearchErrorCode.InvalidResponse)]
    [InlineData("<html><body>maintenance</body></html>", TrackerSearchErrorCode.ParserContract)]
    public async Task Broken_global_list_surface_throws_typed_error(string html, TrackerSearchErrorCode expected)
    {
        var handler = new RoutingHttpMessageHandler((_, _) =>
            Task.FromResult(TrackerTestClients.Html(html)));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.AnimeLayer, error.Tracker);
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task Valid_empty_list_is_a_healthy_empty_result()
    {
        const string empty = "<html><body><div id='wrapper'><form action='/torrents/anime/'></form><ul class='torrents-list'><li class='torrent-item'><div>Торрентов не найдено</div></li></ul></div></body></html>";
        var handler = new RoutingHttpMessageHandler((_, _) =>
            Task.FromResult(TrackerTestClients.Html(empty)));
        ITrackerSearch tracker = CreateTracker(handler);

        Assert.Empty(await tracker.SearchAsync("Дюна Dune"));
        Assert.Single(handler.RequestUrls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var handler = new RoutingHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Canceled search must not issue HTTP."));
        ITrackerSearch tracker = CreateTracker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.SearchAsync("Атака титанов Attack on Titan", cts.Token));
        Assert.Empty(handler.RequestUrls);
    }

    [Fact]
    public async Task Unique_topics_are_fetched_with_at_most_four_concurrent_requests()
    {
        var handler = new BoundedDetailHandler(VideoList(6));
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Атака титанов Attack on Titan");

        Assert.Equal(6, results.Count);
        Assert.Equal(1, handler.ListRequests);
        Assert.Equal(6, handler.TopicRequests);
        Assert.Equal(4, handler.MaxConcurrentTopics);
        Assert.All(results, result => Assert.Equal(TrackerDetailsState.Fetched, result.Source!.DetailsState));
    }

    [Fact]
    public async Task Response_charset_is_honored_for_legacy_windows_1251_pages()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
                return Task.FromResult(Html1251(TrackerTestClients.ReadFixture("AnimeLayer", "search-list.html")));
            if (url.Contains("/torrent/", StringComparison.Ordinal))
                return Task.FromResult(Html1251(Topic(url.Contains("aaaaaaaa", StringComparison.Ordinal)
                    ? "0123456789ABCDEF0123456789ABCDEF01234567"
                    : "7CC6F866595149DE96DAD57F566AD1481F6837E3")));
            throw new InvalidOperationException($"Unexpected AnimeLayer request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Атака титанов Attack on Titan");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("Атака титанов", result.Name));
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler)
    {
        var config = EnabledConfig();
        return new AnimeLayerSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new FixtureCache("fixture-session"));
    }

    private static Config EnabledConfig() => new()
    {
        AnimeLayer = new() { EnableSearch = true }
    };

    private static HttpResponseMessage Html1251(string html)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var content = new ByteArrayContent(Encoding.GetEncoding("windows-1251").GetBytes(html));
        content.Headers.ContentType = new("text/html") { CharSet = "windows-1251" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static string Topic(string hash) => TrackerTestClients
        .ReadFixture("AnimeLayer", "topic-complete.html")
        .Replace("{{HASH}}", hash, StringComparison.Ordinal);

    private static string VideoList(int count)
    {
        var items = string.Join("", Enumerable.Range(1, count).Select(index => $"""
            <li class="torrent-item torrent-item-medium panel">
              <h3><a href="/torrent/{index.ToString("x24")}/">Anime {index} / Аниме {index} [ТВ]</a></h3>
              <div class="info"><i class="icon s-icons-upload"></i>{100 - index} <i class="icon s-icons-download"></i>0 1.00 GB</div>
              <a href="/torrents/anime/" class="category">аниме</a>
            </li>
            """));
        return $"<html><body><div id='wrapper'><form action='/torrents/anime/'></form><ul class='torrents-list'>{items}</ul></div></body></html>";
    }

    private sealed class BoundedDetailHandler(string listHtml) : HttpMessageHandler
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
            if (url.Contains("/torrents/anime/?q=", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listRequests);
                return TrackerTestClients.Html(listHtml);
            }

            var active = Interlocked.Increment(ref _activeTopics);
            UpdateMaximum(ref _maxConcurrentTopics, active);
            try
            {
                await Task.Delay(40, cancellationToken);
                Interlocked.Increment(ref _topicRequests);
                var id = url.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
                var hash = id.PadLeft(40, '0')[^40..];
                return TrackerTestClients.Html(Topic(hash));
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
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
