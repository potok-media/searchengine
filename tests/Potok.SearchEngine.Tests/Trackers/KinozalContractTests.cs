using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.Kinozal;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class KinozalContractTests
{
    [Fact]
    public async Task Video_categories_fetch_each_unique_topic_and_service_details_once_and_return_only_playable_facts()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("search-list.html")));
            if (url.Contains("details.php?id=", StringComparison.Ordinal) &&
                !url.Contains("get_srv_details.php", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("topic-complete.html")));
            if (url.Contains("get_srv_details.php", StringComparison.Ordinal))
            {
                var second = url.Contains("id=2002", StringComparison.Ordinal);
                var hash = second
                    ? "0123456789ABCDEF0123456789ABCDEF01234567"
                    : "7CC6F866595149DE96DAD57F566AD1481F6837E3";
                var fileName = second ? "Attack.on.Titan.S01.torrent" : "Dune.Part.Two.2024.WEB-DL.1080p.torrent";
                return Task.FromResult(ServiceResponse(hash, fileName));
            }
            throw new InvalidOperationException($"Unexpected Kinozal request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(2, results.Count);
        Assert.Equal(5, handler.RequestUrls.Count);
        Assert.Single(handler.RequestUrls, url => url.Contains("browse.php?", StringComparison.Ordinal));
        Assert.Contains("s=%C4%FE%ED%E0+Dune", handler.RequestUrls.First(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%D0%94", handler.RequestUrls.First(), StringComparison.OrdinalIgnoreCase);
        foreach (var id in new[] { "2001", "2002" })
        {
            Assert.Single(handler.RequestUrls, url => url == $"https://kinozal.guru/details.php?id={id}");
            Assert.Single(handler.RequestUrls, url =>
                url == $"https://kinozal.guru/get_srv_details.php?id={id}&action=2");
        }
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("id=3001", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("id=4001", StringComparison.Ordinal));

        var result = results.Single(item => item.Source!.SourceKey == "2001");
        Assert.Equal("https://kinozal.guru/details.php?id=2001", result.Url);
        Assert.Equal("Дюна: Часть вторая / Dune: Part Two (2024) WEB-DL 1080p", result.Title);
        Assert.Equal("7cc6f866595149de96dad57f566ad1481f6837e3", result.InfoHash);
        Assert.Equal("magnet:?xt=urn:btih:7cc6f866595149de96dad57f566ad1481f6837e3", result.Magnet);
        Assert.Equal("Дюна: Часть вторая", result.Name);
        Assert.Equal("Dune: Part Two", result.OriginalName);
        Assert.Equal(2024, result.ReleaseYear);
        Assert.Equal(1080, result.Quality);
        Assert.Equal("Matroska", result.VideoType);
        Assert.Equal(9_035_640_990, result.Size);
        Assert.Equal(104, result.Sid);
        Assert.Equal(0, result.Pir);
        Assert.Equal(["movie"], result.Types!);
        Assert.Contains("Русский", result.Languages!);
        Assert.Contains("Английский", result.Languages!);
        Assert.Contains("Дублированный", result.Voices!);

        var source = result.Source!;
        Assert.Equal(TrackerDetailsState.Fetched, source.DetailsState);
        Assert.Equal("kinozal/2026-09-22", source.ParserVersion);
        Assert.NotNull(source.DetailsFetchedAt);
        Assert.True(source.ObservedFields.HasFlag(TorrentObservedFields.InfoHash));
        Assert.True(source.ObservedFields.HasFlag(TorrentObservedFields.Magnet));
        Assert.True(source.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        var payload = source.SourcePayload;
        Assert.Equal(12, payload.GetProperty("list").GetProperty("comments").GetInt32());
        Assert.Equal("4 МБ", payload.GetProperty("details").GetProperty("service")
            .GetProperty("pieceSize").GetString());
        Assert.Equal("Dune.Part.Two.2024.WEB-DL.1080p.torrent",
            payload.GetProperty("details").GetProperty("downloadFileName").GetString());
        var facts = payload.GetProperty("details").GetProperty("facts");
        foreach (var label in new[]
                 {
                     "Название", "Оригинальное название", "Год выпуска", "Жанр", "Выпущено", "Режиссер",
                     "В ролях", "Описание", "Продолжительность", "Качество видео", "Контейнер", "Перевод",
                     "Субтитры", "Видео", "Аудио 1", "Аудио 2", "Оценка", "Просмотров"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing Kinozal fact: {label}");
        Assert.False(facts.TryGetProperty("Сидер замечен", out _));
        Assert.False(facts.TryGetProperty("Transfer rate", out _));
        Assert.False(facts.TryGetProperty("Время онлайн", out _));
        Assert.False(facts.TryGetProperty("Cookie", out _));
        Assert.Equal("MediaInfo", payload.GetProperty("details").GetProperty("hiddenSections")[0]
            .GetProperty("title").GetString());
        Assert.Contains("https://www.imdb.com/title/tt15239678/",
            payload.GetProperty("details").GetProperty("externalLinks").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains("https://images.example.test/dune-poster.jpg",
            payload.GetProperty("details").GetProperty("images").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal("Все фильмы", payload.GetProperty("unmapped").GetProperty("breadcrumbs")[0].GetString());
        var rawPayload = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("29 августа 2026", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("18 MB/s", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("4 часа", rawPayload, StringComparison.Ordinal);

        var anime = results.Single(item => item.Source!.SourceKey == "2002");
        Assert.Equal(["anime"], anime.Types!);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", anime.InfoHash);
        Assert.Equal(TrackerDetailsState.Fetched, anime.Source!.DetailsState);
    }

    [Fact]
    public async Task One_topic_failure_is_skipped_without_discarding_another_playable_result()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("search-list.html")));
            if (url == "https://kinozal.guru/details.php?id=2001")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (url == "https://kinozal.guru/details.php?id=2002")
                return Task.FromResult(Html(Fixture("topic-complete.html")));
            if (url.Contains("get_srv_details.php?id=2002", StringComparison.Ordinal))
                return Task.FromResult(ServiceResponse("0123456789ABCDEF0123456789ABCDEF01234567",
                    "Attack.on.Titan.S01.torrent"));
            throw new InvalidOperationException($"Unexpected Kinozal request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("2002", result.Source!.SourceKey);
        Assert.Equal(TrackerDetailsState.Fetched, result.Source.DetailsState);
        Assert.Equal(4, handler.RequestUrls.Count);
        Assert.DoesNotContain(handler.RequestUrls,
            url => url.Contains("get_srv_details.php?id=2001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_service_details_failure_is_skipped_without_discarding_another_playable_result()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("search-list.html")));
            if (url.Contains("/details.php?id=", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("topic-complete.html")));
            if (url.Contains("get_srv_details.php?id=2001", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (url.Contains("get_srv_details.php?id=2002", StringComparison.Ordinal))
                return Task.FromResult(ServiceResponse(
                    "0123456789ABCDEF0123456789ABCDEF01234567", "Attack.on.Titan.S01.torrent"));
            throw new InvalidOperationException($"Unexpected Kinozal request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("2002", result.Source!.SourceKey);
        Assert.Equal(5, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Invalid_and_mismatched_service_identities_are_not_returned()
    {
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("search-list.html")));
            if (url.Contains("/details.php?id=", StringComparison.Ordinal))
                return Task.FromResult(Html(Fixture("topic-complete.html")));
            if (url.Contains("get_srv_details.php?id=2001", StringComparison.Ordinal))
                return Task.FromResult(Html("<ul><li>Инфо хеш: not-a-valid-hash</li></ul>"));
            if (url.Contains("get_srv_details.php?id=2002", StringComparison.Ordinal))
            {
                const string mismatch = """
                    <ul>
                      <li>Инфо хеш: 0123456789ABCDEF0123456789ABCDEF01234567</li>
                      <li><a href="magnet:?xt=urn:btih:7CC6F866595149DE96DAD57F566AD1481F6837E3">Magnet</a></li>
                    </ul>
                    """;
                return Task.FromResult(Html(mismatch));
            }
            throw new InvalidOperationException($"Unexpected Kinozal request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        Assert.Empty(await tracker.SearchAsync("Дюна Dune"));
        Assert.Equal(5, handler.RequestUrls.Count);
    }

    [Theory]
    [InlineData("<html><title>Just a moment...</title><div class='cf-chl-test'></div></html>", TrackerSearchErrorCode.Challenge)]
    [InlineData("<html><form action='/takelogin.php'><input name='username'><input name='password'></form>Вход в систему</html>", TrackerSearchErrorCode.Authentication)]
    [InlineData("", TrackerSearchErrorCode.InvalidResponse)]
    [InlineData("<html><body>unexpected maintenance page</body></html>", TrackerSearchErrorCode.ParserContract)]
    public async Task Broken_global_list_surface_throws_typed_error(string html, TrackerSearchErrorCode expected)
    {
        var handler = new RoutingHttpMessageHandler((_, _) => Task.FromResult(Html(html)));
        ITrackerSearch tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Kinozal, error.Tracker);
        Assert.Equal(expected, error.Code);
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
            () => tracker.SearchAsync("Дюна Dune", cts.Token));
        Assert.Empty(handler.RequestUrls);
    }

    [Fact]
    public async Task All_unique_video_candidates_are_enriched_with_at_most_four_concurrent_requests()
    {
        var topicHtml = Fixture("topic-complete.html");
        var handler = new RoutingHttpMessageHandler(async (request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Html(VideoList(5));
            await Task.Delay(40, ct);
            if (url.Contains("get_srv_details.php", StringComparison.Ordinal))
            {
                var id = int.Parse(Regex.Match(url, @"[?&]id=(?<id>\d+)").Groups["id"].Value);
                return ServiceResponse(id.ToString("x40"), $"video-{id}.torrent");
            }
            return Html(topicHtml);
        }, concurrencyProbePathSuffix: "details.php");
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(5, results.Count);
        Assert.Single(handler.RequestUrls, url => url.Contains("browse.php?", StringComparison.Ordinal));
        Assert.Equal(5, handler.RequestUrls.Count(url => url.Contains("get_srv_details.php", StringComparison.Ordinal)));
        Assert.Equal(5, handler.RequestUrls.Count(url =>
            url.Contains("/details.php?id=", StringComparison.Ordinal)));
        Assert.Equal(4, handler.MaxConcurrentProbedRequests);
        Assert.All(results, result =>
        {
            Assert.NotNull(result.InfoHash);
            Assert.NotNull(result.Magnet);
            Assert.Equal(TrackerDetailsState.Fetched, result.Source!.DetailsState);
        });
    }

    [Fact]
    public async Task Exact_video_category_allowlist_is_typed_and_non_video_categories_are_never_fetched()
    {
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["8"] = ["movie"], ["6"] = ["movie"], ["15"] = ["movie"], ["17"] = ["movie"],
            ["35"] = ["movie"], ["39"] = ["movie"], ["13"] = ["movie"], ["14"] = ["movie"],
            ["24"] = ["movie"], ["11"] = ["movie"], ["9"] = ["movie"], ["47"] = ["movie"],
            ["12"] = ["movie"], ["10"] = ["movie"], ["7"] = ["movie"], ["16"] = ["movie"],
            ["18"] = ["documovie"], ["37"] = ["sport"], ["45"] = ["serial"], ["46"] = ["serial"],
            ["38"] = ["tvshow"], ["48"] = ["tvshow"], ["49"] = ["tvshow"], ["50"] = ["tvshow"],
            ["21"] = ["multfilm", "multserial"], ["22"] = ["multfilm", "multserial"], ["20"] = ["anime"]
        };
        var html = CategoryList(expected.Keys, ["1", "2", "3", "4", "5", "23", "32", "40", "41", "42", "999"]);
        var topicHtml = Fixture("topic-complete.html");
        var handler = new RoutingHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("browse.php?", StringComparison.Ordinal))
                return Task.FromResult(Html(html));
            if (url.Contains("get_srv_details.php", StringComparison.Ordinal))
            {
                var id = int.Parse(Regex.Match(url, @"[?&]id=(?<id>\d+)").Groups["id"].Value);
                return Task.FromResult(ServiceResponse(id.ToString("x40"), $"category-{id}.torrent"));
            }
            if (url.Contains("/details.php?id=", StringComparison.Ordinal))
                return Task.FromResult(Html(topicHtml));
            throw new InvalidOperationException($"Unexpected Kinozal request: {url}");
        });
        ITrackerSearch tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(expected.Count, results.Count);
        Assert.Equal(1 + expected.Count * 2, handler.RequestUrls.Count);
        foreach (var result in results)
        {
            var categoryId = result.Source!.SourcePayload.GetProperty("list").GetProperty("categoryId").GetString()!;
            Assert.Equal(expected[categoryId], result.Types);
        }
        foreach (var skippedId in Enumerable.Range(9001, 11))
            Assert.DoesNotContain(handler.RequestUrls, url => url.Contains($"id={skippedId}", StringComparison.Ordinal));
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler)
    {
        var config = EnabledConfig();
        return new KinozalSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new FixtureCache("fixture-session"));
    }

    private static Config EnabledConfig() => new()
    {
        Kinozal = new() { EnableSearch = true }
    };

    private static HttpResponseMessage Html(string html) => TrackerTestClients.Html(html);

    private static HttpResponseMessage ServiceResponse(string hash, string fileName)
    {
        var html = Fixture("service-details-complete.html")
            .Replace("{{HASH}}", hash, StringComparison.Ordinal)
            .Replace("{{FILE_NAME}}", fileName, StringComparison.Ordinal);
        return Html(html);
    }

    private static string Fixture(string name) => TrackerTestClients.ReadFixture("Kinozal", name);

    private static string VideoList(int count)
    {
        var rows = string.Join("", Enumerable.Range(1, count).Select(index => $"""
            <tr class="bg result-row">
              <td class="bt"><img onclick="cat(17);" alt="Драма"></td>
              <td class="nam"><a class="r1" href="/details.php?id={2100 + index}">Видео {index} / Video {index} (2024) WEB-DL 1080p</a></td>
              <td>{index}</td><td>1.00 ГБ</td><td>{100 - index}</td><td>0</td><td>15.04.2024 в 12:30</td>
            </tr>
            """));
        return $"<html><body><form action='/browse.php'></form><table><tbody>{rows}</tbody></table></body></html>";
    }

    private static string CategoryList(IEnumerable<string> allowed, IEnumerable<string> skipped)
    {
        var rows = new List<string>();
        var id = 8000;
        foreach (var category in allowed)
        {
            id++;
            rows.Add(CategoryRow(id, category));
        }
        id = 9000;
        foreach (var category in skipped)
        {
            id++;
            rows.Add(CategoryRow(id, category));
        }
        return $"<html><body><form action='/browse.php'></form><table><tbody>{string.Join("", rows)}</tbody></table></body></html>";
    }

    private static string CategoryRow(int id, string category) => $"""
        <tr class="bg">
          <td class="bt"><img onclick="cat({category});" alt="Категория {category}"></td>
          <td class="nam"><a href="/details.php?id={id}" class="r1">Видео {id} / Video {id} (2024) WEB-DL 1080p</a></td>
          <td>0</td><td>1.00 ГБ</td><td>1</td><td>0</td><td>15.04.2024 в 12:30</td>
        </tr>
        """;
}
