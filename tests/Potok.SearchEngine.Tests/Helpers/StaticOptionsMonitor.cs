using Microsoft.Extensions.Options;

namespace Potok.SearchEngine.Tests.Helpers;

/// <summary>IOptionsMonitor over a fixed value — no change notifications.</summary>
public sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = currentValue;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
