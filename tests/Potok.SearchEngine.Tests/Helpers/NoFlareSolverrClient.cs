using Potok.SearchEngine.Infrastructure.Http.FlareSolverr;

namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>FlareSolverr stub: never solves, reports sessions as healthy.</summary>
public sealed class NoFlareSolverrClient : IFlareSolverrClient
{
    public Task<FlareSolverrSolution?> GetAsync(string url, string? cookieHeader, FlareSolverrProxy? proxy,
        CancellationToken ct) => Task.FromResult<FlareSolverrSolution?>(null);

    public Task<FlareSolverrSolution?> PostAsync(string url, string? postData, string? cookieHeader,
        FlareSolverrProxy? proxy, CancellationToken ct) => Task.FromResult<FlareSolverrSolution?>(null);

    public Task<bool> WarmupAsync(string url, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> EnsureSessionAsync(CancellationToken ct) => Task.FromResult(true);
}
