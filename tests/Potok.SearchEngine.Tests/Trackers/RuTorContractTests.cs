using System.Net;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.RuTor;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class RuTorContractTests
{
    [Fact]
    public async Task Live_shaped_list_fetches_one_popular_topic_and_preserves_complete_observation()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueCategorySearches(handler, "search-list.html", failSecondCategory: true);
        handler.Enqueue(_ => TrackerTestClients.Html(TrackerTestClients.ReadFixture("RuTor", "topic-complete.html")));
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Equal(2, results.Count);
        Assert.Equal(10, handler.RequestUrls.Count);
        Assert.Equal(new[] { "1", "5", "12", "4", "16", "6", "7", "10", "13" },
            handler.RequestUrls.Take(9).Select(CategoryId));
        Assert.Equal("http://rutor.info/torrent/983790", handler.RequestUrls[9]);

        var complete = results.Single(result => result.Source!.SourceKey == "/torrent/983790");
        Assert.Equal("http://rutor.info/torrent/983790", complete.Source!.SourceUrl);
        Assert.Equal("420a1035496cdab32f06fb5bc175e8a25dd8e857", complete.InfoHash);
        Assert.Contains("xt=urn:btih:420A1035496CDAB32F06FB5BC175E8A25DD8E857", complete.Magnet);
        Assert.Equal("Дюна: Часть вторая", complete.Name);
        Assert.Equal("Dune: Part Two", complete.OriginalName);
        Assert.Equal(2024, complete.ReleaseYear);
        Assert.Equal(1080, complete.Quality);
        Assert.Equal("Matroska", complete.VideoType);
        Assert.Equal(15_347_435_004, complete.Size);
        Assert.Equal(58, complete.Sid);
        Assert.Equal(0, complete.Pir);
        Assert.Equal(["movie"], complete.Types!);
        Assert.Contains("Русский", complete.Languages!);
        Assert.Contains("Английский", complete.Languages!);
        Assert.Contains("Дублированный Bravo Records Georgia", complete.Voices!);
        Assert.Equal(TrackerDetailsState.Fetched, complete.Source.DetailsState);
        Assert.NotNull(complete.Source.DetailsFetchedAt);
        Assert.Equal(1, complete.Source.PayloadSchemaVersion);
        Assert.Equal("rutor/2026-09-22", complete.Source.ParserVersion);
        Assert.True(complete.Source.ObservedFields.HasFlag(TorrentObservedFields.InfoHash));
        Assert.True(complete.Source.ObservedFields.HasFlag(TorrentObservedFields.Magnet));
        Assert.True(complete.Source.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
        Assert.True(complete.Source.ObservedFields.HasFlag(TorrentObservedFields.Types));
        Assert.True(complete.Source.ObservedFields.HasFlag(TorrentObservedFields.Languages));

        var payload = complete.Source.SourcePayload;
        Assert.Equal(8, payload.GetProperty("list").GetProperty("comments").GetInt32());
        Assert.Equal("Зарубежные фильмы", payload.GetProperty("details").GetProperty("metadata")
            .GetProperty("Категория").GetString());
        Assert.Equal("9.1 из 10", payload.GetProperty("details").GetProperty("metadata")
            .GetProperty("Оценка").GetString());
        Assert.Equal("12045", payload.GetProperty("details").GetProperty("metadata")
            .GetProperty("Просмотров").GetString());
        var facts = payload.GetProperty("details").GetProperty("facts");
        foreach (var label in new[]
                 {
                     "Название", "Оригинальное название", "Год выпуска", "Жанр", "Выпущено", "Режиссёр",
                     "В ролях", "Описание", "Продолжительность", "Перевод(ы)", "Субтитры", "Формат",
                     "Качество", "Видео", "Аудио 1", "Аудио 2"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing Rutor fact: {label}");
        Assert.Contains("https://www.imdb.com/title/tt15239678/",
            payload.GetProperty("details").GetProperty("externalLinks").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal("MediaInfo", payload.GetProperty("details").GetProperty("hiddenSections")[0]
            .GetProperty("title").GetString());
        var rawPayload = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret-cookie", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-user", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("Сид был", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("Сидер замечен", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("Скорость раздачи", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("Скорость скачивания", rawPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("Время онлайн", rawPayload, StringComparison.Ordinal);
        Assert.Contains("sensitive_fact_redacted", payload.GetProperty("warnings").EnumerateArray()
            .Select(value => value.GetString()));

        var listOnly = results.Single(result => result.Source!.SourceKey == "/torrent/853287");
        Assert.Equal(TrackerDetailsState.NotRequired, listOnly.Source!.DetailsState);
        Assert.Equal("7c77e6dcb2468f9a955195d64cc63f0ee0e3713f", listOnly.InfoHash);
    }

    [Fact]
    public async Task Detail_failure_preserves_complete_list_observation()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueCategorySearches(handler, "search-list.html");
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        var result = results.Single(item => item.Source!.SourceKey == "/torrent/983790");
        Assert.Equal(TrackerDetailsState.Failed, result.Source!.DetailsState);
        Assert.Null(result.Source.DetailsFetchedAt);
        Assert.Equal("420a1035496cdab32f06fb5bc175e8a25dd8e857", result.InfoHash);
        Assert.NotNull(result.Magnet);
        Assert.Equal(95, result.Sid);
        Assert.Equal(0, result.Pir);
        Assert.Contains("detail_empty_response", result.Source.SourcePayload.GetProperty("warnings")
            .EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Invalid_list_magnet_is_skipped_before_any_topic_request()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueCategorySearches(handler, "search-invalid-magnet.html");
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Invalid Release");

        Assert.Empty(results);
        Assert.Equal(9, handler.RequestUrls.Count);
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("/torrent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Topic_hash_mismatch_removes_only_the_conflicting_release()
    {
        var handler = new ScriptedHttpMessageHandler();
        EnqueueCategorySearches(handler, "search-list.html");
        var mismatchedTopic = TrackerTestClients.ReadFixture("RuTor", "topic-complete.html").Replace(
            "<b>Cookie:</b>",
            "<b>Инфо-хеш:</b> ffffffffffffffffffffffffffffffffffffffff<br><b>Cookie:</b>",
            StringComparison.Ordinal);
        handler.Enqueue(_ => TrackerTestClients.Html(mismatchedTopic));
        var tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("/torrent/853287", result.Source!.SourceKey);
        Assert.Equal(TrackerDetailsState.NotRequired, result.Source.DetailsState);
        Assert.Equal(10, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Empty_list_response_throws_typed_invalid_response_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(string.Empty));
        var tracker = CreateTracker(handler);

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Rutor, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.InvalidResponse, error.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var handler = new ScriptedHttpMessageHandler();
        var tracker = CreateTracker(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tracker.SearchAsync("Дюна Dune", cts.Token));
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler) => new RuTorSearch(
        Options.Create(EnabledConfig()),
        TrackerTestClients.CreateHttpClient(handler, EnabledConfig()),
        new FixtureCache());

    private static void EnqueueCategorySearches(
        ScriptedHttpMessageHandler handler,
        string firstFixture,
        bool failSecondCategory = false)
    {
        handler.Enqueue(_ => TrackerTestClients.Html(TrackerTestClients.ReadFixture("RuTor", firstFixture)));
        if (failSecondCategory)
            handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        else
            handler.Enqueue(_ => TrackerTestClients.Html(TrackerTestClients.ReadFixture("RuTor", "search-empty.html")));
        for (var index = 2; index < 9; index++)
            handler.Enqueue(_ => TrackerTestClients.Html(TrackerTestClients.ReadFixture("RuTor", "search-empty.html")));
    }

    private static string CategoryId(string url) => new Uri(url).AbsolutePath
        .Split('/', StringSplitOptions.RemoveEmptyEntries)[2];

    private static Config EnabledConfig() => new()
    {
        RuTor = new() { EnableSearch = true }
    };
}
