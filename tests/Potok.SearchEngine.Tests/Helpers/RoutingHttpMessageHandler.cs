namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>
///     Routes every request through a delegate and records all requested URLs. When
///     <paramref name="concurrencyProbePathSuffix"/> is set, requests whose path ends with it
///     are concurrency-tracked and the observed maximum is exposed — used to assert bounded
///     parallel topic enrichment.
/// </summary>
public sealed class RoutingHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route,
    string? concurrencyProbePathSuffix = null) : HttpMessageHandler
{
    private readonly object _requestLock = new();
    private int _activeProbedRequests;
    private int _maxConcurrentProbedRequests;

    public List<string> RequestUrls { get; } = [];

    public int MaxConcurrentProbedRequests => Volatile.Read(ref _maxConcurrentProbedRequests);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_requestLock)
            RequestUrls.Add(request.RequestUri!.ToString());

        var probed = concurrencyProbePathSuffix is not null &&
                     request.RequestUri!.AbsolutePath.EndsWith(
                         concurrencyProbePathSuffix, StringComparison.Ordinal);
        if (!probed)
            return await route(request, cancellationToken);

        var active = Interlocked.Increment(ref _activeProbedRequests);
        Max(ref _maxConcurrentProbedRequests, active);
        try
        {
            return await route(request, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeProbedRequests);
        }
    }

    private static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
