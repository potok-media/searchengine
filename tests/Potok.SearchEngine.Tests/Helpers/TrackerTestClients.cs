using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Potok.SearchEngine.Core.Models.Options;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Http.FlareSolverr;

namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>
///     Shared wiring for tracker contract tests: a TrackerHttpClient over a scripted handler,
///     an HTML response helper with the browser-fetched marker, and fixture file loading from
///     tests/Potok.SearchEngine.Tests/Fixtures/Trackers/&lt;Tracker&gt;/.
/// </summary>
public static class TrackerTestClients
{
    /// <summary>TrackerHttpClient with no proxies, no FlareSolverr and a stub Cloudflare guard.</summary>
    public static TrackerHttpClient CreateHttpClient(HttpMessageHandler handler, Config? config = null)
    {
        var monitor = new StaticOptionsMonitor<Config>(config ?? new Config());
        return new TrackerHttpClient(
            new FixtureHttpClientFactory(handler),
            monitor,
            new CloudflareGuard(monitor, NullLogger<CloudflareGuard>.Instance),
            new NoFlareSolverrClient(),
            new TrackerProxyPool(monitor),
            NullLogger<TrackerHttpClient>.Instance);
    }

    /// <summary>200 text/html response marked as browser-fetched (skips encoding re-reads).</summary>
    public static HttpResponseMessage Html(string html)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
        };
        response.Headers.TryAddWithoutValidation("X-Potok-Browser", "1");
        return response;
    }

    /// <summary>Reads Fixtures/Trackers/&lt;tracker&gt;/&lt;name&gt; relative to the test project.</summary>
    public static string ReadFixture(string tracker, string name) =>
        File.ReadAllText(Path.Combine(FixturesRoot(), tracker, name));

    private static string FixturesRoot() =>
        Path.Combine(Path.GetDirectoryName(HelpersDirectory())!, "..", "Fixtures", "Trackers");

    private static string HelpersDirectory([CallerFilePath] string sourceFile = "") => sourceFile;
}
