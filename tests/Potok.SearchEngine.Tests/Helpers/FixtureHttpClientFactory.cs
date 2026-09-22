namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>Single HttpClient over the given handler for every named client.</summary>
public sealed class FixtureHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    private readonly HttpClient _client = new(handler);

    public HttpClient CreateClient(string name) => _client;
}
