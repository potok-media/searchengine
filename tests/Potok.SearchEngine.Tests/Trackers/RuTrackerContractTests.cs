using System.Net;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Trackers.RuTracker;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Trackers;

public class RuTrackerContractTests
{
    [Fact]
    public async Task Detail_failure_is_not_returned_as_a_list_only_observation()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("search-list.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-missing-magnet.html")));
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Equal(2, handler.RequestUrls.Count);
        Assert.Contains("tracker.php", handler.RequestUrls[0]);
        Assert.Contains("viewtopic.php?t=6677889", handler.RequestUrls[1]);
    }

    [Fact]
    public async Task Complete_topic_populates_hash_normalized_fields_and_zero_peers()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("search-list.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-complete.html")));
        var tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.InfoHash);
        Assert.Contains("xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567", result.Magnet);
        Assert.Equal(444, result.Sid);
        Assert.Equal(0, result.Pir);
        Assert.Equal(1080, result.Quality);
        Assert.Equal("MKV", result.VideoType);
        Assert.Equal(2021, result.ReleaseYear);
        Assert.Equal(9126805504, result.Size);
        Assert.Equal(TrackerDetailsState.Fetched, result.Source?.DetailsState);
        Assert.NotNull(result.Source?.DetailsFetchedAt);
        Assert.True(result.Source?.ObservedFields.HasFlag(TorrentObservedFields.InfoHash));
        Assert.True(result.Source?.ObservedFields.HasFlag(TorrentObservedFields.Magnet));
        Assert.True(result.Source?.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
    }

    [Fact]
    public async Task Payload_contains_all_release_facts_and_redacts_transport_secrets()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("search-list.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-complete.html")));
        var tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));
        var payload = result.Source!.SourcePayload;
        var facts = payload.GetProperty("details").GetProperty("facts");

        foreach (var label in new[]
                 {
                     "Год выпуска", "Страна", "Жанр", "Продолжительность", "Перевод", "Субтитры",
                     "Режиссёр", "Качество", "Формат видео", "Видео", "Аудио", "Размер", "Описание"
                 })
            Assert.True(facts.TryGetProperty(label, out _), $"Missing labelled release fact: {label}");
        Assert.Equal("6677889", payload.GetProperty("list").GetProperty("sourceKey").GetString());
        Assert.Contains("https://www.imdb.com/title/tt1160419/",
            payload.GetProperty("details").GetProperty("externalLinks").EnumerateArray()
                .Select(value => value.GetString()));
        var raw = payload.GetRawText();
        Assert.DoesNotContain("fixture-secret-cookie", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret-token", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-user", raw, StringComparison.Ordinal);
        Assert.False(facts.TryGetProperty("Скорость отдачи", out _));
        Assert.False(facts.TryGetProperty("Сидер замечен", out _));
        Assert.Contains("sensitive_fact_redacted",
            payload.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Search_tolerates_extra_css_classes_and_preserves_cyrillic()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("search-list.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-complete.html")));
        var tracker = CreateTracker(handler);

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.StartsWith("Площадь Дюны", result.Title, StringComparison.Ordinal);
        Assert.Equal(0, result.Pir);
        Assert.True(result.Source!.ObservedFields.HasFlag(TorrentObservedFields.Leechers));
    }

    [Fact]
    public async Task Search_fetches_every_video_topic_and_returns_only_valid_identity_results()
    {
        var handler = new RoutingHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("tracker.php", StringComparison.Ordinal))
                return TrackerTestClients.Html(Fixture("search-list-two.html"));

            await Task.Delay(25, ct);
            return TrackerTestClients.Html(Fixture(request.RequestUri.Query.Contains("6677889", StringComparison.Ordinal)
                ? "topic-complete.html"
                : "topic-missing-magnet.html"));
        }, "viewtopic.php");
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        var result = Assert.Single(results);
        Assert.Equal("6677889", result.Source!.SourceKey);
        Assert.Equal(TrackerDetailsState.Fetched, result.Source.DetailsState);
        Assert.Equal(3, handler.RequestUrls.Count);
        Assert.Contains(handler.RequestUrls, url => url.Contains("viewtopic.php?t=6677889", StringComparison.Ordinal));
        Assert.Contains(handler.RequestUrls, url => url.Contains("viewtopic.php?t=6677890", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("viewtopic.php?t=7777000", StringComparison.Ordinal));
        Assert.Equal(2, handler.MaxConcurrentProbedRequests);
    }

    [Fact]
    public async Task Mismatched_explicit_hash_and_magnet_are_not_returned()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("search-list.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-hash-mismatch.html")));
        var tracker = CreateTracker(handler);

        var results = await tracker.SearchAsync("Дюна Dune");

        Assert.Empty(results);
        Assert.Equal(2, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Signed_out_search_throws_typed_authentication_error()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("signed-out.html")));
        handler.EnqueueStatus(HttpStatusCode.Unauthorized);
        var config = EnabledConfig();
        config.RuTracker.Authorization = new() { Login = "fixture-user", Password = "fixture-pass" };
        var tracker = new RuTrackerSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new FixtureCache("fixture-session"));

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Rutracker, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.Authentication, error.Code);
        Assert.Equal(2, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Configured_cookie_is_sent_without_a_login_request()
    {
        string? sentCookie = null;
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(request =>
        {
            sentCookie = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join(",", values)
                : null;
            return TrackerTestClients.Html(Fixture("search-list.html"));
        });
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-complete.html")));
        var config = EnabledConfig();
        config.RuTracker.Authorization = new() { Cookie = "bb_session=configured-session" };
        var tracker = new RuTrackerSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new FixtureCache());

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("6677889", result.Source!.SourceKey);
        Assert.Equal("bb_session=configured-session", sentCookie);
        Assert.DoesNotContain(handler.RequestUrls, url => url.Contains("login.php", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Successful_login_strips_set_cookie_attributes_from_the_session_header()
    {
        string? sentCookie = null;
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            // The FlareSolverr shape: the browser follows the login 302 and the
            // synthesized response carries the session cookies on a 200.
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("Set-Cookie",
                "bb_session=fresh-session; path=/; domain=.rutracker.org; secure; HttpOnly");
            return response;
        });
        handler.Enqueue(request =>
        {
            sentCookie = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join(",", values)
                : null;
            return TrackerTestClients.Html(Fixture("search-list.html"));
        });
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("topic-complete.html")));
        var config = EnabledConfig();
        config.RuTracker.Authorization = new() { Login = "fixture-user", Password = "fixture-pass" };
        var tracker = new RuTrackerSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new StoringCache());

        var result = Assert.Single(await tracker.SearchAsync("Дюна Dune"));

        Assert.Equal("6677889", result.Source!.SourceKey);
        Assert.Equal("bb_session=fresh-session", sentCookie);
        Assert.Contains("login.php", handler.RequestUrls[0]);
    }

    [Fact]
    public async Task Captcha_gated_login_throws_authentication_error_naming_the_captcha()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("login-captcha.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("signed-out.html")));
        handler.Enqueue(_ => TrackerTestClients.Html(Fixture("login-captcha.html")));
        var config = EnabledConfig();
        config.RuTracker.Authorization = new() { Login = "fixture-user", Password = "fixture-pass" };
        var tracker = new RuTrackerSearch(
            Options.Create(config),
            TrackerTestClients.CreateHttpClient(handler, config),
            new FixtureCache());

        var error = await Assert.ThrowsAsync<TrackerSearchException>(
            () => tracker.SearchAsync("Дюна Dune"));

        Assert.Equal(TrackerType.Rutracker, error.Tracker);
        Assert.Equal(TrackerSearchErrorCode.Authentication, error.Code);
        Assert.Contains("captcha", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUrls.Count);
        Assert.Contains("login.php", handler.RequestUrls[0]);
    }

    private static ITrackerSearch CreateTracker(HttpMessageHandler handler) => new RuTrackerSearch(
        Options.Create(EnabledConfig()),
        TrackerTestClients.CreateHttpClient(handler, EnabledConfig()),
        new FixtureCache("fixture-session"));

    private static Config EnabledConfig() => new()
    {
        Cache = new Cache { Enable = true, AuthExpiry = 1 },
        RuTracker = new() { EnableSearch = true }
    };

    private static string Fixture(string name) => TrackerTestClients.ReadFixture("RuTracker", name);

    /// <summary>In-memory cache that actually stores, mirroring the enabled CacheService.</summary>
    private sealed class StoringCache : ICacheService
    {
        private readonly Dictionary<string, object> _values = new();

        public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null) =>
            factory();

        public Task InvalidateAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
        {
            if (value is not null)
                _values[key] = value;
            return Task.CompletedTask;
        }

        public bool TryGetValue<T>(string key, out T? value)
        {
            if (_values.TryGetValue(key, out var stored) && stored is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }
    }
}
