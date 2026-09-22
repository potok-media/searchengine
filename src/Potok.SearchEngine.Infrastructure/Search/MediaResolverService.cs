using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;

namespace Potok.SearchEngine.Infrastructure.Search;

public class MediaResolverService : IMediaResolverService
{
    private readonly ICacheService _cacheService;
    private readonly Config _config;
    private readonly TrackerHttpClient _httpService;

    public MediaResolverService(
        ICacheService cacheService,
        TrackerHttpClient httpService,
        IOptions<Config> config)
    {
        _cacheService = cacheService;
        _httpService = httpService;
        _config = config.Value;
    }

    public async Task<(string? search, string? altname)> ResolveKpImdb(string? search, string? altname)
    {
        if (string.IsNullOrWhiteSpace(search))
            return (search, altname);

        var trimmedSearch = search.Trim();
        if (!Regex.IsMatch(trimmedSearch, "^((tt|kp)[0-9]+|[0-9]+)$"))
            return (search, altname);

        var cacheKey = CacheKeyBuilder.Build("api", "v1.0", "torrents", trimmedSearch);
        var cache = await _cacheService.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                if (string.IsNullOrWhiteSpace(_config.MediaResolver.AllohaToken))
                    return ((string?)null, (string?)null);

                string uri;
                if (trimmedSearch.StartsWith("kp"))
                    uri = $"&kp={trimmedSearch[2..]}";
                else if (trimmedSearch.StartsWith("tt"))
                    uri = $"&imdb={trimmedSearch}";
                else
                    uri = $"&tmdb={trimmedSearch}";

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var json = await _httpService.GetStringAsync(
                        $"https://api.alloha.tv/?token={_config.MediaResolver.AllohaToken}{uri}",
                        ct: cts.Token);

                    if (string.IsNullOrWhiteSpace(json)) return (null, null);

                    using var document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty("data", out var data) ||
                        data.ValueKind != JsonValueKind.Object)
                        return (null, null);

                    return (GetString(data, "original_name"), GetString(data, "name"));
                }
                catch
                {
                    return (null, null);
                }
            },
            TimeSpan.FromMinutes(_config.Cache.Expiry));

        return !string.IsNullOrWhiteSpace(cache.Item1) && !string.IsNullOrWhiteSpace(cache.Item2)
            ? (cache.Item1, cache.Item2)
            : (cache.Item1 ?? cache.Item2, altname);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
