using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Http.FlareSolverr;
using Potok.SearchEngine.Tests.Helpers;
using Xunit;

namespace Potok.SearchEngine.Tests.Http;

/// <summary>
///     A status-ok FlareSolverr answer whose body is still the IUAM challenge page means
///     the solve raced the request (warmup in progress). The client must settle and retry
///     once in the same session instead of handing the challenge page to the adapter.
/// </summary>
public class FlareSolverrChallengeRetryTests
{
    private const string ChallengeHtml =
        "<html><title>Just a moment...</title><div class=\"challenge-platform\"></div></html>";
    private const string RealHtml = "<html><body>real content</body></html>";

    [Fact]
    public async Task Challenge_body_is_retried_once_and_real_content_is_returned()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("{\"status\":\"ok\"}"); // sessions.create
        handler.EnqueueJson(SolutionJson(ChallengeHtml));
        handler.EnqueueJson(SolutionJson(RealHtml));
        var client = CreateClient(handler);

        var solution = await client.GetAsync("https://rutracker.org/forum/tracker.php?nm=test", null, null, CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(RealHtml, solution!.Html);
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Persistent_challenge_is_returned_for_the_adapter_to_classify()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("{\"status\":\"ok\"}"); // sessions.create
        handler.EnqueueJson(SolutionJson(ChallengeHtml));
        handler.EnqueueJson(SolutionJson(ChallengeHtml));
        var client = CreateClient(handler);

        var solution = await client.GetAsync("https://rutracker.org/forum/tracker.php?nm=test", null, null, CancellationToken.None);

        Assert.NotNull(solution);
        Assert.Equal(ChallengeHtml, solution!.Html);
        Assert.Equal(3, handler.RequestUrls.Count);
    }

    [Fact]
    public async Task Warmup_does_not_count_a_challenge_body_as_warm()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("{\"status\":\"ok\"}"); // sessions.create
        handler.EnqueueJson(SolutionJson(ChallengeHtml));
        handler.EnqueueJson(SolutionJson(ChallengeHtml)); // settle retry
        var client = CreateClient(handler);

        Assert.False(await client.WarmupAsync("https://rutracker.org/", CancellationToken.None));
    }

    [Fact]
    public async Task Warmup_accepts_a_real_page()
    {
        var handler = new ScriptedHttpMessageHandler();
        handler.EnqueueJson("{\"status\":\"ok\"}"); // sessions.create
        handler.EnqueueJson(SolutionJson(RealHtml));
        var client = CreateClient(handler);

        Assert.True(await client.WarmupAsync("https://rutracker.org/", CancellationToken.None));
        Assert.Equal(2, handler.RequestUrls.Count);
    }

    private static string SolutionJson(string html)
    {
        var escaped = JsonEncodedText.Encode(html).ToString();
        return "{\"status\":\"ok\",\"solution\":{\"status\":200,\"response\":\"" + escaped + "\",\"cookies\":[]}}";
    }

    private static FlareSolverrClient CreateClient(HttpMessageHandler handler)
    {
        var config = new Config
        {
            FlareSolverr = new FlareSolverrSettings
            {
                Enable = true,
                Url = "http://flaresolverr.test/v1",
                MaxTimeoutMs = 55_000,
                SessionIdleMinutes = 0
            }
        };
        var monitor = new StaticOptionsMonitor<Config>(config);
        return new FlareSolverrClient(
            new FixtureHttpClientFactory(handler),
            monitor,
            new TrackerProxyPool(monitor),
            NullLogger<FlareSolverrClient>.Instance);
    }
}
