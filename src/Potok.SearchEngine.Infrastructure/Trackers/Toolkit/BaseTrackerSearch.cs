using System.Text;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Infrastructure.Http;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Thin base for tracker adapters: config + http + cache, identity members and a
///     default empty <see cref="SearchAsync"/>. Parsing/enrichment helpers live in the
///     Toolkit statics (<see cref="TrackerText"/>, <see cref="LabelledFacts"/>,
///     <see cref="TrackerPayload"/>, <see cref="TrackerResponseClassifier"/>,
///     <see cref="DetailEnricher"/>, <see cref="SafeUrl"/>).
/// </summary>
public abstract class BaseTrackerSearch : ITrackerSearch
{
    static BaseTrackerSearch()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        RuEncoding = Encoding.GetEncoding("windows-1251");
    }

    /// <summary>
    ///     windows-1251 for legacy tracker pages (RuTracker, Kinozal, NNMClub, MegaPeer).
    ///     Initialized inside the static ctor: field initializers run before it, and
    ///     GetEncoding throws until the code-pages provider is registered.
    /// </summary>
    protected static readonly Encoding RuEncoding;

    protected readonly ICacheService CacheService;
    protected readonly Config Config;
    protected readonly TrackerHttpClient HttpService;

    protected BaseTrackerSearch(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
    {
        HttpService = httpService;
        CacheService = cacheService;
        Config = config.Value;
    }

    public abstract TrackerType Tracker { get; }
    public abstract string TrackerName { get; }
    public abstract string Host { get; }

    public virtual Task<IReadOnlyCollection<TorrentDetails>> SearchAsync(string query, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<TorrentDetails>>([]);
    }
}
