using System.Net;

namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>
///     Scripted HttpMessageHandler for tracker contract tests: queued responses (or response
///     factories) plus a record of every requested URL. No real network.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scripts = new();

    public List<string> RequestUrls { get; } = [];

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) => _scripts.Enqueue(respond);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

    public void EnqueueStatus(HttpStatusCode status, string? retryAfterSeconds = null) =>
        Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (retryAfterSeconds is not null)
                response.Headers.Add("Retry-After", retryAfterSeconds);
            return response;
        });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUrls.Add(request.RequestUri!.ToString());
        if (_scripts.Count == 0)
            throw new InvalidOperationException($"No scripted response for {request.RequestUri}");
        return Task.FromResult(_scripts.Dequeue()(request));
    }
}
